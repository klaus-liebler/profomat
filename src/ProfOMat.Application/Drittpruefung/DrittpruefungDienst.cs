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
/// Anmeldung mündlicher Drittprüfungen per Einladung: Erstprüfende erzeugen einen geheimen Link mit vorausgefüllten Angaben →
/// Studierende ergänzen Matrikelnummer und Terminwünsche und unterschreiben → Erstprüfende ergänzen Termin, Ort und Zweitprüfende
/// und unterschreiben → Zweitprüfende unterschreiben → Studiendekanat unterschreibt im ProfOMat oder bekommt das PDF außerhalb.
/// Nach jeder Unterschrift entsteht eine neue Fassung des ausgefüllten Formulars (PDF). Jede Unterschrift wird hier geprüft
/// (Text, Kette, Signatur bzw. Bild) und vom Server versiegelt.
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
    TimeProvider uhr)
{
    /// <summary>So lange darf zwischen Anzeigen und Absenden einer Unterschrift liegen.</summary>
    public static readonly TimeSpan Signierfenster = TimeSpan.FromMinutes(30);

    /// <summary>Altes Verfahren: so lange blieb ein unbestätigter Antrag offen.</summary>
    private static readonly TimeSpan AlteBestaetigungsfrist = TimeSpan.FromDays(7);

    private ProfOMatOptionen O => optionen.Value;

    /// <summary>Einladungslinks erzeugen Prüfende (sie werden Erstprüfende).</summary>
    public static Freigabe EinladungenErzeugen(Benutzer wer) => wer.Hat(Rolle.Pruefende)
        ? Freigabe.Ja
        : Freigabe.Nein("Einladungen zur mündlichen Drittprüfung erzeugen Prüfende.");

    /// <summary>Vorschlag für die Gültigkeit eines neuen Links (Tage).</summary>
    public int EinladungGueltigTage => Math.Clamp(O.EinladungGueltigTage, 1, Einladungsangaben.MaxGueltigTage);

    // =====================================================================================================
    // Erstprüfende: Einladung
    // =====================================================================================================

    /// <summary>Erzeugt den Vorgang und den geheimen Link für die Studierende bzw. den Studierenden (wird nur hier einmal angezeigt).</summary>
    public async Task<Einladungslink> EinladenAsync(Einladungsangaben angaben, Festlegung? vorbelegung, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(angaben);
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        EinladungenErzeugen(wer).Sicherstellen();
        if (angaben.Beanstandung() is { } fehler)
        {
            throw new FachlicherFehler(fehler);
        }

        var studiengang = await daten.FindenAsync<Studiengang>(angaben.StudiengangId, abbruch) ?? throw new FachlicherFehler("Studiengang nicht gefunden.");
        var pruefung = await daten.FindenAsync<Pruefung>(angaben.PruefungId, abbruch) ?? throw new FachlicherFehler("Prüfung nicht gefunden.");
        if (pruefung.StudiengangId != studiengang.Id)
        {
            throw new FachlicherFehler("Die Prüfung gehört nicht zum gewählten Studiengang.");
        }

        var jetzt = Signaturinhalt.Kappen(uhr.GetUtcNow());
        var id = Ids.Neu();
        var nummer = $"DP-{ortszeit.Lokal(jetzt).Year}-{id.ToString("N")[^6..].ToUpperInvariant()}";
        var token = NeuesToken();
        var antrag = Antrag.Einladen(id, nummer, angaben, wer.KontoIdPflicht, vorbelegung is null ? null : await MitZweitKontoAsync(vorbelegung, abbruch),
            jetzt, TokenHash(token));
        daten.Hinzufuegen(antrag);
        protokoll.Eintragen(wer, "Drittpruefung.Eingeladen", $"Antrag {nummer}: Einladungslink erzeugt ({angaben.GueltigTage} Tage gültig)", id);
        await daten.SpeichernAsync(abbruch);
        return new Einladungslink(id, nummer, StudierendenLink(id, token), antrag.GueltigBis!.Value);
    }

    /// <summary>Neuer Link (der alte gilt nicht mehr), z. B. wenn die Mail verloren ging oder der Link abgelaufen ist.</summary>
    public async Task<Einladungslink> LinkErneuernAsync(Guid antragId, int gueltigTage, CancellationToken abbruch = default)
    {
        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        var token = NeuesToken();
        antrag.LinkErneuern(wer.KontoIdPflicht, TokenHash(token), gueltigTage, uhr.GetUtcNow());
        protokoll.Eintragen(wer, "Drittpruefung.LinkErneuert", $"Antrag {antrag.Nummer}: neuer Einladungslink ({gueltigTage} Tage gültig)", antrag.Id);
        await daten.SpeichernAsync(abbruch);
        return new Einladungslink(antrag.Id, antrag.Nummer, StudierendenLink(antrag.Id, token), antrag.GueltigBis!.Value);
    }

    public async Task EinladungZuruecknehmenAsync(Guid antragId, CancellationToken abbruch = default)
    {
        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        antrag.EinladungZuruecknehmen(wer.KontoIdPflicht, uhr.GetUtcNow());
        protokoll.Eintragen(wer, "Drittpruefung.EinladungZurueck", $"Antrag {antrag.Nummer}: Einladung zurückgenommen", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Termin, Ort und Zweitprüfende vormerken (vor oder nach der Unterschrift der Studierenden; noch ohne eigene Unterschrift).</summary>
    public async Task FestlegungSpeichernAsync(Guid antragId, Festlegung festlegung, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(festlegung);
        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        antrag.KannFestlegen(wer.KontoIdPflicht).Sicherstellen();
        antrag.FestlegungSetzen(wer.KontoIdPflicht, await MitZweitKontoAsync(festlegung, abbruch), uhr.GetUtcNow());
        protokoll.Eintragen(wer, "Drittpruefung.Festlegung", $"Antrag {antrag.Nummer}: Termin, Ort bzw. Zweitprüfende vorgemerkt", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    // =====================================================================================================
    // Studierende (ohne Anmeldung; Zugang über den geheimen Link)
    // =====================================================================================================

    public async Task<AntragAnsicht?> StudierendenAnsichtAsync(Guid antragId, string token, CancellationToken abbruch = default)
        => await PerTokenAsync(antragId, token, abbruch) is { } antrag ? await AnsichtBildenAsync(antrag, pruefen: true, abbruch) : null;

    /// <summary>Prüft Matrikelnummer und Terminwünsche und erzeugt den Text, den die oder der Studierende unterschreibt.</summary>
    public async Task<Signiervorlage> StudierendenVorlageAsync(Guid antragId, string token, string matrikelnummer, string? terminwuensche,
        CancellationToken abbruch = default)
    {
        var antrag = await PerTokenAsync(antragId, token, abbruch) ?? throw new FachlicherFehler("Der Link ist ungültig.");
        antrag.KannStudierendUnterschreiben(uhr.GetUtcNow()).Sicherstellen();
        if (Antrag.StudierendenBeanstandung(matrikelnummer, terminwuensche) is { } fehler)
        {
            throw new FachlicherFehler(fehler);
        }

        var zeitpunkt = Signaturinhalt.Kappen(uhr.GetUtcNow());
        var text = await TextAsync(antrag, Signaturschritt.Antragstellung, antrag.Name, zeitpunkt, matrikelnummer, terminwuensche, abbruch);
        return new Signiervorlage(antrag.Id, Signaturschritt.Antragstellung, text, zeitpunkt, null, antrag.Name);
    }

    /// <summary>Die oder der Studierende unterschreibt: das Formular-PDF entsteht und geht an die Erstprüfenden.</summary>
    public async Task StudierendUnterschreibenAsync(Guid antragId, string token, string matrikelnummer, string? terminwuensche, DateTimeOffset zeitpunkt,
        string angezeigterText, Unterschrift unterschrift, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(unterschrift);
        var antrag = await PerTokenAsync(antragId, token, abbruch) ?? throw new FachlicherFehler("Der Link ist ungültig.");
        antrag.KannStudierendUnterschreiben(uhr.GetUtcNow()).Sicherstellen();
        ImSignierfenster(zeitpunkt);
        var text = await TextAsync(antrag, Signaturschritt.Antragstellung, antrag.Name, zeitpunkt, matrikelnummer, terminwuensche, abbruch);
        if (text != angezeigterText)
        {
            throw new FachlicherFehler("Die Angaben wurden nach dem Anzeigen geändert. Bitte die Seite neu laden und erneut unterschreiben.");
        }

        var signatur = SignaturBilden(antrag.Id, Signaturschritt.Antragstellung, zeitpunkt, antrag.Name, null, text, null, unterschrift, null);
        antrag.StudierendUnterschreiben(matrikelnummer, terminwuensche, signatur, uhr.GetUtcNow());
        var (ansicht, dokument) = await PdfAblegenAsync(antrag, abbruch);
        var (betreff, mail) = Mailtexte.AnErstpruefende(ansicht);
        post.Einreihen(ansicht.Erstpruefende.Email, betreff, mail, antrag.Id, dokument.Id);
        protokoll.Eintragen(Benutzer.Niemand, "Drittpruefung.Studierende", $"Antrag {antrag.Nummer}: von der/dem Studierenden unterschrieben ({unterschrift.Art.Anzeigename()})",
            antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task ZurueckziehenAsync(Guid antragId, string token, CancellationToken abbruch = default)
    {
        var antrag = await PerTokenAsync(antragId, token, abbruch) ?? throw new FachlicherFehler("Antrag nicht gefunden.");
        var unterschrieben = antrag.Status is not (AntragStatus.Eingeladen or AntragStatus.WartetAufBestaetigung);
        antrag.Zurueckziehen(uhr.GetUtcNow());
        var ansicht = await AnsichtBildenAsync(antrag, pruefen: false, abbruch);
        var (betreff, text) = Mailtexte.Zurueckgezogen(ansicht, ansicht.Erstpruefende.Anzeigename);
        if (unterschrieben || antrag.Verfahren == Antragsverfahren.Einladung)
        {
            post.Einreihen(ansicht.Erstpruefende.Email, betreff, text, antrag.Id);
        }

        protokoll.Eintragen(Benutzer.Niemand, "Drittpruefung.Zurueckgezogen", $"Antrag {antrag.Nummer} von der/dem Studierenden zurückgezogen", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Aktuelle Fassung des PDF für Studierende (sobald sie unterschrieben haben).</summary>
    public async Task<Dokument?> PdfFuerStudierendeAsync(Guid antragId, string token, CancellationToken abbruch = default)
        => await PerTokenAsync(antragId, token, abbruch) is { } antrag ? await PdfDokumentAsync(antrag.Id, abbruch) : null;

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
            abfrage = abfrage.Where(a => a.Status == AntragStatus.Eingeladen || a.Status == AntragStatus.BeimErstpruefer || a.Status == AntragStatus.BeimZweitpruefer
                                         || a.Status == AntragStatus.BeimStudiendekanat || a.Status == AntragStatus.ExternWeiterleiten
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
        var hatPdf = await PdfDokumentAsync(antrag.Id, abbruch) is not null;
        return new AntragAktionen(
            antrag.KannFestlegen(ich),
            antrag.KannErstpruefungUnterschreiben(ich, uhr.GetUtcNow()),
            antrag.KannZweitpruefungUnterschreiben(ich),
            antrag.KannGenehmigen(ich, dekanat),
            antrag.KannZurueckgeben(ich, dekanat),
            antrag.KannAblehnen(ich, dekanat),
            antrag.KannWeitergeleitetMelden(ich),
            antrag.KannLinkErneuern(ich),
            antrag.KannEinladungZuruecknehmen(ich),
            hatPdf ? Freigabe.Ja : Freigabe.Nein("Das PDF entsteht, sobald die/der Studierende unterschrieben hat."));
    }

    /// <summary>Text für die Unterschrift der Erst-, Zweitprüfenden bzw. des Studiendekanats.</summary>
    public async Task<Signiervorlage> VorlageAsync(Guid antragId, Signaturschritt schritt, CancellationToken abbruch = default)
    {
        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        SchrittFreigabe(antrag, schritt, wer).Sicherstellen();
        var zeitpunkt = Signaturinhalt.Kappen(uhr.GetUtcNow());
        return new Signiervorlage(antrag.Id, schritt, await TextAsync(antrag, schritt, wer.Name, zeitpunkt, null, null, abbruch), zeitpunkt,
            antrag.LetzteGueltigeSignatur?.InhaltHash, wer.Name);
    }

    /// <summary>Erstprüfende unterschreiben; der Antrag geht an die Zweitprüfenden.</summary>
    public async Task ErstpruefungUnterschreibenAsync(Guid antragId, DateTimeOffset zeitpunkt, string angezeigterText, Unterschrift unterschrift,
        CancellationToken abbruch = default)
    {
        var (wer, antrag, konto, signatur) = await PruefenUndBildenAsync(antragId, Signaturschritt.Erstpruefung, zeitpunkt, angezeigterText, unterschrift, abbruch);
        antrag.ErstpruefungUnterschreiben(konto.Id, signatur, uhr.GetUtcNow());
        var (ansicht, dokument) = await PdfAblegenAsync(antrag, abbruch);
        if (ansicht.ZweitpruefendeKonto is { } zweit)
        {
            var (betreff, text) = Mailtexte.AnZweitpruefende(ansicht, zweit.Anzeigename);
            post.Einreihen(zweit.Email, betreff, text, antrag.Id, dokument.Id);
        }

        protokoll.Eintragen(wer, "Drittpruefung.Erstpruefung", $"Antrag {antrag.Nummer}: Erstprüfung unterschrieben ({unterschrift.Art.Anzeigename()}), an Zweitprüfende",
            antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>
    /// Zweitprüfende unterschreiben. Ist für den Studiengang (bzw. die Fakultät) ein Studiendekanat im ProfOMat hinterlegt, geht der
    /// Antrag dorthin; prüft diese Person selbst, an die Vertretung. Sonst leiten die Zweitprüfenden das PDF außerhalb weiter.
    /// </summary>
    public async Task ZweitpruefungUnterschreibenAsync(Guid antragId, DateTimeOffset zeitpunkt, string angezeigterText, Unterschrift unterschrift,
        CancellationToken abbruch = default)
    {
        var (wer, antrag, konto, signatur) = await PruefenUndBildenAsync(antragId, Signaturschritt.Zweitpruefung, zeitpunkt, angezeigterText, unterschrift, abbruch);
        var (imSystem, zustaendig, empfaenger) = await StudiendekanatErmittelnAsync(antrag, abbruch);
        antrag.ZweitpruefungUnterschreiben(konto.Id, signatur, imSystem, zustaendig);
        var (ansicht, dokument) = await PdfAblegenAsync(antrag, abbruch);
        foreach (var dekanat in empfaenger)
        {
            var (betreff, text) = Mailtexte.AnStudiendekanat(ansicht, dekanat.Anzeigename);
            post.Einreihen(dekanat.Email, betreff, text, antrag.Id, dokument.Id);
        }

        protokoll.Eintragen(wer, "Drittpruefung.Zweitpruefung",
            $"Antrag {antrag.Nummer}: Zweitprüfung unterschrieben ({unterschrift.Art.Anzeigename()}), {(imSystem ? "an Studiendekanat" : "Studiendekanat außerhalb")}",
            antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Studiendekanat unterschreibt: letzte Fassung des PDF; die Prüfenden bekommen sie zur Kenntnis.</summary>
    public async Task GenehmigenAsync(Guid antragId, DateTimeOffset zeitpunkt, string angezeigterText, Unterschrift unterschrift, CancellationToken abbruch = default)
    {
        var (wer, antrag, konto, signatur) = await PruefenUndBildenAsync(antragId, Signaturschritt.Studiendekanat, zeitpunkt, angezeigterText, unterschrift, abbruch);
        antrag.StudiendekanatUnterschreiben(konto.Id, signatur, uhr.GetUtcNow());
        var (ansicht, dokument) = await PdfAblegenAsync(antrag, abbruch);
        foreach (var (anrede, an) in new[] { (ansicht.Erstpruefende.Anzeigename, ansicht.Erstpruefende.Email), (antrag.ZweitpruefendeName, antrag.ZweitpruefendeEmail) })
        {
            if (anrede is not null && an is not null)
            {
                var (betreff, text) = Mailtexte.AbgeschlossenAnPruefende(ansicht, anrede);
                post.Einreihen(an, betreff, text, antrag.Id, dokument.Id);
            }
        }

        protokoll.Eintragen(wer, "Drittpruefung.Bestellt", $"Antrag {antrag.Nummer}: Prüfende bestellt ({unterschrift.Art.Anzeigename()})", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Die Prüfenden melden, dass das PDF an das Studiendekanat außerhalb des ProfOMat gegangen ist.</summary>
    public async Task WeitergeleitetAsync(Guid antragId, CancellationToken abbruch = default)
    {
        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        antrag.WeitergeleitetMelden(wer.KontoIdPflicht, uhr.GetUtcNow());
        protokoll.Eintragen(wer, "Drittpruefung.Weitergeleitet", $"Antrag {antrag.Nummer}: an das Studiendekanat außerhalb weitergeleitet", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Zweitprüfende bzw. Studiendekanat geben mit Rückfrage an die Erstprüfenden zurück.</summary>
    public async Task ZurueckgebenAsync(Guid antragId, string grund, CancellationToken abbruch = default)
    {
        if (string.IsNullOrWhiteSpace(grund))
        {
            throw new FachlicherFehler("Bitte kurz begründen, was geändert werden soll.");
        }

        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        antrag.Zurueckgeben(wer.KontoIdPflicht, wer.Hat(Rolle.Studiendekanat), wer.Name, grund, uhr.GetUtcNow());
        var (ansicht, _) = await PdfAblegenAsync(antrag, abbruch);
        var (betreff, text) = Mailtexte.Zurueckgegeben(ansicht, wer.Name, grund.Trim());
        post.Einreihen(ansicht.Erstpruefende.Email, betreff, text, antrag.Id);
        protokoll.Eintragen(wer, "Drittpruefung.Zurueckgegeben", $"Antrag {antrag.Nummer}: an Erstprüfende zurückgegeben", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task AblehnenAsync(Guid antragId, string grund, CancellationToken abbruch = default)
    {
        if (string.IsNullOrWhiteSpace(grund))
        {
            throw new FachlicherFehler("Bitte die Ablehnung begründen; die/der Studierende sieht die Begründung über den Link.");
        }

        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        antrag.Ablehnen(wer.KontoIdPflicht, wer.Hat(Rolle.Studiendekanat), wer.Name, grund, uhr.GetUtcNow());
        if (!string.IsNullOrEmpty(antrag.Email))
        {
            var ansicht = await AnsichtBildenAsync(antrag, pruefen: false, abbruch);
            var (betreff, text) = Mailtexte.Abgelehnt(ansicht);
            post.Einreihen(antrag.Email, betreff, text, antrag.Id);
        }

        protokoll.Eintragen(wer, "Drittpruefung.Abgelehnt", $"Antrag {antrag.Nummer} abgelehnt", antrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Aktuelle Fassung des PDF.</summary>
    public async Task<Dokument?> PdfAsync(Guid antragId, CancellationToken abbruch = default)
    {
        var (_, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        return await PdfDokumentAsync(antrag.Id, abbruch);
    }

    /// <summary>Ungenutzte Einladungslinks (und unbestätigte Anträge des alten Verfahrens) verfallen lassen (Hintergrundjob).</summary>
    public async Task<int> VerfallenLassenAsync(CancellationToken abbruch = default)
    {
        var jetzt = uhr.GetUtcNow();
        var offen = await daten.ListeAsync(daten.Menge<Antrag>().Where(a => a.Status == AntragStatus.Eingeladen || a.Status == AntragStatus.WartetAufBestaetigung),
            abbruch);
        var anzahl = 0;
        foreach (var a in offen.Where(a => a.Verfallen(jetzt, jetzt - AlteBestaetigungsfrist)))
        {
            anzahl++;
            protokoll.Eintragen(Benutzer.Automatik, "Drittpruefung.Verfallen", $"Antrag {a.Nummer} verfallen (Link nicht genutzt)", a.Id);
        }

        await daten.SpeichernAsync(abbruch);
        return anzahl;
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

    private Freigabe SchrittFreigabe(Antrag antrag, Signaturschritt schritt, Benutzer wer) => schritt switch
    {
        Signaturschritt.Erstpruefung => antrag.KannErstpruefungUnterschreiben(wer.KontoIdPflicht, uhr.GetUtcNow()),
        Signaturschritt.Zweitpruefung => antrag.KannZweitpruefungUnterschreiben(wer.KontoIdPflicht),
        Signaturschritt.Studiendekanat => antrag.KannGenehmigen(wer.KontoIdPflicht, wer.Hat(Rolle.Studiendekanat)),
        _ => Freigabe.Nein("Diesen Schritt unterschreiben die Studierenden über ihren Link."),
    };

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

    private static string NeuesToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string TokenHash(string token) => Signaturinhalt.Hash(token);

    private string StudierendenLink(Guid id, string token) => $"{O.OeffentlicheAdresse}drittpruefung/{id:D}/{token}";

    private void ImSignierfenster(DateTimeOffset zeitpunkt)
    {
        var jetzt = uhr.GetUtcNow();
        if (zeitpunkt > jetzt.AddMinutes(1) || zeitpunkt < jetzt - Signierfenster)
        {
            throw new FachlicherFehler("Die Vorlage zum Unterschreiben ist abgelaufen (30 Minuten). Bitte erneut unterschreiben.");
        }
    }

    /// <summary>Zweitprüfende nur mit Konto (sie unterschreiben im ProfOMat); Name und Mailadresse kommen aus dem Konto.</summary>
    private async Task<Festlegung> MitZweitKontoAsync(Festlegung festlegung, CancellationToken abbruch)
    {
        if (festlegung.ZweitpruefendeKontoId is not { } zweitId)
        {
            return festlegung with { ZweitpruefendeName = null, ZweitpruefendeEmail = null };
        }

        var zweit = await daten.FindenAsync<Konto>(zweitId, abbruch);
        if (zweit is not { Aktiv: true } || !zweit.Hat(Rolle.Pruefende))
        {
            throw new FachlicherFehler("Die gewählte Person steht als Zweitprüfende/r nicht zur Verfügung.");
        }

        return festlegung with { ZweitpruefendeName = zweit.Anzeigename, ZweitpruefendeEmail = zweit.Email };
    }

    /// <summary>
    /// Der Text zum Unterschreiben. Studiengang, Prüfung und Erstprüfende stehen so darin, wie die oder der Studierende sie
    /// unterschrieben hat (ein späterer Katalogabgleich ändert sie nicht mehr).
    /// </summary>
    private async Task<string> TextAsync(Antrag antrag, Signaturschritt schritt, string unterzeichner, DateTimeOffset zeitpunkt, string? matrikelnummer,
        string? terminwuensche, CancellationToken abbruch)
    {
        var studiengang = await daten.FindenAsync<Studiengang>(antrag.StudiengangId, abbruch) ?? throw new FachlicherFehler("Studiengang nicht gefunden.");
        var pruefung = await daten.FindenAsync<Pruefung>(antrag.PruefungId, abbruch) ?? throw new FachlicherFehler("Prüfung nicht gefunden.");
        var erst = await daten.FindenAsync<Konto>(antrag.ErstpruefendeKontoId, abbruch) ?? throw new FachlicherFehler("Erstprüfende/r nicht gefunden.");
        var basis = antrag.GueltigeSignaturen.FirstOrDefault(s => s.Schritt == Signaturschritt.Antragstellung);
        var studierend = schritt == Signaturschritt.Antragstellung;
        var v = new Vorgangstext(antrag.Id, antrag.Nummer, antrag.Erstellt, antrag.Vorname, antrag.Nachname,
            studierend ? matrikelnummer?.Trim() : antrag.Matrikelnummer,
            Wert("Studiengang", studiengang.Anzeige), studiengang.Schluessel, Wert("Modul", pruefung.Anzeige), pruefung.Schluessel,
            studierend ? (string.IsNullOrWhiteSpace(terminwuensche) ? null : terminwuensche.Trim()) : antrag.Terminwuensche,
            Wert("Erstprüfende/r", erst.Anzeigename), erst.Id, antrag.ZweitpruefendeName, antrag.ZweitpruefendeKontoId,
            antrag.Termin is { } t ? $"{ortszeit.Termin(t)} ({O.Zeitzone})" : null, antrag.Ort, antrag.Bemerkung);
        return Signaturinhalt.ErstellenV2(schritt, v, antrag.LetzteGueltigeSignatur?.InhaltHash, unterzeichner, zeitpunkt);

        string Wert(string feld, string aktuellerWert) => basis is null ? aktuellerWert : Signaturinhalt.Feld(basis.Inhalt, feld) ?? aktuellerWert;
    }

    private async Task<(Benutzer Wer, Antrag Antrag, Konto Konto, Signatur Signatur)> PruefenUndBildenAsync(Guid antragId, Signaturschritt schritt,
        DateTimeOffset zeitpunkt, string angezeigterText, Unterschrift unterschrift, CancellationToken abbruch)
    {
        ArgumentNullException.ThrowIfNull(unterschrift);
        var (wer, antrag) = await LadenMitRechtAsync(antragId, abbruch);
        var konto = await daten.FindenAsync<Konto>(wer.KontoIdPflicht, abbruch) ?? throw new FachlicherFehler("Konto nicht gefunden.");
        SchrittFreigabe(antrag, schritt, wer).Sicherstellen();
        ImSignierfenster(zeitpunkt);
        var text = await TextAsync(antrag, schritt, konto.Anzeigename, zeitpunkt, null, null, abbruch);
        if (text != angezeigterText)
        {
            throw new FachlicherFehler("Der Antrag wurde nach dem Anzeigen geändert. Bitte die Seite neu laden und erneut unterschreiben.");
        }

        var signatur = SignaturBilden(antrag.Id, schritt, zeitpunkt, konto.Anzeigename, konto.Id, text, antrag.LetzteGueltigeSignatur?.InhaltHash, unterschrift, konto);
        return (wer, antrag, konto, signatur);
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

    /// <summary>Zuständiges Studiendekanat im ProfOMat (Studiengang vor Fakultät); prüft es selbst, die Vertretung; sonst außerhalb.</summary>
    private async Task<(bool ImSystem, Guid? Zustaendig, IReadOnlyList<Konto> Empfaenger)> StudiendekanatErmittelnAsync(Antrag antrag, CancellationToken abbruch)
    {
        var studiengang = await daten.FindenAsync<Studiengang>(antrag.StudiengangId, abbruch);
        var fakultaet = studiengang is null ? null : await daten.FindenAsync<Fakultaet>(studiengang.FakultaetId, abbruch);
        if ((studiengang?.StudiendekanatKontoId ?? fakultaet?.StudiendekanatKontoId) is not { } id
            || await daten.FindenAsync<Konto>(id, abbruch) is not { Aktiv: true } konto || !konto.Hat(Rolle.Studiendekanat))
        {
            return (false, null, []);
        }

        if (konto.Id != antrag.ErstpruefendeKontoId && konto.Id != antrag.ZweitpruefendeKontoId)
        {
            return (true, konto.Id, [konto]);
        }

        var alle = await daten.ListeAsync(daten.Menge<Konto>().Where(k => k.Aktiv).OrderBy(k => k.Nachname).ThenBy(k => k.Id), abbruch);
        var vertretung = alle.Where(k => k.Hat(Rolle.Studiendekanat) && k.Id != antrag.ErstpruefendeKontoId && k.Id != antrag.ZweitpruefendeKontoId).ToList();
        return vertretung.Count > 0 ? (true, null, vertretung) : (false, null, []);
    }

    /// <summary>Neue Fassung des ausgefüllten Formulars ablegen (nach jeder Unterschrift bzw. Rückgabe).</summary>
    private async Task<(AntragAnsicht Ansicht, Dokument Dokument)> PdfAblegenAsync(Antrag antrag, CancellationToken abbruch)
    {
        var ansicht = await AnsichtBildenAsync(antrag, pruefen: true, abbruch);
        if (!ansicht.KetteInOrdnung)
        {
            throw new FachlicherFehler("Die Unterschriftenkette ist nicht in Ordnung. Bitte die Systemverwaltung informieren.");
        }

        var inhalt = pdf.Erzeugen(ansicht);
        var dokument = new Dokument(antrag.Id, $"Drittpruefung-{antrag.Nummer}.pdf", "application/pdf", inhalt, Signaturinhalt.Hash(inhalt), uhr.GetUtcNow());
        daten.Hinzufuegen(dokument);
        return (ansicht, dokument);
    }

    private async Task<Dokument?> PdfDokumentAsync(Guid antragId, CancellationToken abbruch)
        => await daten.ErsterAsync(daten.Menge<Dokument>().Where(d => d.AntragId == antragId && d.Inhaltstyp == "application/pdf")
            .OrderByDescending(d => d.Erstellt).ThenByDescending(d => d.Id), abbruch);

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

    private static bool WartetAuf(Antrag a, Benutzer wer) => a.Status switch
    {
        AntragStatus.BeimErstpruefer => a.ErstpruefendeKontoId == wer.KontoId,
        AntragStatus.BeimZweitpruefer or AntragStatus.ExternWeiterleiten => a.ZweitpruefendeKontoId == wer.KontoId,
        AntragStatus.BeimStudiendekanat => wer.Hat(Rolle.Studiendekanat)
                                           && (a.StudiendekanatKontoId is null
                                               ? a.ErstpruefendeKontoId != wer.KontoId && a.ZweitpruefendeKontoId != wer.KontoId
                                               : a.StudiendekanatKontoId == wer.KontoId),
        _ => false,
    };

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
            $"{O.OeffentlicheAdresse}antraege/{antrag.Id:D}", antrag.Termin is { } t ? ortszeit.Termin(t) : null,
            fakultaet?.SekretariatEmail ?? O.SekretariatEmail);
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
public sealed record AntragAktionen(
    Freigabe Festlegen,
    Freigabe ErstpruefungUnterschreiben,
    Freigabe ZweitpruefungUnterschreiben,
    Freigabe Genehmigen,
    Freigabe Zurueckgeben,
    Freigabe Ablehnen,
    Freigabe WeitergeleitetMelden,
    Freigabe LinkErneuern,
    Freigabe EinladungZuruecknehmen,
    Freigabe Pdf);

/// <summary>Der geheime Link für die Studierende bzw. den Studierenden – wird nur beim Erzeugen angezeigt (gespeichert ist nur sein Hash).</summary>
public sealed record Einladungslink(Guid AntragId, string Nummer, string Link, DateTimeOffset GueltigBis);
