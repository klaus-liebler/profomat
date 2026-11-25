using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProfOMat.Application.Signaturen;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Hausarbeiten;

namespace ProfOMat.Application.Hausarbeiten;

/// <summary>Erörterungsgespräche (FA-608–FA-612): Einschränkungen, Kalender, Terminmails mit Vorlage, Kalenderdatei und Abo-Link.</summary>
public sealed partial class HausarbeitDienst
{
    public const string AnlassTermin = "Hausarbeit: Gesprächstermin";

    // ------------------------------------------------------------------ Studierende: Einschränkungen nachträglich

    public async Task<Themenbestaetigung?> EinschraenkungenAendernAsync(Guid mitgliedId, string token, string? einschraenkungen, CancellationToken abbruch = default)
    {
        if (await PerTokenAsync(mitgliedId, token, abbruch) is not { } h)
        {
            return null;
        }

        h.EinschraenkungenAendern(mitgliedId, einschraenkungen);
        protokoll.Eintragen(Benutzer.Niemand, "Hausarbeit.Einschraenkungen", "Terminliche Einschränkungen geändert (persönlicher Link)", h.Id);
        await daten.SpeichernAsync(abbruch);
        return Ansicht(h, mitgliedId);
    }

    // ------------------------------------------------------------------ Planung

    public Task GespraecheEinstellenAsync(Guid id, int minuten, IReadOnlyCollection<DateOnly> tage, TimeOnly beginn, TimeOnly ende, string? ort, CancellationToken abbruch = default)
        => AendernAsync(id, h => h.GespraecheEinstellen(minuten, tage, beginn, ende, ort), "Hausarbeit.Gespraeche", "Gesprächsplanung eingestellt", abbruch);

    /// <summary>Termin eines Teams setzen oder (null) entfernen.</summary>
    public Task TerminSetzenAsync(Guid id, Guid teamId, DateTime? beginn, CancellationToken abbruch = default)
        => AendernAsync(id, h => h.TerminSetzen(teamId, beginn), "Hausarbeit.Termin", beginn is null ? "Gesprächstermin entfernt" : "Gesprächstermin gesetzt", abbruch);

    public async Task<int> OhneTerminVerteilenAsync(Guid id, CancellationToken abbruch = default)
    {
        var (wer, h) = await LadenMitRechtAsync(id, abbruch);
        var n = h.OhneTerminVerteilen();
        protokoll.Eintragen(wer, "Hausarbeit.Termin", $"{n} Team(s) automatisch eingeplant", h.Id);
        await daten.SpeichernAsync(abbruch);
        return n;
    }

    /// <summary>Vorlage der Mail zur Themenbestätigung (FA-613).</summary>
    public Task BestaetigungsvorlageSetzenAsync(Guid id, string? betreff, string? text, CancellationToken abbruch = default)
        => AendernAsync(id, h => h.BestaetigungsvorlageSetzen(betreff, text), "Hausarbeit.Vorlage", "Vorlage der Bestätigungsmail geändert", abbruch);

    public Task TerminvorlageSetzenAsync(Guid id, string? betreff, string? text, CancellationToken abbruch = default)
        => AendernAsync(id, h => h.TerminvorlageSetzen(betreff, text), "Hausarbeit.Vorlage", "Vorlage der Terminmail geändert", abbruch);

    /// <summary>Entwürfe der Terminmails für alle, deren aktueller Termin noch nicht mitgeteilt wurde (ersetzt ältere Terminentwürfe derselben Person).</summary>
    public async Task<int> TerminmailsErstellenAsync(Guid id, CancellationToken abbruch = default)
    {
        var (wer, h) = await LadenMitRechtAsync(id, abbruch);
        var offen = h.TerminZuBenachrichtigen();
        foreach (var (team, m) in offen)
        {
            var person = h.Person(m.PersonId);
            var (betreff, text) = Terminmail(h, team, m, wer.Name);
            await entwuerfe.AnlegenAsync(wer.KontoIdPflicht, person.Email!, person.VollerName, betreff, text, AnlassTermin, TerminBezug(m.Id), abbruch);
            h.TerminMitgeteilt(m.Id);
        }

        if (offen.Count > 0)
        {
            protokoll.Eintragen(wer, "Hausarbeit.Terminmails", $"{offen.Count} Terminmail(s) erstellt", h.Id);
        }

        await daten.SpeichernAsync(abbruch);
        return offen.Count;
    }

    public static (string Betreff, string Text) Terminmail(Hausarbeit h, Team team, Teammitglied m, string absender)
    {
        ArgumentNullException.ThrowIfNull(h);
        var werte = h.Terminwerte(team, m, absender);
        return (Terminplatzhalter.Ersetzen(h.BetreffVorlage, werte).ReplaceLineEndings(" "), Terminplatzhalter.Ersetzen(h.TextVorlage, werte).ReplaceLineEndings("\n"));
    }

    /// <summary>Bezug der Terminmail (eigener als der Bestätigungsentwurf, damit beide nebeneinander bestehen).</summary>
    public static Guid TerminBezug(Guid mitgliedId)
    {
        var b = SHA256.HashData(Encoding.UTF8.GetBytes("termin:" + mitgliedId.ToString("N")));
        return new Guid(b.AsSpan(0, 16));
    }

    // ------------------------------------------------------------------ Kalender (Outlook)

    /// <summary>Alle Gesprächstermine als iCalendar-Datei (Outlook: öffnen bzw. importieren).</summary>
    public async Task<(byte[] Inhalt, string Dateiname)> KalenderAsync(Guid id, CancellationToken abbruch = default)
    {
        var (_, h) = await LadenMitRechtAsync(id, abbruch);
        return (Kalender(h), $"{Gefache.GefachDienst.Dateiname(h.Titel)}-gespraeche.ics");
    }

    /// <summary>Neuer geheimer Abo-Link (Outlook: „Kalender aus dem Internet hinzufügen“); der vorige gilt dann nicht mehr.</summary>
    public async Task<string> KalenderAboErzeugenAsync(Guid id, CancellationToken abbruch = default)
    {
        var (wer, h) = await LadenMitRechtAsync(id, abbruch);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        h.KalenderTokenSetzen(Signaturinhalt.Hash(token));
        protokoll.Eintragen(wer, "Hausarbeit.Kalenderabo", "Kalender-Abo-Link neu erzeugt", h.Id);
        await daten.SpeichernAsync(abbruch);
        return $"{optionen.Value.OeffentlicheAdresse}hausarbeit/kalender/{h.Id:D}/{token}.ics";
    }

    public Task KalenderAboBeendenAsync(Guid id, CancellationToken abbruch = default)
        => AendernAsync(id, h => h.KalenderTokenSetzen(null), "Hausarbeit.Kalenderabo", "Kalender-Abo-Link zurückgezogen", abbruch);

    /// <summary>Abo ohne Anmeldung über den geheimen Link; null bei falschem oder zurückgezogenem Link.</summary>
    public async Task<byte[]?> KalenderAboAsync(Guid id, string token, CancellationToken abbruch = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100 || await daten.FindenAsync<Hausarbeit>(id, abbruch) is not { KalenderTokenHash: { } gespeichert } h)
        {
            return null;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(gespeichert), Encoding.ASCII.GetBytes(Signaturinhalt.Hash(token))) ? Kalender(h) : null;
    }

    /// <summary>iCalendar (RFC 5545) mit einem Termin je Team; Zeiten in UTC, Text maskiert und auf 75 Oktette umbrochen.</summary>
    private byte[] Kalender(Hausarbeit h)
    {
        var sb = new StringBuilder();
        void Zeile(string z)
        {
            var bytes = Encoding.UTF8.GetBytes(z);
            if (bytes.Length <= 75)
            {
                sb.Append(z).Append("\r\n");
                return;
            }

            // Umbruch an Zeichengrenzen nach höchstens 75 Oktetten, Folgezeilen beginnen mit einem Leerzeichen
            var teil = new StringBuilder();
            var laenge = 0;
            var erste = true;
            foreach (var r in z.EnumerateRunes())
            {
                var n = r.Utf8SequenceLength;
                if (laenge + n > (erste ? 75 : 74))
                {
                    sb.Append(erste ? "" : " ").Append(teil).Append("\r\n");
                    (teil, laenge, erste) = (new StringBuilder(), 0, false);
                }

                teil.Append(r.ToString());
                laenge += n;
            }

            sb.Append(erste ? "" : " ").Append(teil).Append("\r\n");
        }

        static string T(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal).ReplaceLineEndings("\\n");
        string Utc(DateTime lokal) => ortszeit.AlsUtc(lokal).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        Zeile("BEGIN:VCALENDAR");
        Zeile("VERSION:2.0");
        Zeile("PRODID:-//ProfOMat//Hausarbeiten//DE");
        Zeile("CALSCALE:GREGORIAN");
        Zeile("METHOD:PUBLISH");
        Zeile("X-WR-CALNAME:" + T($"Gespräche {h.Titel}"));
        var stempel = uhr.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var team in h.MitTermin)
        {
            var namen = team.Mitglieder.Select(m => h.Person(m.PersonId).VollerName).ToList();
            var thema = h.Thema(team.ThemaId);
            var beschreibung = new StringBuilder($"Team {team.Nr}: {string.Join(", ", namen)}\nThema: {thema?.Anzeige ?? "–"}");
            foreach (var m in team.Mitglieder.Where(m => m.Einschraenkungen is not null))
            {
                beschreibung.Append($"\nEinschränkungen {h.Person(m.PersonId).VollerName}: {m.Einschraenkungen}");
            }

            Zeile("BEGIN:VEVENT");
            Zeile($"UID:{team.Id:N}@profomat");
            Zeile("DTSTAMP:" + stempel);
            Zeile("DTSTART:" + Utc(team.Termin!.Value));
            Zeile("DTEND:" + Utc(h.Ende(team)!.Value));
            Zeile("SUMMARY:" + T($"Erörterungsgespräch Team {team.Nr}: {string.Join(", ", namen)}"));
            if (h.GespraechOrt is { } ort)
            {
                Zeile("LOCATION:" + T(ort));
            }

            Zeile("DESCRIPTION:" + T(beschreibung.ToString()));
            Zeile("END:VEVENT");
        }

        Zeile("END:VCALENDAR");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
