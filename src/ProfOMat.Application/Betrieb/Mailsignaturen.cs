using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Betrieb;

/// <summary>Hochgeladene Datei (Signatur-HTML, Text oder Bild).</summary>
public sealed record HochgeladeneDatei(string Name, byte[] Inhalt);

/// <summary>Ergebnis der Übernahme: was eingebettet wurde und was nicht.</summary>
public sealed record SignaturUebernahme(int Bilder, IReadOnlyList<string> Hinweise);

/// <summary>Optische Mailsignatur je Konto: Upload der Outlook-Signaturdateien, Testmail an sich selbst.</summary>
public sealed class Mailsignaturen(IDaten daten, IAktuellerBenutzer aktuell, TimeProvider uhr)
{
    public const int HtmlMaxBytes = 300 * 1024;

    public async Task<Mailsignatur?> MeineAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        return await VonAsync(wer.KontoIdPflicht, abbruch);
    }

    public Task<Mailsignatur?> VonAsync(Guid kontoId, CancellationToken abbruch = default)
        => daten.ErsterAsync(daten.Menge<Mailsignatur>().Where(s => s.KontoId == kontoId), abbruch);

    /// <summary>
    /// Übernimmt die Outlook-Signatur: eine .htm/.html-Datei (Pflicht), dazu die Bilder aus dem Ordner „…-Dateien“ und optional die .txt-Fassung.
    /// Das HTML wird bereinigt (keine Skripte, keine externen Inhalte); Bilder werden eingebettet.
    /// </summary>
    public async Task<SignaturUebernahme> HochladenAsync(IReadOnlyList<HochgeladeneDatei> dateien, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(dateien);
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var htm = dateien.Where(d => Endung(d.Name) is ".htm" or ".html").ToList();
        if (htm.Count != 1)
        {
            throw new FachlicherFehler("Bitte genau eine Signaturdatei (.htm) wählen, dazu die Bilder aus dem Ordner „…-Dateien“.");
        }

        if (htm[0].Inhalt.Length > HtmlMaxBytes)
        {
            throw new FachlicherFehler("Die Signaturdatei ist zu groß.");
        }

        var bilder = dateien.Where(d => Signaturaufbereitung.Bildtyp(d.Name) is not null).ToList();
        var aufbereitet = Signaturaufbereitung.Aufbereiten(Signaturaufbereitung.Dekodieren(htm[0].Inhalt), bilder);
        var text = dateien.FirstOrDefault(d => Endung(d.Name) == ".txt") is { } txt
            ? Signaturaufbereitung.Dekodieren(txt.Inhalt).Trim()
            : aufbereitet.Text;

        var signatur = await VonAsync(wer.KontoIdPflicht, abbruch);
        if (signatur is null)
        {
            signatur = new Mailsignatur(wer.KontoIdPflicht);
            daten.Hinzufuegen(signatur);
        }

        signatur.Setzen(aufbereitet.Html, text, aufbereitet.Bilder, uhr.GetUtcNow());
        await daten.SpeichernAsync(abbruch);
        return new SignaturUebernahme(aufbereitet.Bilder.Count, aufbereitet.Hinweise);
    }

    public async Task EntfernenAsync(CancellationToken abbruch = default)
    {
        if (await MeineAsync(abbruch) is { } s)
        {
            daten.Entfernen(s);
            await daten.SpeichernAsync(abbruch);
        }
    }

    /// <summary>Testmail an die eigene Adresse (zum Prüfen von Signatur und S/MIME in Outlook).</summary>
    public async Task<(byte[] Inhalt, string Dateiname)> TestmailAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var konto = await daten.FindenAsync<Konto>(wer.KontoIdPflicht, abbruch) ?? throw new FachlicherFehler("Konto nicht gefunden.");
        var text = $"""
            Testmail aus ProfOMat.

            Prüfen Sie: Steht unten Ihre Signatur mit Bildern? Zeigt Outlook beim Senden das Siegel der digitalen Signatur (S/MIME)?
            Ein Link zum Ausprobieren: https://www.hs-osnabrueck.de/

            Viele Grüße
            {konto.Anzeigename}
            """;
        return (Eml.Erzeugen(konto.Email, konto.Anzeigename, "ProfOMat: Testmail mit Signatur", text.ReplaceLineEndings("\n"), await VonAsync(konto.Id, abbruch)),
            "ProfOMat-Testmail.eml");
    }

    private static string Endung(string name) => Path.GetExtension(name).ToLowerInvariant();
}

/// <summary>Bereinigt Outlook-Signatur-HTML (Word-HTML) und bettet Bilder ein. Bewusst streng: was nicht gebraucht wird, fliegt raus.</summary>
public static partial class Signaturaufbereitung
{
    public sealed record Ergebnis(string Html, string Text, IReadOnlyList<(string ContentId, string Dateiname, string Typ, byte[] Inhalt)> Bilder,
        IReadOnlyList<string> Hinweise);

    public static string? Bildtyp(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        _ => null,
    };

    /// <summary>UTF-8, sonst Windows-1252 (so speichert Outlook die Signatur).</summary>
    public static string Dekodieren(byte[] inhalt)
    {
        ArgumentNullException.ThrowIfNull(inhalt);
        try
        {
            return new UTF8Encoding(false, true).GetString(inhalt).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(inhalt);
        }
    }

    public static Ergebnis Aufbereiten(string html, IReadOnlyList<HochgeladeneDatei> bilder)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(bilder);
        var hinweise = new List<string>();

        // Stile aus dem Kopf (Word-Klassen wie MsoNormal) behalten, aber ohne alles, was etwas nachlädt
        var stile = string.Concat(StilBlock().Matches(html).Select(m => m.Groups[1].Value))
            .Replace("<!--", "", StringComparison.Ordinal).Replace("-->", "", StringComparison.Ordinal);
        stile = FontFace().Replace(stile, "");
        // Regel für Regel: Word schreibt z. B. „v\:* {behavior:url(#default#VML);}“ – nur solche Regeln fallen weg, nicht der ganze Block
        stile = string.Concat(Stilregel().Matches(stile).Where(r => !Gefaehrlich().IsMatch(r.Value)).Select(r => r.Value.Trim() + "\n"));

        var koerper = Koerper().Match(html) is { Success: true } k ? k.Groups[1].Value : html;
        koerper = VmlBlock().Replace(koerper, "");                       // VML-Grafiken (Fallback-<img> bleibt)
        koerper = Bedingt().Replace(koerper, "");                        // <![if !vml]> … <![endif]> – Inhalt bleibt
        koerper = Kommentar().Replace(koerper, "");
        koerper = MitInhalt().Replace(koerper, "");
        koerper = Einzeltag().Replace(koerper, "");
        koerper = Namensraumtag().Replace(koerper, "");
        koerper = Ereignis().Replace(koerper, "");

        // Bilder: hochgeladene Dateien (per Dateiname) und direkt eingebettete data:-Bilder (so speichert Outlook sie oft) werden als
        // Anhang mit cid eingebettet; alles andere (z. B. Bilder aus dem Internet) wird entfernt.
        var eingebettet = new List<(string, string, string, byte[])>();
        var nachName = bilder.GroupBy(b => Path.GetFileName(b.Name).ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        koerper = Bild().Replace(koerper, m =>
        {
            var src = Attribut(m.Value, "src");
            (string Name, string Typ, byte[] Inhalt)? bild = null;
            if (src is not null && DataBild().Match(src) is { Success: true } d)
            {
                var typ = d.Groups[1].Value.ToLowerInvariant() is "jpg" or "jpeg" ? "image/jpeg" : $"image/{d.Groups[1].Value.ToLowerInvariant()}";
                var inhalt = Base64Lesen(src[d.Length..]);
                if (inhalt is { Length: > 0 and <= Mailsignatur.BildMaxBytes })
                {
                    bild = ($"bild{eingebettet.Count + 1}{(typ == "image/jpeg" ? ".jpg" : "." + typ[6..])}", typ, inhalt);
                }
            }
            else if (src is not null
                     && nachName.TryGetValue(Path.GetFileName(WebUtility.UrlDecode(src.Replace('\\', '/')).Split('?')[0]).ToLowerInvariant(), out var b)
                     && Bildtyp(b.Name) is { } typ)
            {
                bild = (Path.GetFileName(b.Name), typ, b.Inhalt);
            }

            if (bild is not { } x)
            {
                var anzeige = src is null ? "?" : src.Length > 60 ? src[..60] + " …" : src;
                hinweise.Add($"Bild „{anzeige}“ nicht hochgeladen oder ungültig – weggelassen.");
                return string.Empty;
            }

            var vorhanden = eingebettet.FirstOrDefault(e => e.Item4.AsSpan().SequenceEqual(x.Inhalt));   // gleiches Bild nur einmal
            var cid = vorhanden.Item1 ?? $"sig{eingebettet.Count + 1}@profomat";
            if (vorhanden.Item1 is null)
            {
                eingebettet.Add((cid, x.Name, x.Typ, x.Inhalt));
            }

            return Quelle().Replace(m.Value, $"src=\"cid:{cid}\"", 1);
        });
        if (eingebettet.Count > Mailsignatur.MaxBilder)
        {
            throw new FachlicherFehler($"Die Signatur enthält mehr als {Mailsignatur.MaxBilder} Bilder.");
        }

        // Links nur http(s), mailto, tel
        koerper = Verweis().Replace(koerper, m => m.Groups[2].Value.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                                  || m.Groups[2].Value.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                                  || m.Groups[2].Value.TrimStart().StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                                                  || m.Groups[2].Value.TrimStart().StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
                                                  || m.Groups[2].Value.StartsWith("cid:", StringComparison.Ordinal)
            ? m.Value
            : string.Empty);
        koerper = koerper.Trim();
        if (koerper.Length == 0)
        {
            throw new FachlicherFehler("In der Datei wurde keine Signatur gefunden.");
        }

        var ergebnisHtml = (stile.Trim().Length > 0 ? $"<style>{stile.Trim()}</style>\n" : "") + koerper;
        return new Ergebnis(ergebnisHtml, AlsText(koerper), eingebettet, hinweise.Distinct().ToList());
    }

    /// <summary>HTML grob als Text (Zeilenumbrüche an Absätzen und Zeilen, Entities aufgelöst).</summary>
    public static string AlsText(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var t = Umbruch().Replace(html, "\n");
        t = Tag().Replace(t, "");
        t = WebUtility.HtmlDecode(t).Replace(' ', ' ');
        var zeilen = t.Split('\n').Select(z => Leerraum().Replace(z, " ").Trim());
        return Leerzeilen().Replace(string.Join("\n", zeilen), "\n\n").Trim();
    }

    private static byte[]? Base64Lesen(string s)
    {
        var sauber = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        try
        {
            return Convert.FromBase64String(sauber);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^data:image/(png|jpe?g|gif);base64,", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DataBild();

    private static string? Attribut(string tag, string name)
        => Regex.Match(tag, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) is { Success: true } m
            ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)
            : null;

    [GeneratedRegex(@"<style[^>]*>(.*?)</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex StilBlock();

    [GeneratedRegex(@"@font-face\s*\{[^}]*\}", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex FontFace();

    /// <summary>Einfache CSS-Regel „Selektor { Deklarationen }“ (verschachtelte @-Blöcke wie @media fallen dabei weg).</summary>
    [GeneratedRegex(@"[^{}@]+\{[^{}]*\}", RegexOptions.None, 1000)]
    private static partial Regex Stilregel();

    [GeneratedRegex(@"@import|url\s*\(|expression\s*\(|behavior\s*:|javascript:|[<>]", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Gefaehrlich();

    [GeneratedRegex(@"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex Koerper();

    [GeneratedRegex(@"<!--\[if gte vml 1\]>.*?<!\[endif\]-->", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex VmlBlock();

    [GeneratedRegex(@"<!\[(if [^\]]*|endif)\]>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Bedingt();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, 1000)]
    private static partial Regex Kommentar();

    [GeneratedRegex(@"<(script|style|iframe|object|embed|form|noscript|xml|head|svg|math|template)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex MitInhalt();

    [GeneratedRegex(@"</?(script|style|iframe|object|embed|form|input|button|textarea|select|link|meta|base|frame|frameset|applet|noscript|xml)\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Einzeltag();

    [GeneratedRegex(@"</?[a-z]+:[a-z]+\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Namensraumtag();

    [GeneratedRegex(@"\s(on[a-z]+|v:[a-z]+|o:[a-z]+|srcset|formaction|xmlns(:[a-z]+)?)\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Ereignis();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Bild();

    [GeneratedRegex(@"\bsrc\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Quelle();

    [GeneratedRegex(@"\s(href|src|background|action|poster|lowsrc|dynsrc)\s*=\s*[""']?([^""'\s>]*)[""']?", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Verweis();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|tr|li|h[1-6])\s*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Umbruch();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r]+", RegexOptions.None, 1000)]
    private static partial Regex Leerraum();

    [GeneratedRegex(@"\n{3,}", RegexOptions.None, 1000)]
    private static partial Regex Leerzeilen();
}
