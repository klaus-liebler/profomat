using ClosedXML.Excel;
using ProfOMat.Application.Finanzen;
using ProfOMat.Domain.Finanzen;

namespace ProfOMat.Infrastructure.Dokumente;

/// <summary>Dateien zu den Finanzen (XLSX über ClosedXML).</summary>
public sealed class FinanzDokumente : IFinanzDokumente
{
    private const string Euro = "#,##0.00 €";

    public byte[] AuslagenXlsx(Auslagenabrechnung abrechnung, IReadOnlyDictionary<Guid, Topf> toepfe, string person)
    {
        ArgumentNullException.ThrowIfNull(abrechnung);
        ArgumentNullException.ThrowIfNull(toepfe);
        using var mappe = new XLWorkbook();
        var uebersicht = mappe.Worksheets.Add("Übersicht");
        uebersicht.Cell(1, 1).Value = abrechnung.Bezeichnung;
        uebersicht.Cell(1, 1).Style.Font.FontSize = 16;
        uebersicht.Cell(2, 1).Value = $"Vorfinanziert von {person}" + (abrechnung.Eingereicht is { } e ? $" · eingereicht am {e:dd.MM.yyyy}" : "");
        Kopf(uebersicht, 4, "Topf", "Art", "Kennung / Innenauftrag", "Positionen", "Summe");
        var zeile = 5;
        var gruppen = abrechnung.PositionenSortiert.GroupBy(p => p.TopfId)
            .OrderBy(g => toepfe.GetValueOrDefault(g.Key)?.Art).ThenBy(g => toepfe.GetValueOrDefault(g.Key)?.Kennung, StringComparer.Ordinal).ToList();
        foreach (var g in gruppen)
        {
            var t = toepfe.GetValueOrDefault(g.Key);
            uebersicht.Cell(zeile, 1).Value = t?.Bezeichnung ?? "unbekannt";
            uebersicht.Cell(zeile, 2).Value = t?.Art switch { Topfart.Kostenstelle => "Kostenstelle", Topfart.Investition => "Investition", Topfart.Sqm => "SQM", _ => "" };
            uebersicht.Cell(zeile, 3).Value = t is null ? "" : t.Innenauftrag is { } ia ? $"{t.Kennung} / {ia}" : t.Kennung;
            uebersicht.Cell(zeile, 4).Value = g.Count();
            uebersicht.Cell(zeile, 5).Value = g.Sum(p => p.Betrag);
            uebersicht.Cell(zeile, 5).Style.NumberFormat.Format = Euro;
            zeile++;
        }

        uebersicht.Cell(zeile, 1).Value = "Summe";
        uebersicht.Cell(zeile, 4).Value = abrechnung.Positionen.Count;
        uebersicht.Cell(zeile, 5).Value = abrechnung.Summe;
        uebersicht.Cell(zeile, 5).Style.NumberFormat.Format = Euro;
        uebersicht.Row(zeile).Style.Font.Bold = true;
        uebersicht.Columns().AdjustToContents();

        foreach (var g in gruppen)
        {
            var t = toepfe.GetValueOrDefault(g.Key);
            var blatt = mappe.Worksheets.Add(Blattname(t?.Kennung ?? "unbekannt", mappe));
            blatt.Cell(1, 1).Value = $"{t?.Kennung} – {t?.Bezeichnung}";
            blatt.Cell(1, 1).Style.Font.Bold = true;
            Kopf(blatt, 3, "Nr.", "Kaufdatum", "Händler", "Artikel", "Betrag (brutto)");
            var z = 4;
            foreach (var p in g)
            {
                blatt.Cell(z, 1).Value = p.Nr;
                blatt.Cell(z, 2).Value = p.Kaufdatum.ToDateTime(TimeOnly.MinValue);
                blatt.Cell(z, 2).Style.DateFormat.Format = "dd.MM.yyyy";
                blatt.Cell(z, 3).Value = p.Haendler;
                blatt.Cell(z, 4).Value = p.Artikel;
                blatt.Cell(z, 5).Value = p.Betrag;
                blatt.Cell(z, 5).Style.NumberFormat.Format = Euro;
                z++;
            }

            blatt.Cell(z, 4).Value = "Summe";
            blatt.Cell(z, 5).Value = g.Sum(p => p.Betrag);
            blatt.Cell(z, 5).Style.NumberFormat.Format = Euro;
            blatt.Row(z).Style.Font.Bold = true;
            blatt.Columns().AdjustToContents();
        }

        using var ziel = new MemoryStream();
        mappe.SaveAs(ziel);
        return ziel.ToArray();
    }

    private static void Kopf(IXLWorksheet blatt, int zeile, params string[] texte)
    {
        for (var i = 0; i < texte.Length; i++)
        {
            blatt.Cell(zeile, i + 1).Value = texte[i];
        }

        blatt.Row(zeile).Style.Font.Bold = true;
    }

    /// <summary>Blattnamen: höchstens 31 Zeichen, ohne : \ / ? * [ ], eindeutig.</summary>
    private static string Blattname(string name, XLWorkbook mappe)
    {
        var s = new string([.. name.Where(c => !":\\/?*[]".Contains(c, StringComparison.Ordinal))]);
        s = s.Length == 0 ? "Topf" : s.Length > 28 ? s[..28] : s;
        var ergebnis = s;
        for (var i = 2; mappe.Worksheets.Contains(ergebnis); i++)
        {
            ergebnis = $"{s} {i}";
        }

        return ergebnis;
    }
}
