using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ProfOMat.Application.Finanzen;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Infrastructure.Dokumente;

/// <summary>
/// Liest die SQM-Liste der Fakultät („SQM-IuI-2026-Antraege-bewilligt-aktuell.xlsx“): ein Blatt je Laborbereich bzw. Dekanat, Kopfzeile mit
/// „Antrag-Nr.“; Spalten über die Kopfzeile (Antragsteller, Bezeichnung, Laufzeit, Personal-/Sachkosten, Gesamtsumme, Kategorie, Verausgabt).
/// </summary>
public sealed partial class SqmListenLeser : ISqmListenLeser
{
    public SqmListe Lesen(byte[] inhalt)
    {
        ArgumentNullException.ThrowIfNull(inhalt);
        XLWorkbook mappe;
        try
        {
            mappe = new XLWorkbook(new MemoryStream(inhalt));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FileFormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new FachlicherFehler("Die Datei ist keine lesbare Excel-Datei (.xlsx).", ex);
        }

        using (mappe)
        {
            var zeilen = new List<SqmZeile>();
            foreach (var blatt in mappe.Worksheets)
            {
                var kopf = blatt.RowsUsed().Take(15).FirstOrDefault(r => r.CellsUsed().Any(c => Norm(c.GetString()).StartsWith("antrag-nr", StringComparison.Ordinal)));
                if (kopf is null)
                {
                    continue;
                }

                var spalten = kopf.CellsUsed().ToDictionary(c => c.Address.ColumnNumber, c => Norm(c.GetString()));
                int? Spalte(params string[] anfaenge) => spalten.Where(s => anfaenge.Any(a => s.Value.StartsWith(a, StringComparison.Ordinal))).Select(s => (int?)s.Key).FirstOrDefault();
                var sNr = Spalte("antrag-nr")!.Value;
                var sWer = Spalte("antragssteller", "antragsteller");
                var sBez = Spalte("bezeichnung");
                var sLauf = Spalte("laufzeit");
                var sPers = Spalte("personal-kosten", "personalkosten");
                var sSach = Spalte("sachkosten");
                var sGes = Spalte("gesamtsum");
                var sKat = Spalte("kategorie");
                var sVer = Spalte("verausgabt");

                foreach (var r in blatt.RowsUsed().Where(r => r.RowNumber() > kopf.RowNumber()))
                {
                    var nr = r.Cell(sNr).GetString().Trim().ToUpperInvariant();
                    if (!SqmNummer().IsMatch(nr))
                    {
                        continue;
                    }

                    string? T(int? s) => s is { } x && r.Cell(x).GetString().Trim() is { Length: > 0 } t ? t : null;
                    decimal Z(int? s) => s is { } x ? Zahl(r.Cell(x)) ?? 0 : 0;
                    zeilen.Add(new SqmZeile(blatt.Name, nr, T(sWer) ?? "", T(sBez) ?? nr, T(sLauf), Z(sPers), Z(sSach), T(sKat), Z(sGes),
                        sVer is { } v ? Zahl(r.Cell(v)) : null));
                }
            }

            if (zeilen.Count == 0)
            {
                throw new FachlicherFehler("In der Datei steht kein SQM-Antrag (Spalte „Antrag-Nr.“ mit Nummern wie SQM-2026-15).");
            }

            var geaendert = mappe.Properties.Modified;
            return new SqmListe(zeilen, geaendert.Year > 2000 ? DateOnly.FromDateTime(geaendert) : null);
        }
    }

    private static decimal? Zahl(IXLCell c)
    {
        if (c.DataType == XLDataType.Number)
        {
            return decimal.Round((decimal)c.GetDouble(), 2);
        }

        var s = c.GetString().Replace("€", "", StringComparison.Ordinal).Trim();
        return s.Length == 0 ? null
            : decimal.TryParse(s, NumberStyles.Number, CultureInfo.GetCultureInfo("de-DE"), out var d) ? decimal.Round(d, 2) : null;
    }

    private static string Norm(string s) => s.Trim().ToLowerInvariant();

    [GeneratedRegex("^SQM-[0-9]{4}-[0-9]{1,3}[A-Z]?$")]
    private static partial Regex SqmNummer();
}
