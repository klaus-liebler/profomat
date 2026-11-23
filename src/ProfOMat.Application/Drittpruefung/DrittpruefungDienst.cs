using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ProfOMat.Application.Betrieb;
using ProfOMat.Application.Signaturen;
using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Drittpruefung;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Katalog;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Drittpruefung;

/// <summary>
/// Anmeldung mündlicher Drittprüfungen: Antrag der Studierenden → Bestätigung per Mail → Festlegung und Unterschrift der
/// Erstprüfenden → Bestellung durch das Studiendekanat → PDF an das Studierendensekretariat.
/// Jede Unterschrift wird hier geprüft (Text, Kette, Signatur bzw. Bild) und vom Server versiegelt.
/// </summary>
public sealed class DrittpruefungDienst(
    IDaten daten,
    IAktuellerBenutzer aktuell,
    ISystemsiegel siegel,
    IAntragPdf pdf,
    Postausgang post,
    Protokoll protokoll,
    Ortszeit ortszeit,
    IOptions<ProfOMatOptionen> optionen,
    TimeProvider uhr,
    Menschenpruefung menschenpruefung)
{
    /// <summary>Höchstens so viele unbestätigte Anträge je Mailadresse und Tag (Missbrauchsschutz des öffentlichen Formulars).</summary>
    public const int MaxUnbestaetigtJeMailUndTag = 3;

    /// <summary>So lange darf zwischen Anzeigen und Absenden einer Unterschrift liegen.</summary>
    public static readonly TimeSpan Signierfenster = TimeSpan.FromMinutes(30);

    private ProfOMatOptionen O => optionen.Value;

    // =====================================================================================================
    // Studierende (ohne Anmeldung; Identität über die bestätigte Mailadresse, später über das Hochschulsystem)
    // =====================================================================================================

    /// <summary>Prüft die Angaben und erzeugt den Text, den die oder der Studierende unterschreibt.</summary>
    public async Task<Antragsentwurf> EntwurfAsync(Antragsangaben angaben, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(angaben);
        if (angaben.Beanstandung() is { } fehler)
        {
            throw new FachlicherFehler(fehler);
        }

        var a = angaben.Bereinigt();
        if (!MaildomainErlaubt(a.Email))
        {
            throw new FachlicherFehler($"Bitte die Hochschul-Mailadresse verwenden ({string.Join(", ", O.StudierendenMailDomains)}).");
        }

        var id = Ids.Neu();
        var zeitpunkt = Signaturinhalt.Kappen(uhr.GetUtcNow());
        var nummer = $"DP-{ortszeit.Lokal(zeitpunkt).Year}-{id.ToString("N")[^6..].ToUpperInvariant()}";
        var text = await AntragstextFuerEntwurfAsync(id, nummer, zeitpunkt, a, abbruch);
        return new Antragsentwurf(id, nummer, zeitpunkt, a,
            Signaturinhalt.Erstellen(Signaturschritt.Antragstellung, text, null, null, $"{a.Vorname} {a.Nachname}", zeitpunkt));
    }

    /// <summary>
    /// Reicht den unterschriebenen Antrag ein und schickt den Bestätigungslink. Liefert die Antragsnummer.
    /// Öffentlich ohne Anmeldung: nur mit gelöster Sicherheitsaufgabe (<see cref="Menschenpruefung"/>) und begrenzt je Mailadresse.
    /// </summary>
    public async Task<string> EinreichenAsync(Antragsentwurf entwurf, Unterschrift unterschrift, Rechenloesung? sicherheit, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(entwurf);
        ArgumentNullException.ThrowIfNull(unterschrift);
        menschenpruefung.Pruefen(sicherheit);
        ImSignierfenster(entwurf.Zeitpunkt);
        var a = entwurf.Angaben.Bereinigt();
        if (!MaildomainErlaubt(a.Email) || a.Beanstandung() is not null)
        {
            throw new FachlicherFehler("Die Angaben sind unvollständig. Bitte das Formular erneut prüfen.");
        }

        var seit = uhr.GetUtcNow().AddDays(-1);
        if (await daten.AnzahlAsync(daten.Menge<Antrag>().Where(x => x.Email == a.Email && x.Status == AntragStatus.WartetAufBestaetigung && x.Erstellt > seit),
                abbruch) >= MaxUnbestaetigtJeMailUndTag)
        {
            throw new FachlicherFehler($"Für diese Mailadresse liegen schon {MaxUnbestaetigtJeMailUndTag} unbestätigte Anträge vor. Bitte zuerst den Link "
                                       + "in der Bestätigungsmail anklicken (auch im Spam-Ordner nachsehen).");
        }

        // Text serverseitig neu bilden – unterschrieben gilt nur, was der Server selbst so erzeugt.
        var text = Signaturinhalt.Erstellen(Signaturschritt.Antragstellung,
            await AntragstextFuerEntwurfAsync(entwurf.Id, entwurf.Nummer, entwurf.Zeitpunkt, a, abbruch), null, null, $"{a.Vorname} {a.Nachname}", entwurf.Zeitpunkt);
        if (text != entwurf.Text)
        {
            throw new FachlicherFehler("Die Angaben haben sich nach dem Anzeigen geändert. Bitte erneut unterschreiben.");
        }

        if (await daten.FindenAsync<Antrag>(entwurf.Id, abbruch) is not null)
        {
            throw new FachlicherFehler("Dieser Antrag wurde bereits eingereicht.");
        }

        var signatur = SignaturBilden(entwurf.Id, Signaturschritt.Antragstellung, entwurf.Zeitpunkt, $"{a.Vorname} {a.Nachname}", null, text, null, unterschrift, null);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var antrag = new Antrag(entwurf.Id, entwurf.Nummer, a, entwurf.Zeitpunkt, TokenHash(token), signatur);
        daten.Hinzufuegen(antrag);

        var ansicht = await AnsichtBildenAsync(antrag, pruefen: false, abbruch);
        var (betreff, mail) = Mailtexte.Bestaetigen(ansicht, StudierendenLink(antrag.Id, token), O.BestaetigungsfristTage);
        post.Einreihen(antrag.Email, betreff, mail, antrag.Id);
        protokoll.Eintragen(Benutzer.Niemand, "Drittpruefung.Eingereicht", $"Antrag {antrag.Nummer} eingereicht ({unterschrift.Art.Anzeigename()})", antrag.Id);
        await daten.SpeichernAsync(abbruch);
        return antrag.Nummer;
    }

    /// <summary>Mailadresse bestätigt (Link aus der Mail) → Antrag geht an die Erstprüfenden.</summary>
    public async Task<AntragAnsicht?> BestaetigenAsync(Guid antragId, string token, CancellationToken abbruch = default)
    {
        var antrag = await PerTokenAsync(antragId, token, abbruch);
        if (antrag is null)
        {
            return null;
        }

        if (antrag.Status == AntragStatus.WartetAufBestaetigung)
        {
            antrag.EmailBestaetigen(uhr.GetUtcNow());
            var ansicht = await AnsichtBildenAsync(antrag, pruefen: false, abbruch);
            var (betreff, text) = Mailtexte.AnErstpruefende(ansicht);
            post.Einreihen(ansicht.Erstpruefende.Email, betreff, text, antrag.Id);
            protokoll.Eintragen(Benutzer.Niemand, "Drittpruefung.Bestaetigt", $"Antrag {antrag.Nummer}: Mailadresse bestätigt, an Erstprüfende weitergeleitet", antrag.Id);
            await daten.SpeichernAsync(abbruch);
        }

        return await AnsichtBildenAsync(antrag, pruefen: true, abbruch);
    }

    /// <summary>Statusseite der Studierenden (geheimer Link).</summary>
    public async Task<AntragAnsicht?> StudierendenAnsichtAsync(Guid antragId, string token, CancellationToken abbruch = default)
        => await PerTokenAsync(antragId, token, abbruch) is { } antrag ? await AnsichtBildenAsync(antrag, pruefen: true, abbruch) : null;

    public async Task ZurueckziehenAsync(Guid antragId, string token, CancellationToken abbruch = default)
    {
        var antrag = await PerTokenAsync(antragId, token, abbruch) ?? throw new FachlicherFehler("Antrag nicht gefunden.");
        var warBeimPruefer = antrag.Status is AntragStatus.BeimErstpruefer or AntragStatus.BeimStudiendekanat;
        antrag.Zurueckziehen(uhr.GetUtcNow());
        var ansicht = await AnsichtBildenAsync(antrag, pruefen: false, abbruch);
        if (warBeimPruefer)
        {
            var (betreff, text) = Mailtexte.Zurueckgezogen(ansicht, ansicht.Erstpruefende.Anzeigename);
            post.Einreihen(ansicht.Erstpruefende.Email, betreff, text, antrag.Id);
        }

        protokoll.Eintragen(Benutzer.Niemand, "Drittpruefung.Zurueckgezogen", $"Antrag {antrag.Nummer} von der/dem Studierenden zurückgezogen", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task<Dokument?> PdfFuerStudierendeAsync(Guid antragId, string token, CancellationToken abbruch = default)
        => await PerTokenAsync(antragId, token, abbruch) is { Status: AntragStatus.Abgeschlossen } antrag ? await PdfDokumentAsync(antrag.Id, abbruch) : null;

    // =====================================================================================================
    // Beschäftigte (angemeldet)
    // =====================================================================================================

    public async Task<IReadOnlyList<AntragZeile>> ListeAsync(AntragFilter filter, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var ich = wer.KontoIdPflicht;
        var abfrage = daten.Menge<Antrag>().AsQueryable();
        if (!Rechte.AlleAntraegeSehen(wer))
        {
            abfrage = abfrage.Where(a => a.ErstpruefendeKontoId == ich || a.ZweitpruefendeKontoId == ich);
        }

        if (filter != AntragFilter.Alle)
        {
            abfrage = abfrage.Where(a => a.Status == AntragStatus.BeimErstpruefer || a.Status == AntragStatus.BeimStudiendekanat
                                         || a.Status == AntragStatus.WartetAufBestaetigung);
        }

        var antraege = await daten.ListeAsync(abfrage.OrderByDescending(a => a.Id).Take(500), abbruch);
        var zeilen = await ZeilenAsync(antraege, wer, abbruch);
        return filter == AntragFilter.MeineAufgaben ? zeilen.Where(z => z.WartetAufMich).ToList() : zeilen;
    }

    public async Task<AntragAnsicht> AnsichtAsync(Guid antragId, CancellationToken abbruch = default)
    {
        var (_, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        return await AnsichtBildenAsync(antrag, pruefen: true, abbruch);
    }

    /// <summary>Was die angemeldete Person mit dem Antrag gerade tun darf (für Schaltflächen mit Begründung).</summary>
    public async Task<AntragAktionen> AktionenAsync(Guid antragId, CancellationToken abbruch = default)
    {
        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        var ich = wer.KontoIdPflicht;
        var dekanat = wer.Hat(Rolle.Studiendekanat);
        var unterschreiben = antrag.KannFestlegen(ich).Und(() => antrag.AktuelleFestlegung is null
            ? Freigabe.Nein("Bitte zuerst Termin, Ort und Zweitprüfende speichern.")
            : antrag.Termin <= uhr.GetUtcNow() ? Freigabe.Nein("Der gespeicherte Termin liegt in der Vergangenheit.") : Freigabe.Ja);
        return new AntragAktionen(
            antrag.KannFestlegen(ich),
            unterschreiben,
            antrag.KannGenehmigen(ich, dekanat),
            antrag.KannAblehnen(ich, dekanat),
            antrag.Status == AntragStatus.Abgeschlossen ? Freigabe.Ja : Freigabe.Nein("Das PDF entsteht, sobald das Studiendekanat unterschrieben hat."));
    }

    /// <summary>Termin, Ort und Zweitprüfende vormerken (nach Rücksprache; noch ohne Unterschrift).</summary>
    public async Task FestlegungSpeichernAsync(Guid antragId, Festlegung festlegung, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(festlegung);
        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        if (festlegung.ZweitpruefendeKontoId is { } zweitId)
        {
            var zweit = await daten.FindenAsync<Konto>(zweitId, abbruch) ?? throw new FachlicherFehler("Zweitprüfende/r nicht gefunden.");
            festlegung = festlegung with { ZweitpruefendeName = zweit.Anzeigename, ZweitpruefendeEmail = zweit.Email };
        }

        antrag.FestlegungVormerken(wer.KontoIdPflicht, festlegung, uhr.GetUtcNow());
        protokoll.Eintragen(wer, "Drittpruefung.Festlegung", $"Antrag {antrag.Nummer}: Termin und Ort vorgemerkt", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Text für die Unterschrift der Erstprüfenden bzw. des Studiendekanats.</summary>
    public async Task<Signiervorlage> VorlageAsync(Guid antragId, Signaturschritt schritt, CancellationToken abbruch = default)
    {
        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        switch (schritt)
        {
            case Signaturschritt.Erstpruefung:
                antrag.KannFestlegen(wer.KontoIdPflicht).Sicherstellen();
                break;
            case Signaturschritt.Studiendekanat:
                antrag.KannGenehmigen(wer.KontoIdPflicht, wer.Hat(Rolle.Studiendekanat)).Sicherstellen();
                break;
            default:
                throw new FachlicherFehler("Diesen Schritt unterschreiben die Studierenden.");
        }

        var zeitpunkt = Signaturinhalt.Kappen(uhr.GetUtcNow());
        return new Signiervorlage(antrag.Id, schritt, await TextAsync(antrag, schritt, wer.Name, zeitpunkt, abbruch), zeitpunkt,
            antrag.LetzteGueltigeSignatur?.InhaltHash, wer.Name);
    }

    /// <summary>Erstprüfende unterschreiben; der Antrag geht an das Studiendekanat, die Studierenden erfahren den vorläufigen Termin.</summary>
    public async Task ErstpruefungUnterschreibenAsync(Guid antragId, DateTimeOffset zeitpunkt, string angezeigterText, Unterschrift unterschrift,
        CancellationToken abbruch = default)
    {
        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        var konto = await daten.FindenAsync<Konto>(wer.KontoIdPflicht, abbruch) ?? throw new FachlicherFehler("Konto nicht gefunden.");
        antrag.KannFestlegen(konto.Id).Sicherstellen();
        var signatur = await PruefenUndBildenAsync(antrag, Signaturschritt.Erstpruefung, zeitpunkt, angezeigterText, unterschrift, konto, abbruch);

        var ansichtVorher = await AnsichtBildenAsync(antrag, pruefen: false, abbruch);
        var zustaendig = ansichtVorher.Studiengang.StudiendekanatKontoId ?? ansichtVorher.Fakultaet?.StudiendekanatKontoId;
        if (zustaendig == antrag.ErstpruefendeKontoId || zustaendig == antrag.ZweitpruefendeKontoId)
        {
            zustaendig = null;   // wer prüft, bestellt sich nicht selbst → jede/r aus dem Studiendekanat
        }

        antrag.ErstpruefungUnterschreiben(konto.Id, signatur, zustaendig);
        var ansicht = await AnsichtBildenAsync(antrag, pruefen: false, abbruch);
        foreach (var dekanat in await StudiendekanatEmpfaengerAsync(antrag, zustaendig, abbruch))
        {
            var (betreff, text) = Mailtexte.AnStudiendekanat(ansicht, dekanat.Anzeigename);
            post.Einreihen(dekanat.Email, betreff, text, antrag.Id);
        }

        var (b2, t2) = Mailtexte.TerminAnStudierende(ansicht);
        post.Einreihen(antrag.Email, b2, t2, antrag.Id);

        protokoll.Eintragen(wer, "Drittpruefung.Erstpruefung", $"Antrag {antrag.Nummer}: Festlegung unterschrieben ({unterschrift.Art.Anzeigename()}), an Studiendekanat", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Studiendekanat unterschreibt: PDF erzeugen, an das Studierendensekretariat und alle Beteiligten schicken.</summary>
    public async Task GenehmigenAsync(Guid antragId, DateTimeOffset zeitpunkt, string angezeigterText, Unterschrift unterschrift, CancellationToken abbruch = default)
    {
        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        var konto = await daten.FindenAsync<Konto>(wer.KontoIdPflicht, abbruch) ?? throw new FachlicherFehler("Konto nicht gefunden.");
        antrag.KannGenehmigen(konto.Id, konto.Hat(Rolle.Studiendekanat)).Sicherstellen();
        var signatur = await PruefenUndBildenAsync(antrag, Signaturschritt.Studiendekanat, zeitpunkt, angezeigterText, unterschrift, konto, abbruch);
        var jetzt = uhr.GetUtcNow();
        antrag.StudiendekanatUnterschreiben(konto.Id, signatur, jetzt);

        var ansicht = await AnsichtBildenAsync(antrag, pruefen: true, abbruch);
        if (!ansicht.KetteInOrdnung)
        {
            throw new FachlicherFehler("Die Unterschriftenkette ist nicht in Ordnung. Bitte die Systemverwaltung informieren.");
        }

        var inhalt = pdf.Erzeugen(ansicht);
        var dokument = new Dokument(antrag.Id, $"Drittpruefung-{antrag.Nummer}.pdf", "application/pdf", inhalt, Signaturinhalt.Hash(inhalt), jetzt);
        daten.Hinzufuegen(dokument);

        var sekretariat = ansicht.Fakultaet?.SekretariatEmail ?? O.SekretariatEmail;
        var (betreff, text) = Mailtexte.AnSekretariat(ansicht);
        post.Einreihen(sekretariat, betreff, text, antrag.Id, dokument.Id);

        var (b2, t2) = Mailtexte.AbgeschlossenAnBeteiligte(ansicht, antrag.Name, null);
        post.Einreihen(antrag.Email, b2, t2, antrag.Id, dokument.Id);
        var (b3, t3) = Mailtexte.AbgeschlossenAnBeteiligte(ansicht, ansicht.Erstpruefende.Anzeigename, ansicht.Link);
        post.Einreihen(ansicht.Erstpruefende.Email, b3, t3, antrag.Id, dokument.Id);
        if (antrag.ZweitpruefendeEmail is { } zweitMail)
        {
            var (b4, t4) = Mailtexte.AbgeschlossenAnBeteiligte(ansicht, antrag.ZweitpruefendeName!, antrag.ZweitpruefendeKontoId is null ? null : ansicht.Link);
            post.Einreihen(zweitMail, b4, t4, antrag.Id, dokument.Id);
        }

        protokoll.Eintragen(wer, "Drittpruefung.Bestellt", $"Antrag {antrag.Nummer}: Prüfende bestellt ({unterschrift.Art.Anzeigename()}), PDF an {sekretariat}", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Studiendekanat gibt mit Rückfrage an die Erstprüfenden zurück.</summary>
    public async Task ZurueckgebenAsync(Guid antragId, string grund, CancellationToken abbruch = default)
    {
        if (string.IsNullOrWhiteSpace(grund))
        {
            throw new FachlicherFehler("Bitte kurz begründen, was geändert werden soll.");
        }

        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        antrag.Zurueckgeben(wer.KontoIdPflicht, grund, uhr.GetUtcNow());
        var ansicht = await AnsichtBildenAsync(antrag, pruefen: false, abbruch);
        var (betreff, text) = Mailtexte.Zurueckgegeben(ansicht, grund.Trim());
        post.Einreihen(ansicht.Erstpruefende.Email, betreff, text, antrag.Id);
        protokoll.Eintragen(wer, "Drittpruefung.Zurueckgegeben", $"Antrag {antrag.Nummer}: an Erstprüfende zurückgegeben", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task AblehnenAsync(Guid antragId, string grund, CancellationToken abbruch = default)
    {
        if (string.IsNullOrWhiteSpace(grund))
        {
            throw new FachlicherFehler("Bitte die Ablehnung begründen; die Begründung geht an die/den Studierenden.");
        }

        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        antrag.Ablehnen(wer.KontoIdPflicht, wer.Hat(Rolle.Studiendekanat), wer.Name, grund, uhr.GetUtcNow());
        var ansicht = await AnsichtBildenAsync(antrag, pruefen: false, abbruch);
        var (betreff, text) = Mailtexte.Abgelehnt(ansicht);
        post.Einreihen(antrag.Email, betreff, text, antrag.Id);
        protokoll.Eintragen(wer, "Drittpruefung.Abgelehnt", $"Antrag {antrag.Nummer} abgelehnt", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task<Dokument?> PdfAsync(Guid antragId, CancellationToken abbruch = default)
    {
        var (_, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        return await PdfDokumentAsync(antrag.Id, abbruch);
    }

    /// <summary>Nicht bestätigte Anträge verfallen lassen (Hintergrundjob).</summary>
    public async Task<int> VerfallenLassenAsync(CancellationToken abbruch = default)
    {
        var grenze = uhr.GetUtcNow().AddDays(-O.BestaetigungsfristTage);
        var offen = await daten.ListeAsync(daten.Menge<Antrag>().Where(a => a.Status == AntragStatus.WartetAufBestaetigung), abbruch);
        var faellig = offen.Where(a => a.Erstellt < grenze).ToList();
        foreach (var a in faellig)
        {
            a.Verfallen(uhr.GetUtcNow());
            protokoll.Eintragen(Benutzer.Automatik, "Drittpruefung.Verfallen", $"Antrag {a.Nummer} verfallen (nicht bestätigt)", a.Id);
        }

        await daten.SpeichernAsync(abbruch);
        return faellig.Count;
    }

    // =====================================================================================================
    // Intern
    // =====================================================================================================

    private async Task<(Benutzer Wer, Antrag Antrag)> LadenMitRechtAsync(Guid antragId, CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var antrag = await daten.FindenAsync<Antrag>(antragId, abbruch) ?? throw new FachlicherFehler("Antrag nicht gefunden.");
        var beteiligt = antrag.ErstpruefendeKontoId == wer.KontoId || antrag.ZweitpruefendeKontoId == wer.KontoId;
        if (!beteiligt && !Rechte.AlleAntraegeSehen(wer))
        {
            throw new FachlicherFehler("Diesen Antrag sehen nur die Prüfenden, das Studiendekanat und das Studierendensekretariat.");
        }

        return (wer, antrag);
    }

    private async Task<Antrag?> PerTokenAsync(Guid antragId, string token, CancellationToken abbruch)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var antrag = await daten.FindenAsync<Antrag>(antragId, abbruch);
        return antrag is not null && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(antrag.StatusTokenHash), System.Text.Encoding.ASCII.GetBytes(TokenHash(token)))
            ? antrag
            : null;
    }

    private static string TokenHash(string token) => Signaturinhalt.Hash(token);

    private string StudierendenLink(Guid id, string token) => $"{O.OeffentlicheAdresse}drittpruefung/status/{id:D}/{token}";

    private bool MaildomainErlaubt(string email)
    {
        if (O.StudierendenMailDomains.Count == 0)
        {
            return true;
        }

        var domain = email[(email.LastIndexOf('@') + 1)..].ToLowerInvariant();
        return O.StudierendenMailDomains.Any(d => domain == d.ToLowerInvariant() || domain.EndsWith("." + d.ToLowerInvariant(), StringComparison.Ordinal));
    }

    private void ImSignierfenster(DateTimeOffset zeitpunkt)
    {
        var jetzt = uhr.GetUtcNow();
        if (zeitpunkt > jetzt.AddMinutes(1) || zeitpunkt < jetzt - Signierfenster)
        {
            throw new FachlicherFehler("Die Vorlage zum Unterschreiben ist abgelaufen (30 Minuten). Bitte erneut unterschreiben.");
        }
    }

    private async Task<Antragstext> AntragstextFuerEntwurfAsync(Guid id, string nummer, DateTimeOffset zeitpunkt, Antragsangaben a, CancellationToken abbruch)
    {
        var studiengang = await daten.FindenAsync<Studiengang>(a.StudiengangId, abbruch) ?? throw new FachlicherFehler("Studiengang nicht gefunden.");
        var pruefung = await daten.FindenAsync<Pruefung>(a.PruefungId, abbruch) ?? throw new FachlicherFehler("Prüfung nicht gefunden.");
        if (pruefung.StudiengangId != studiengang.Id)
        {
            throw new FachlicherFehler("Die Prüfung gehört nicht zum gewählten Studiengang.");
        }

        var erst = await daten.FindenAsync<Konto>(a.ErstpruefendeKontoId, abbruch);
        if (erst is not { Aktiv: true } || !erst.Hat(Rolle.Pruefende))
        {
            throw new FachlicherFehler("Die gewählte Person steht als Erstprüfende/r nicht zur Verfügung.");
        }

        return new Antragstext(id, nummer, zeitpunkt, a.Vorname, a.Nachname, a.Matrikelnummer, a.Email, studiengang.Anzeige, studiengang.Schluessel,
            pruefung.Anzeige, pruefung.Schluessel, erst.Anzeigename, erst.Id, a.Terminwuensche, a.Zweitpruefungswunsch);
    }

    private async Task<string> TextAsync(Antrag antrag, Signaturschritt schritt, string unterzeichner, DateTimeOffset zeitpunkt, CancellationToken abbruch)
    {
        // Für den Text gelten die Namen zum Zeitpunkt der Antragstellung: sie stehen im unterschriebenen Text der Studierenden.
        var basis = antrag.Signaturen.First(s => s.Schritt == Signaturschritt.Antragstellung);
        var text = await AntragstextFuerEntwurfAsync(antrag.Id, antrag.Nummer, antrag.Erstellt,
            new Antragsangaben(antrag.Vorname, antrag.Nachname, antrag.Matrikelnummer, antrag.Email, antrag.StudiengangId, antrag.PruefungId,
                antrag.ErstpruefendeKontoId, antrag.Terminwuensche, antrag.Zweitpruefungswunsch), abbruch);
        text = text with
        {
            Studiengang = Feld(basis.Inhalt, "Studiengang", text.Studiengang),
            Pruefung = Feld(basis.Inhalt, "Prüfung", text.Pruefung),
            Erstpruefende = Feld(basis.Inhalt, "Gewünschte/r Erstprüfende/r", text.Erstpruefende),
        };
        var f = antrag.AktuelleFestlegung ?? throw new FachlicherFehler("Bitte zuerst Termin, Ort und Zweitprüfende festlegen.");
        var festlegung = new Festlegungstext(ortszeit.Termin(f.Termin) + $" ({O.Zeitzone})", f.Ort,
            f.ZweitpruefendeEmail is null ? f.ZweitpruefendeName : $"{f.ZweitpruefendeName} <{f.ZweitpruefendeEmail}>", f.Bemerkung);
        return Signaturinhalt.Erstellen(schritt, text, antrag.LetzteGueltigeSignatur?.InhaltHash, festlegung, unterzeichner, zeitpunkt);

        static string Feld(string inhalt, string name, string ersatz)
        {
            var zeile = inhalt.Split('\n').FirstOrDefault(z => z.StartsWith(name + ": ", StringComparison.Ordinal));
            if (zeile is null)
            {
                return ersatz;
            }

            var wert = zeile[(name.Length + 2)..];
            var klammer = wert.LastIndexOf(" [", StringComparison.Ordinal);
            return klammer > 0 ? wert[..klammer] : wert;
        }
    }

    private async Task<Signatur> PruefenUndBildenAsync(Antrag antrag, Signaturschritt schritt, DateTimeOffset zeitpunkt, string angezeigterText,
        Unterschrift unterschrift, Konto konto, CancellationToken abbruch)
    {
        ArgumentNullException.ThrowIfNull(unterschrift);
        ImSignierfenster(zeitpunkt);
        var text = await TextAsync(antrag, schritt, konto.Anzeigename, zeitpunkt, abbruch);
        if (text != angezeigterText)
        {
            throw new FachlicherFehler("Der Antrag wurde nach dem Anzeigen geändert. Bitte die Seite neu laden und erneut unterschreiben.");
        }

        return SignaturBilden(antrag.Id, schritt, zeitpunkt, konto.Anzeigename, konto.Id, text, antrag.LetzteGueltigeSignatur?.InhaltHash, unterschrift, konto);
    }

    private Signatur SignaturBilden(Guid antragId, Signaturschritt schritt, DateTimeOffset zeitpunkt, string unterzeichner, Guid? kontoId, string text,
        string? vorgaenger, Unterschrift u, Konto? konto)
    {
        Signatur signatur;
        try
        {
            if (u.Art == Signaturart.Schluessel)
            {
                if (u is not { Algorithmus: { } alg, SignaturWertBase64: { } wert, OeffentlicherSchluesselJwk: { } jwk })
                {
                    throw new SignaturFehler("Bitte den privaten Schlüssel in das Feld ziehen und unterschreiben.");
                }

                var schluessel = SignaturPruefung.SchluesselSignaturPruefen(text, alg, wert, jwk);
                var hinterlegt = konto?.HatGueltigenSchluessel(schluessel.Fingerabdruck) ?? false;
                if (konto is not null && !hinterlegt)
                {
                    throw new SignaturFehler("Dieser Schlüssel ist in Ihrem Profil nicht hinterlegt. Bitte unter „Profil → Signaturschlüssel“ hinterlegen oder per Unterschriftsfeld unterschreiben.");
                }

                signatur = Signatur.MitSchluessel(antragId, schritt, zeitpunkt, unterzeichner, kontoId, text, Signaturinhalt.Hash(text), vorgaenger,
                    alg, wert, schluessel.Kanonisch, schluessel.Fingerabdruck, hinterlegt);
            }
            else
            {
                SignaturPruefung.BildPruefen(u.BildPng);
                signatur = Signatur.MitHandschrift(antragId, schritt, zeitpunkt, unterzeichner, kontoId, text, Signaturinhalt.Hash(text), vorgaenger,
                    u.BildPng!, Signaturinhalt.Hash(u.BildPng!));
            }
        }
        catch (SignaturFehler fehler)
        {
            throw new FachlicherFehler(fehler.Message, fehler);
        }

        signatur.Versiegeln(siegel.Versiegeln(signatur.SiegelText), siegel.SchluesselFingerabdruck);
        return signatur;
    }

    private async Task<IReadOnlyList<Konto>> StudiendekanatEmpfaengerAsync(Antrag antrag, Guid? zustaendig, CancellationToken abbruch)
    {
        if (zustaendig is { } id && await daten.FindenAsync<Konto>(id, abbruch) is { Aktiv: true } k)
        {
            return [k];
        }

        var alle = await daten.ListeAsync(daten.Menge<Konto>().Where(k => k.Aktiv).OrderBy(k => k.Nachname).ThenBy(k => k.Id), abbruch);
        return alle.Where(k => k.Hat(Rolle.Studiendekanat) && k.Id != antrag.ErstpruefendeKontoId && k.Id != antrag.ZweitpruefendeKontoId).ToList();
    }

    private async Task<Dokument?> PdfDokumentAsync(Guid antragId, CancellationToken abbruch)
        => await daten.ErsterAsync(daten.Menge<Dokument>().Where(d => d.AntragId == antragId && d.Inhaltstyp == "application/pdf")
            .OrderByDescending(d => d.Id), abbruch);

    private async Task<List<AntragZeile>> ZeilenAsync(IReadOnlyList<Antrag> antraege, Benutzer wer, CancellationToken abbruch)
    {
        var sgIds = antraege.Select(a => a.StudiengangId).Distinct().ToList();
        var pIds = antraege.Select(a => a.PruefungId).Distinct().ToList();
        var kIds = antraege.Select(a => a.ErstpruefendeKontoId).Distinct().ToList();
        var sg = (await daten.ListeAsync(daten.Menge<Studiengang>().Where(s => sgIds.Contains(s.Id)), abbruch)).ToDictionary(s => s.Id);
        var pr = (await daten.ListeAsync(daten.Menge<Pruefung>().Where(p => pIds.Contains(p.Id)), abbruch)).ToDictionary(p => p.Id);
        var ko = (await daten.ListeAsync(daten.Menge<Konto>().Where(k => kIds.Contains(k.Id)), abbruch)).ToDictionary(k => k.Id);
        return antraege.Select(a => new AntragZeile(a.Id, a.Nummer, a.Status, a.Name, a.Matrikelnummer,
                pr.TryGetValue(a.PruefungId, out var p) ? p.Anzeige : "?", sg.TryGetValue(a.StudiengangId, out var s) ? s.Kuerzel : "?",
                ko.TryGetValue(a.ErstpruefendeKontoId, out var k) ? k.Anzeigename : "?", a.Erstellt, a.Termin,
                WartetAuf(a, wer)))
            .OrderByDescending(z => z.WartetAufMich).ThenByDescending(z => z.Erstellt).ToList();
    }

    private static bool WartetAuf(Antrag a, Benutzer wer)
        => (a.Status == AntragStatus.BeimErstpruefer && a.ErstpruefendeKontoId == wer.KontoId)
           || (a.Status == AntragStatus.BeimStudiendekanat && wer.Hat(Rolle.Studiendekanat)
               && (a.StudiendekanatKontoId is null ? a.ErstpruefendeKontoId != wer.KontoId && a.ZweitpruefendeKontoId != wer.KontoId : a.StudiendekanatKontoId == wer.KontoId));

    private async Task<AntragAnsicht> AnsichtBildenAsync(Antrag antrag, bool pruefen, CancellationToken abbruch)
    {
        var studiengang = await daten.FindenAsync<Studiengang>(antrag.StudiengangId, abbruch) ?? throw new InvalidOperationException("Studiengang fehlt.");
        var pruefung = await daten.FindenAsync<Pruefung>(antrag.PruefungId, abbruch) ?? throw new InvalidOperationException("Prüfung fehlt.");
        var fakultaet = await daten.FindenAsync<Fakultaet>(studiengang.FakultaetId, abbruch);
        var erst = await daten.FindenAsync<Konto>(antrag.ErstpruefendeKontoId, abbruch) ?? throw new InvalidOperationException("Erstprüfende fehlen.");
        var zweit = antrag.ZweitpruefendeKontoId is { } z ? await daten.FindenAsync<Konto>(z, abbruch) : null;
        var dekanat = antrag.Status == AntragStatus.Abgeschlossen && antrag.StudiendekanatKontoId is { } d ? await daten.FindenAsync<Konto>(d, abbruch) : null;
        var befunde = pruefen ? KettePruefen(antrag) : antrag.Signaturen.Select(s => new SignaturBefund(s, true, [])).ToList();
        return new AntragAnsicht(antrag, studiengang, pruefung, fakultaet, erst, zweit, dekanat, befunde, O.Hochschule,
            $"{O.OeffentlicheAdresse}antraege/{antrag.Id:D}", antrag.Termin is { } t ? ortszeit.Termin(t) : null);
    }

    /// <summary>Prüft jede Unterschrift: Hash des Textes, Kette, Systemsiegel und – bei Schlüsseln – die Signatur selbst.</summary>
    internal List<SignaturBefund> KettePruefen(Antrag antrag)
    {
        var befunde = new List<SignaturBefund>();
        string? vorher = null;
        foreach (var s in antrag.Signaturen.OrderBy(s => s.Zeitpunkt).ThenBy(s => s.Schritt))
        {
            var hinweise = new List<string>();
            var ok = true;
            if (Signaturinhalt.Hash(s.Inhalt) != s.InhaltHash)
            {
                ok = false;
                hinweise.Add("Der gespeicherte Text passt nicht zu seinem Hash.");
            }

            if (s.Ueberholt is null)
            {
                if (s.VorgaengerHash != vorher)
                {
                    ok = false;
                    hinweise.Add("Die Kette ist unterbrochen (vorherige Unterschrift passt nicht).");
                }

                vorher = s.InhaltHash;
            }

            if (!siegel.Pruefen(s.SiegelText, s.Systemsiegel, s.SiegelSchluessel))
            {
                ok = false;
                hinweise.Add("Das Systemsiegel ist ungültig – die Unterschrift wurde nach dem Speichern verändert.");
            }
            else
            {
                hinweise.Add("Systemsiegel gültig.");
            }

            if (s.Art == Signaturart.Schluessel)
            {
                try
                {
                    var k = SignaturPruefung.SchluesselSignaturPruefen(s.Inhalt, s.Algorithmus!, s.SignaturWert!, s.OeffentlicherSchluesselJwk!);
                    if (k.Fingerabdruck != s.Fingerabdruck)
                    {
                        throw new SignaturFehler("Fingerabdruck weicht ab.");
                    }

                    hinweise.Add($"Signatur gültig ({s.Algorithmus}, Schlüssel {SignaturPruefung.OeffentlicherSchluessel.Kurz(k.Fingerabdruck)}"
                                 + (s.SchluesselHinterlegt ? ", im Konto hinterlegt)." : ", nicht in einem Konto hinterlegt)."));
                }
                catch (SignaturFehler fehler)
                {
                    ok = false;
                    hinweise.Add($"Signatur ungültig: {fehler.Message}");
                }
            }
            else if (s.BildPng is null || Signaturinhalt.Hash(s.BildPng) != s.BildHash)
            {
                ok = false;
                hinweise.Add("Das Unterschriftsbild wurde verändert.");
            }
            else
            {
                hinweise.Add("Gezeichnete Unterschrift, durch das Systemsiegel an diesen Text gebunden.");
            }

            if (s.Ueberholt is not null)
            {
                hinweise.Add($"Überholt: {s.UeberholtGrund}");
            }

            befunde.Add(new SignaturBefund(s, ok, hinweise));
        }

        return befunde;
    }
}

/// <summary>Was die angemeldete Person mit einem Antrag tun darf – jeweils mit Begründung, falls nicht.</summary>
public sealed record AntragAktionen(Freigabe Festlegen, Freigabe ErstpruefungUnterschreiben, Freigabe Genehmigen, Freigabe Ablehnen, Freigabe Pdf);
