using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ProfOMat.Application.Betrieb;
using ProfOMat.Application.Klausuren;
using ProfOMat.Application.Signaturen;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Hausarbeiten;
using ProfOMat.Domain.Klausuren;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Hausarbeiten;

/// <summary>Was eine Person über ihren persönlichen Link sieht.</summary>
/// <param name="Einschraenkungen">Gemeldete terminliche Einschränkungen für das Erörterungsgespräch.</param>
/// <param name="Termin">Gesprächstermin, sobald mitgeteilt (Ortszeit).</param>
public sealed record Themenbestaetigung(string Hausarbeit, string? Pruefer, string Name, IReadOnlyList<string> Team, HausarbeitThema Thema,
    DateTimeOffset? Frist, DateTimeOffset? Bestaetigt, string? Einschraenkungen = null, DateTime? Termin = null, int Dauer = 0, string? Ort = null);

/// <summary>
/// Hausarbeiten: Personen aus der Prüfungsliste, Themenliste, Teams mit Wünschen, Zuteilung und persönliche Themenbestätigung.
/// Die Mails mit dem Bestätigungslink verschickt die betreuende Person selbst (Entwürfe im persönlichen Postausgang, Desktop-Outlook).
/// Sichtbar für die betreuende Person und die Systemverwaltung.
/// </summary>
public sealed partial class HausarbeitDienst(IDaten daten, IAktuellerBenutzer aktuell, Mailentwuerfe entwuerfe, Protokoll protokoll, Ortszeit ortszeit,
    TimeProvider uhr, IOptions<ProfOMatOptionen> optionen)
{
    public const string AnlassBestaetigung = "Hausarbeit: Themenbestätigung";

    public static Freigabe HausarbeitenBetreuen(Benutzer wer) => wer.Hat(Rolle.Pruefende) || wer.Hat(Rolle.Systemverwaltung)
        ? Freigabe.Ja
        : Freigabe.Nein("Hausarbeiten betreuen Prüfende.");

    public async Task<IReadOnlyList<Hausarbeit>> ListeAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        HausarbeitenBetreuen(wer).Sicherstellen();
        var abfrage = daten.Menge<Hausarbeit>();
        if (!wer.Hat(Rolle.Systemverwaltung))
        {
            abfrage = abfrage.Where(h => h.KontoId == wer.KontoId);
        }

        return await daten.ListeAsync(abfrage.OrderByDescending(h => h.Id), abbruch);
    }

    /// <summary>Neue Hausarbeit aus einer Prüfungsliste; enthält sie mehrere Prüfungen, muss eine gewählt sein.</summary>
    public async Task<Hausarbeit> AusListeAnlegenAsync(byte[] csv, string? titel, string? pruefungsnummer, DateTimeOffset? frist, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        HausarbeitenBetreuen(wer).Sicherstellen();
        var liste = PruefungslisteLeser.Lesen(csv, string.IsNullOrWhiteSpace(pruefungsnummer) ? null : pruefungsnummer);
        if (liste.AuswahlNoetig)
        {
            throw new FachlicherFehler("Die Datei enthält mehrere Prüfungen – bitte eine auswählen.");
        }

        var h = new Hausarbeit(wer.KontoIdPflicht, string.IsNullOrWhiteSpace(titel) ? liste.Pruefungsname ?? "Hausarbeit" : titel, liste.Pruefungsnummer,
            liste.Pruefer, uhr.GetUtcNow());
        h.Aendern(h.Titel, h.Pruefungsnummer, frist);
        var ergebnis = h.Abgleichen(liste.Zeilen);
        daten.Hinzufuegen(h);
        protokoll.Eintragen(wer, "Hausarbeit.Angelegt", $"Hausarbeit „{h.Titel}“ aus Prüfungsliste angelegt ({ergebnis})", h.Id);
        await daten.SpeichernAsync(abbruch);
        return h;
    }

    public async Task<Hausarbeit> LadenAsync(Guid id, CancellationToken abbruch = default) => (await LadenMitRechtAsync(id, abbruch)).Hausarbeit;

    public Task AendernAsync(Guid id, string titel, string? pruefungsnummer, DateTimeOffset? frist, CancellationToken abbruch = default)
        => AendernAsync(id, h => h.Aendern(titel, pruefungsnummer, frist), "Hausarbeit.Geaendert", "Angaben geändert", abbruch);

    /// <summary>Aktuelle Prüfungsliste erneut hochladen; bei mehreren Prüfungen zählt die dieser Hausarbeit.</summary>
    public async Task<ImportErgebnis> ListeAbgleichenAsync(Guid id, byte[] csv, CancellationToken abbruch = default)
    {
        var (wer, h) = await LadenMitRechtAsync(id, abbruch);
        var liste = PruefungslisteLeser.Lesen(csv);
        if (liste.AuswahlNoetig)
        {
            liste = h.Pruefungsnummer is { } eigene ? PruefungslisteLeser.Lesen(csv, eigene)
                : throw new FachlicherFehler("Die Datei enthält mehrere Prüfungen, diese Hausarbeit hat aber keine Prüfungsnummer – bitte zuerst eintragen.");
        }
        else if (h.Pruefungsnummer is { } nr && liste.Pruefungsnummer is { } neu && nr != neu)
        {
            throw new FachlicherFehler($"Die Liste gehört zur Prüfung {neu}, diese Hausarbeit zu {nr}.");
        }

        var ergebnis = h.Abgleichen(liste.Zeilen);
        protokoll.Eintragen(wer, "Hausarbeit.Abgleich", $"Prüfungsliste abgeglichen: {ergebnis}", h.Id);
        await daten.SpeichernAsync(abbruch);
        return ergebnis;
    }

    public Task PersonHinzufuegenAsync(Guid id, string matrikelnummer, string nachname, string vorname, string? email, CancellationToken abbruch = default)
        => AendernAsync(id, h => h.PersonHinzufuegen(matrikelnummer, nachname, vorname, email), "Hausarbeit.Person", "Person von Hand ergänzt", abbruch);

    public Task ThemaSpeichernAsync(Guid id, Guid? themaId, int nr, string titel, string? beschreibung, Themenart art, bool eigen,
        CancellationToken abbruch = default)
        => AendernAsync(id, h => h.ThemaSpeichern(themaId, nr, titel, beschreibung, art, eigen), "Hausarbeit.Thema", $"Thema {nr} gespeichert", abbruch);

    public async Task<ThemenImport> ThemenEinlesenAsync(Guid id, string text, CancellationToken abbruch = default)
    {
        ThemenImport ergebnis = new(0, 0);
        await AendernAsync(id, h => ergebnis = h.ThemenEinlesen(text), "Hausarbeit.Themen", "Themenliste eingelesen", abbruch);
        return ergebnis;
    }

    public Task ThemaEntfernenAsync(Guid id, Guid themaId, CancellationToken abbruch = default)
        => AendernAsync(id, h => h.ThemaEntfernen(themaId), "Hausarbeit.Thema", "Thema entfernt", abbruch);

    public async Task<Team> TeamSpeichernAsync(Guid id, Guid? teamId, IReadOnlyList<Guid> personen, Guid? sprecherId, IReadOnlyList<Guid> wuensche,
        string? notiz, CancellationToken abbruch = default)
    {
        Team? team = null;
        await AendernAsync(id, h => team = h.TeamSpeichern(teamId, personen, sprecherId, wuensche, notiz), "Hausarbeit.Team",
            teamId is null ? "Team angelegt" : "Team geändert", abbruch);
        return team!;
    }

    public Task TeamEntfernenAsync(Guid id, Guid teamId, CancellationToken abbruch = default)
        => AendernAsync(id, h => h.TeamEntfernen(teamId), "Hausarbeit.Team", "Team entfernt", abbruch);

    public Task ThemaZuteilenAsync(Guid id, Guid teamId, Guid? themaId, CancellationToken abbruch = default)
        => AendernAsync(id, h => h.ThemaZuteilen(teamId, themaId), "Hausarbeit.Zuteilung", "Thema zugeteilt", abbruch);

    public async Task<int> NachWuenschenZuteilenAsync(Guid id, CancellationToken abbruch = default)
    {
        var anzahl = 0;
        await AendernAsync(id, h => anzahl = h.NachWuenschenZuteilen(), "Hausarbeit.Zuteilung", "Themen nach Wünschen zugeteilt", abbruch);
        return anzahl;
    }

    /// <summary>
    /// Erstellt für jede Person mit zugeteiltem, noch nicht mitgeteiltem Thema eine Mail mit persönlichem Bestätigungslink – als Entwurf im
    /// persönlichen Postausgang (Versand aus dem eigenen Outlook). Liefert die Zahl der Entwürfe.
    /// </summary>
    public async Task<int> BestaetigungsmailsErstellenAsync(Guid id, CancellationToken abbruch = default)
    {
        var (wer, h) = await LadenMitRechtAsync(id, abbruch);
        var offen = h.ZuBenachrichtigen();
        foreach (var (team, m) in offen)
        {
            await EntwurfErstellenAsync(wer, h, team, m, abbruch);
        }

        if (offen.Count > 0)
        {
            protokoll.Eintragen(wer, "Hausarbeit.Bestaetigungsmails", $"{offen.Count} Mail(s) zur Themenbestätigung erstellt", h.Id);
        }

        await daten.SpeichernAsync(abbruch);
        return offen.Count;
    }

    /// <summary>Neue Mail mit neuem Link für eine Person (z. B. Mail verloren); der alte Link gilt nicht mehr.</summary>
    public async Task MailErneuernAsync(Guid id, Guid mitgliedId, CancellationToken abbruch = default)
    {
        var (wer, h) = await LadenMitRechtAsync(id, abbruch);
        var (team, m) = h.Mitglied(mitgliedId);
        if (h.Person(m.PersonId).Email is null)
        {
            throw new FachlicherFehler("Für diese Person ist keine Mailadresse bekannt.");
        }

        await EntwurfErstellenAsync(wer, h, team, m, abbruch);
        protokoll.Eintragen(wer, "Hausarbeit.Bestaetigungsmails", "Mail zur Themenbestätigung erneuert", h.Id);
        await daten.SpeichernAsync(abbruch);
    }

    // ------------------------------------------------------------------ Studierende über den persönlichen Link
    public async Task<Themenbestaetigung?> BestaetigungAnsichtAsync(Guid mitgliedId, string token, CancellationToken abbruch = default)
        => await PerTokenAsync(mitgliedId, token, abbruch) is { } h ? Ansicht(h, mitgliedId) : null;

    /// <param name="einschraenkungen">Terminliche Einschränkungen für das Erörterungsgespräch (optional).</param>
    public async Task<Themenbestaetigung?> BestaetigenAsync(Guid mitgliedId, string token, string? einschraenkungen = null, CancellationToken abbruch = default)
    {
        if (await PerTokenAsync(mitgliedId, token, abbruch) is not { } h)
        {
            return null;
        }

        var (_, m) = h.Mitglied(mitgliedId);
        if (m.Bestaetigt is null)
        {
            h.Bestaetigen(mitgliedId, uhr.GetUtcNow(), einschraenkungen);
            protokoll.Eintragen(Benutzer.Niemand, "Hausarbeit.Bestaetigt", "Thema bestätigt (persönlicher Link)", h.Id);
            await daten.SpeichernAsync(abbruch);
        }

        return Ansicht(h, mitgliedId);
    }

    private static Themenbestaetigung Ansicht(Hausarbeit h, Guid mitgliedId)
    {
        var (team, m) = h.Mitglied(mitgliedId);
        var person = h.Person(m.PersonId);
        var thema = h.Thema(team.ThemaId) ?? throw new FachlicherFehler("Für Sie liegt gerade kein Thema zur Bestätigung vor.");
        var termin = m.TerminMitgeteilt is not null && m.TerminMitgeteilt == team.Termin ? team.Termin : null;
        return new Themenbestaetigung(h.Titel, h.Pruefer, person.VollerName, team.Mitglieder.Select(x => h.Person(x.PersonId).VollerName).ToList(), thema,
            h.Bestaetigungsfrist, m.Bestaetigt, m.Einschraenkungen, termin, h.GespraechMinuten, h.GespraechOrt);
    }

    internal async Task<Hausarbeit?> PerTokenAsync(Guid mitgliedId, string token, CancellationToken abbruch)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hausarbeitId = await daten.ErsterAsync(daten.Menge<Team>().Where(t => t.Mitglieder.Any(m => m.Id == mitgliedId)).Select(t => (Guid?)t.HausarbeitId), abbruch);
        if (hausarbeitId is not { } hid || await daten.FindenAsync<Hausarbeit>(hid, abbruch) is not { } h)
        {
            return null;
        }

        var (team, m) = h.Mitglied(mitgliedId);
        return team.ThemaId is not null && m.TokenHash is { } gespeichert
               && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(gespeichert), System.Text.Encoding.ASCII.GetBytes(Signaturinhalt.Hash(token)))
            ? h
            : null;
    }

    // ------------------------------------------------------------------ intern
    private async Task EntwurfErstellenAsync(Benutzer wer, Hausarbeit h, Team team, Teammitglied m, CancellationToken abbruch)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        h.Benachrichtigen(m.Id, Signaturinhalt.Hash(token), uhr.GetUtcNow());
        var person = h.Person(m.PersonId);
        var link = $"{optionen.Value.OeffentlicheAdresse}hausarbeit/bestaetigen/{m.Id:D}/{token}";
        var (betreff, text) = h.Bestaetigungsmail(team, m, link, h.Bestaetigungsfrist is { } f ? ortszeit.Datum(f) : null, wer.Name);
        await entwuerfe.AnlegenAsync(wer.KontoIdPflicht, person.Email!, person.VollerName, betreff, text, AnlassBestaetigung, m.Id, abbruch);
    }

    private async Task AendernAsync(Guid id, Action<Hausarbeit> aenderung, string ereignis, string beschreibung, CancellationToken abbruch)
    {
        var (wer, h) = await LadenMitRechtAsync(id, abbruch);
        aenderung(h);
        protokoll.Eintragen(wer, ereignis, beschreibung, h.Id);
        await daten.SpeichernAsync(abbruch);
    }

    internal async Task<(Benutzer Wer, Hausarbeit Hausarbeit)> LadenMitRechtAsync(Guid id, CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        HausarbeitenBetreuen(wer).Sicherstellen();
        var h = await daten.FindenAsync<Hausarbeit>(id, abbruch) ?? throw new FachlicherFehler("Hausarbeit nicht gefunden.");
        return h.KontoId == wer.KontoId || wer.Hat(Rolle.Systemverwaltung)
            ? (wer, h)
            : throw new FachlicherFehler("Diese Hausarbeit sieht nur, wer sie betreut.");
    }
}
