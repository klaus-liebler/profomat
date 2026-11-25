using System.Globalization;
using ProfOMat.Domain.Finanzen;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Application.Finanzen;

/// <summary>Beträge in der Oberfläche: „1.234,56 €“ anzeigen, „1234,56“ oder „1.234,56 €“ lesen.</summary>
public static class Geld
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Text(decimal betrag) => Finanzrechnung.Euro(betrag);

    public static string Text(decimal? betrag) => betrag is { } b ? Text(b) : "–";

    /// <summary>Für Eingabefelder: „1234,56“ (ohne Tausenderpunkte und Zeichen).</summary>
    public static string Eingabe(decimal betrag) => betrag.ToString("0.##", De);

    public static decimal Lesen(string? text, string feld)
    {
        var t = (text ?? string.Empty).Replace("€", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim();
        if (t.Length == 0)
        {
            return 0;
        }

        return decimal.TryParse(t, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, De, out var d)
            ? decimal.Round(d, 2)
            : throw new FachlicherFehler($"{feld}: „{text}“ ist kein Betrag (z. B. 1234,56).");
    }
}
