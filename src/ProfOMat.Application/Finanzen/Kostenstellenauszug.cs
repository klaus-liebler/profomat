using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Application.Finanzen;

/// <summary>Eine Zeile des SAP-Kostenstellenauszugs.</summary>
public sealed record Auszugszeile(int Nr, string Kostenstelle, DateOnly? Erfasst, DateOnly Belegdatum, DateOnly Buchungsdatum, string Kostenart,
    string KostenartText, decimal Betrag, string Text, string? Belegnummer);

/// <summary>Gelesener Auszug. <see cref="Fehler"/> nennt, warum er nicht übernommen werden kann (leer = in Ordnung).</summary>
/// <param name="Summe">Summe laut Summenzeile („*“).</param>
public sealed record Kostenstellenauszug(string Dateiname, string Kostenstelle, int Jahr, DateOnly? Exportdatum, IReadOnlyList<Auszugszeile> Zeilen,
    decimal Summe, IReadOnlyList<string> Fehler)
{
    public DateOnly? BisDatum => Zeilen.Count == 0 ? null : Zeilen.Max(z => z.Buchungsdatum);

    public bool Gueltig => Fehler.Count == 0;
}

/// <summary>
/// Liest den monatlichen SAP-Auszug der Kostenstelle („20260622 12103012 Tabelle.xls“). Trotz der Endung ist es Text: UTF-16
/// (meist ohne BOM), tabgetrennt, eine Kopfzeile, deutsche Beträge („- 5.637,53“), am Ende eine Summenzeile mit „*“.
/// Spalten werden über die Kopfzeile erkannt; deren Bezeichnungen schwanken (`Wert/KW`, `Wert/KWähr`, `KostenartenBez`).
/// </summary>
public static partial class KostenstellenauszugLeser
{
    public const long MaxBytes = 5 * 1024 * 1024;

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static Kostenstellenauszug Lesen(byte[] inhalt, string dateiname)
    {
        ArgumentNullException.ThrowIfNull(inhalt);
        var text = Dekodieren(inhalt);
        var zeilen = text.Split('\n').Select(z => z.TrimEnd('\r')).Where(z => z.Trim().Length > 0).ToList();
        if (zeilen.Count < 2)
        {
            throw new FachlicherFehler($"„{dateiname}“ enthält keine Buchungen.");
        }

        var kopf = zeilen[0].Split('\t').Select(Normalisieren).ToList();
        int Spalte(string name, params string[] anfaenge)
        {
            var i = kopf.FindIndex(k => anfaenge.Any(a => k.StartsWith(a, StringComparison.Ordinal)));
            return i >= 0 ? i : throw new FachlicherFehler($"„{dateiname}“ ist kein SAP-Kostenstellenauszug: Spalte „{name}“ fehlt in der Kopfzeile.");
        }

        var sKst = Spalte("Kostenst.", "kostenst");
        var sErfasst = kopf.FindIndex(k => k.StartsWith("erfa", StringComparison.Ordinal));
        var sBeleg = Spalte("Belegdatum", "belegdatum");
        var sBuch = Spalte("BuchDatum", "buchdatum", "buchungsdatum");
        var sArt = kopf.FindIndex(k => k == "kostenart");
        if (sArt < 0)
        {
            throw new FachlicherFehler($"„{dateiname}“ ist kein SAP-Kostenstellenauszug: Spalte „Kostenart“ fehlt in der Kopfzeile.");
        }

        var sArtText = Spalte("Kostenartenbezeichnung", "kostenartenbez");
        var sWert = Spalte("Wert", "wert");
        var sText = Spalte("Bezeichnung", "bezeichnung");
        var sRef = kopf.FindIndex(k => k.StartsWith("refbel", StringComparison.Ordinal));

        var fehler = new List<string>();
        var liste = new List<Auszugszeile>();
        decimal? summe = null;
        for (var i = 1; i < zeilen.Count; i++)
        {
            var z = zeilen[i].Split('\t').Select(c => c.Trim()).ToArray();
            string Zelle(int s) => s >= 0 && s < z.Length ? z[s] : string.Empty;
            if (Zelle(sKst) == "*")
            {
                summe = Betrag(Zelle(sWert), dateiname, i + 1);
                continue;
            }

            if (summe is not null)
            {
                fehler.Add($"Nach der Summenzeile folgen weitere Zeilen (Zeile {i + 1}).");
                break;
            }

            liste.Add(new Auszugszeile(liste.Count + 1, Zelle(sKst), Datum(Zelle(sErfasst), optional: true, dateiname, i + 1),
                Datum(Zelle(sBeleg), optional: false, dateiname, i + 1)!.Value, Datum(Zelle(sBuch), optional: false, dateiname, i + 1)!.Value,
                Zelle(sArt), Zelle(sArtText), Betrag(Zelle(sWert), dateiname, i + 1), Zelle(sText), Zelle(sRef) is { Length: > 0 } r ? r : null));
        }

        if (liste.Count == 0)
        {
            throw new FachlicherFehler($"„{dateiname}“ enthält keine Buchungen.");
        }

        var kst = liste.Select(l => l.Kostenstelle).Distinct().ToList();
        if (kst.Count > 1)
        {
            fehler.Add($"Der Auszug enthält mehrere Kostenstellen ({string.Join(", ", kst)}).");
        }

        var jahre = liste.Select(l => l.Buchungsdatum.Year).Distinct().Order().ToList();
        if (jahre.Count > 1)
        {
            fehler.Add($"Der Auszug enthält Buchungen aus mehreren Jahren ({string.Join(", ", jahre)}) – erwartet wird der Stand eines Jahres ab 01.01.");
        }

        var berechnet = liste.Sum(l => l.Betrag);
        if (summe is null)
        {
            fehler.Add("Die Summenzeile („*“) fehlt – die Datei ist womöglich unvollständig.");
        }
        else if (Math.Abs(summe.Value - berechnet) > 0.005m)
        {
            fehler.Add($"Die Summenzeile ({summe.Value.ToString("N2", De)}) passt nicht zur Summe der Buchungen ({berechnet.ToString("N2", De)}).");
        }

        if (!liste.Any(l => Domain.Finanzen.Kostenartgruppe.IstBudget(l.Kostenart)))
        {
            fehler.Add("Teilauszug: Die Budgetzeile („Budget Sachmitte Fak“, 930300) fehlt. Übernommen wird nur der vollständige Jahresstand ab 01.01. – "
                       + "einen Monatsausschnitt würde der Import sonst als ganzes Jahr behandeln.");
        }

        return new Kostenstellenauszug(dateiname, kst[0], jahre[^1], Exportdatum(dateiname), liste, summe ?? berechnet, fehler);
    }

    /// <summary>„20260622 12103012 Tabelle.xls“ → 22.06.2026.</summary>
    public static DateOnly? Exportdatum(string dateiname)
    {
        var m = DatumAmAnfang().Match(Path.GetFileName(dateiname ?? string.Empty));
        return m.Success && DateOnly.TryParseExact(m.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>UTF-16 mit oder ohne BOM (SAP-Export), sonst UTF-8 bzw. Windows-1252.</summary>
    internal static string Dekodieren(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        }

        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        }

        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        }

        // Ohne BOM: viele Nullbytes an ungeraden Stellen = UTF-16LE
        var stichprobe = Math.Min(b.Length, 400);
        var nullUngerade = 0;
        for (var i = 1; i < stichprobe; i += 2)
        {
            nullUngerade += b[i] == 0 ? 1 : 0;
        }

        if (stichprobe >= 4 && nullUngerade > stichprobe / 4)
        {
            return Encoding.Unicode.GetString(b, 0, b.Length - (b.Length % 2));
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(b);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(b);
        }
    }

    /// <summary>„- 5.637,53“ → −5637,53; „ 6,98“ → 6,98.</summary>
    internal static decimal Betrag(string s, string dateiname, int zeile)
    {
        var t = (s ?? string.Empty).Replace(" ", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        var negativ = t.StartsWith('-') || t.EndsWith('-');
        t = t.Trim('-');
        return decimal.TryParse(t, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, De, out var d)
            ? negativ ? -d : d
            : throw new FachlicherFehler($"„{dateiname}“, Zeile {zeile}: Betrag „{s}“ ist nicht lesbar.");
    }

    private static DateOnly? Datum(string s, bool optional, string dateiname, int zeile)
    {
        if (string.IsNullOrWhiteSpace(s) && optional)
        {
            return null;
        }

        return DateOnly.TryParseExact(s, ["dd.MM.yyyy", "d.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : optional ? null : throw new FachlicherFehler($"„{dateiname}“, Zeile {zeile}: Datum „{s}“ ist nicht lesbar.");
    }

    private static string Normalisieren(string s) => s.Trim().ToLowerInvariant().Replace("ß", "ss", StringComparison.Ordinal);

    [GeneratedRegex("^((?:19|20)[0-9]{6})")]
    private static partial Regex DatumAmAnfang();
}
