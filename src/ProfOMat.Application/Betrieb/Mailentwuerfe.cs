using System.Text;
using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Application.Betrieb;

/// <summary>
/// Persönlicher Postausgang: vorformulierte Mails, die man selbst aus dem Desktop-Outlook verschickt (ein Klick öffnet sie dort als
/// neue Mail). Jede Person sieht nur ihre eigenen Entwürfe.
/// </summary>
public sealed class Mailentwuerfe(IDaten daten, IAktuellerBenutzer aktuell, Mailsignaturen signaturen, TimeProvider uhr)
{
    /// <summary>Neuen Entwurf anlegen; ein offener Entwurf mit demselben Bezug wird ersetzt (z. B. neuer Link nach Themenwechsel).</summary>
    public async Task<Mailentwurf> AnlegenAsync(Guid kontoId, string an, string? anName, string betreff, string text, string anlass, Guid? bezugId,
        CancellationToken abbruch = default)
    {
        if (bezugId is { } bezug)
        {
            foreach (var alt in await daten.ListeAsync(daten.Menge<Mailentwurf>().Where(e => e.KontoId == kontoId && e.BezugId == bezug), abbruch))
            {
                daten.Entfernen(alt);
            }
        }

        var entwurf = new Mailentwurf(kontoId, an, anName, betreff, text, uhr.GetUtcNow(), anlass, bezugId);
        daten.Hinzufuegen(entwurf);
        return entwurf;
    }

    public async Task<IReadOnlyList<Mailentwurf>> MeineAsync(bool auchErledigte, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var abfrage = daten.Menge<Mailentwurf>().Where(e => e.KontoId == wer.KontoId);
        if (!auchErledigte)
        {
            abfrage = abfrage.Where(e => e.Erledigt == null);
        }

        return await daten.ListeAsync(abfrage.OrderByDescending(e => e.Erstellt).ThenBy(e => e.An), abbruch);
    }

    public async Task<int> OffeneAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        return wer.KontoId is { } konto
            ? await daten.AnzahlAsync(daten.Menge<Mailentwurf>().Where(e => e.KontoId == konto && e.Erledigt == null && e.Geoeffnet == null), abbruch)
            : 0;
    }

    /// <summary>Entwurf als .eml-Datei (Outlook öffnet sie als neue, noch nicht gesendete Mail); merkt sich das Öffnen.</summary>
    public async Task<(byte[] Inhalt, string Dateiname)> EmlAsync(Guid id, CancellationToken abbruch = default)
    {
        var e = await EigenerAsync(id, abbruch);
        e.AlsGeoeffnetMarkieren(uhr.GetUtcNow());
        await daten.SpeichernAsync(abbruch);
        var signatur = await signaturen.VonAsync(e.KontoId, abbruch);
        return (Eml.Erzeugen(e.An, e.AnName, e.Betreff, e.Text, signatur), Eml.Dateiname(e.Betreff, e.AnName ?? e.An));
    }

    /// <summary>
    /// Alle offenen eigenen Entwürfe (optional nur eines Anlasses) als ZIP mit je einer .eml-Datei; merkt sich das Öffnen.
    /// Gleiche Dateinamen bekommen eine laufende Nummer.
    /// </summary>
    public async Task<(byte[] Inhalt, string Dateiname, int Anzahl)> ZipAsync(string? anlass = null, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var abfrage = daten.Menge<Mailentwurf>().Where(e => e.KontoId == wer.KontoId && e.Erledigt == null);
        if (!string.IsNullOrWhiteSpace(anlass))
        {
            abfrage = abfrage.Where(e => e.Anlass == anlass);
        }

        var liste = await daten.ListeAsync(abfrage.OrderBy(e => e.Erstellt).ThenBy(e => e.An), abbruch);
        if (liste.Count == 0)
        {
            throw new FachlicherFehler("Keine offenen Mails zum Herunterladen.");
        }

        var signatur = await signaturen.VonAsync(wer.KontoIdPflicht, abbruch);
        var jetzt = uhr.GetUtcNow();
        var namen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var ziel = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ziel, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in liste)
            {
                var name = Eml.Dateiname(e.Betreff, e.AnName ?? e.An);
                var basis = Path.GetFileNameWithoutExtension(name);
                for (var i = 2; !namen.Add(name); i++)
                {
                    name = $"{basis}-{i}.eml";
                }

                var eintrag = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
                await using (var s = eintrag.Open())
                {
                    await s.WriteAsync(Eml.Erzeugen(e.An, e.AnName, e.Betreff, e.Text, signatur), abbruch);
                }

                e.AlsGeoeffnetMarkieren(jetzt);
            }
        }

        await daten.SpeichernAsync(abbruch);
        return (ziel.ToArray(), "mails.zip", liste.Count);
    }

    public async Task ErledigtSetzenAsync(Guid id, bool erledigt, CancellationToken abbruch = default)
    {
        var e = await EigenerAsync(id, abbruch);
        e.ErledigtSetzen(erledigt, uhr.GetUtcNow());
        await daten.SpeichernAsync(abbruch);
    }

    public async Task LoeschenAsync(Guid id, CancellationToken abbruch = default)
    {
        daten.Entfernen(await EigenerAsync(id, abbruch));
        await daten.SpeichernAsync(abbruch);
    }

    private async Task<Mailentwurf> EigenerAsync(Guid id, CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var e = await daten.FindenAsync<Mailentwurf>(id, abbruch) ?? throw new FachlicherFehler("Mailentwurf nicht gefunden.");
        return e.KontoId == wer.KontoId ? e : throw new FachlicherFehler("Diesen Entwurf sieht nur, wem er gehört.");
    }
}

/// <summary>
/// Schreibt eine Mail als .eml-Datei (RFC 5322, UTF-8). <c>X-Unsent: 1</c> lässt das Desktop-Outlook sie als neue, bearbeitbare Mail
/// öffnen; ohne „From“ nimmt Outlook das Standardkonto. Mit Signatur: Text- und HTML-Fassung, Signaturbilder eingebettet (cid).
/// </summary>
public static partial class Eml
{
    public static byte[] Erzeugen(string an, string? anName, string betreff, string text, Mailsignatur? signatur = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(an);
        ArgumentNullException.ThrowIfNull(betreff);
        ArgumentNullException.ThrowIfNull(text);
        if (an.IndexOfAny(['\r', '\n', '<', '>', ',', ';', '"']) >= 0)
        {
            throw new FachlicherFehler("Die Empfängeradresse ist ungültig.");
        }

        var sb = new StringBuilder();
        sb.Append("X-Unsent: 1\r\n");
        sb.Append("To: ").Append(string.IsNullOrWhiteSpace(anName) ? an : $"{Wort(anName)} <{an}>").Append("\r\n");
        sb.Append("Subject: ").Append(Wort(betreff)).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        if (signatur is null)
        {
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            Base64(sb, Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n")));
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        var (alternativ, verwandt) = ($"=_alt_{Guid.NewGuid():N}", $"=_rel_{Guid.NewGuid():N}");
        sb.Append($"Content-Type: multipart/alternative; boundary=\"{alternativ}\"\r\n\r\n");
        sb.Append($"--{alternativ}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        Base64(sb, Encoding.UTF8.GetBytes($"{text}\n\n{signatur.Text}".ReplaceLineEndings("\r\n")));
        sb.Append($"--{alternativ}\r\nContent-Type: multipart/related; boundary=\"{verwandt}\"; type=\"text/html\"\r\n\r\n");
        sb.Append($"--{verwandt}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        Base64(sb, Encoding.UTF8.GetBytes(Html(text, signatur.Html)));
        foreach (var b in signatur.Bilder)
        {
            sb.Append($"--{verwandt}\r\nContent-Type: {b.Typ}; name=\"{b.Dateiname}\"\r\nContent-Transfer-Encoding: base64\r\n");
            sb.Append($"Content-ID: <{b.ContentId}>\r\nContent-Disposition: inline; filename=\"{b.Dateiname}\"\r\n\r\n");
            Base64(sb, b.Inhalt);
        }

        sb.Append($"--{verwandt}--\r\n--{alternativ}--\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Mailtext als HTML (Links anklickbar, Zeilenumbrüche bleiben), darunter die Signatur.</summary>
    internal static string Html(string text, string signaturHtml)
    {
        var absaetze = System.Net.WebUtility.HtmlEncode(text.ReplaceLineEndings("\n"));
        absaetze = Link().Replace(absaetze, m => $"<a href=\"{m.Value}\">{m.Value}</a>").Replace("\n", "<br>\n", StringComparison.Ordinal);
        return "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"></head><body>\n"
               + $"<div style=\"font-family:Calibri,Arial,sans-serif;font-size:11pt\">{absaetze}</div>\n<br>\n{signaturHtml}\n</body></html>";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"https?://[^\s<>""]+", System.Text.RegularExpressions.RegexOptions.None, 1000)]
    private static partial System.Text.RegularExpressions.Regex Link();

    private static void Base64(StringBuilder sb, byte[] daten)
    {
        var b = Convert.ToBase64String(daten);
        for (var i = 0; i < b.Length; i += 76)
        {
            sb.Append(b.AsSpan(i, Math.Min(76, b.Length - i))).Append("\r\n");
        }
    }

    /// <summary>Dateiname ohne Sonderzeichen, z. B. „Hausarbeit - Mustermann Erika.eml“.</summary>
    public static string Dateiname(string betreff, string empfaenger)
    {
        ArgumentNullException.ThrowIfNull(betreff);
        ArgumentNullException.ThrowIfNull(empfaenger);
        var roh = $"{betreff.Split(':')[0]} - {empfaenger}";
        var sauber = new string(roh.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '_').ToArray()).Trim();
        return (sauber.Length > 80 ? sauber[..80] : sauber) + ".eml";
    }

    /// <summary>Kopfzeilenwert: ASCII bleibt, sonst RFC-2047-Wort (Base64, UTF-8); Zeilenumbrüche werden Leerzeichen.</summary>
    private static string Wort(string wert)
    {
        var einzeilig = wert.ReplaceLineEndings(" ").Trim();
        return einzeilig.All(c => c is >= ' ' and <= '~' && c != '"')
            ? einzeilig
            : string.Join(" ", Teile(einzeilig).Select(t => $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(t))}?="));
    }

    /// <summary>Kodierte Wörter dürfen höchstens 75 Zeichen lang sein: in Stücke von höchstens 40 Zeichen teilen.</summary>
    private static IEnumerable<string> Teile(string s)
    {
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        var sb = new StringBuilder();
        while (e.MoveNext())
        {
            sb.Append(e.GetTextElement());
            if (Encoding.UTF8.GetByteCount(sb.ToString()) >= 40)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }
}
