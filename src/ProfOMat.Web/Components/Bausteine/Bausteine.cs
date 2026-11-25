using ProfOMat.Domain.Drittpruefung;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Web.Components.Bausteine;

/// <summary>Ein sinnvoller Folgeschritt nach einer Aktion.</summary>
public sealed record NaechsterSchritt(string Titel, string Ziel, string? Hinweis = null, Freigabe? Freigabe = null, bool Hauptschritt = false);

internal static class Darstellung
{
    public static string Stil(AntragStatus status) => status switch
    {
        AntragStatus.Abgeschlossen => "gut",
        AntragStatus.BeimErstpruefer or AntragStatus.BeimStudiendekanat => "info",
        AntragStatus.WartetAufBestaetigung => "warn",
        AntragStatus.Abgelehnt or AntragStatus.Verfallen => "krit",
        _ => "neutral",
    };
}

/// <summary>Monatsfelder (&lt;input type="month"&gt;, Wert „2026-12“) und kurze Anzeigen für die Finanzen.</summary>
internal static class FinanzAnzeige
{
    public static DateOnly? Monat(string? wert)
        => DateOnly.TryParseExact((wert ?? string.Empty) + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    public static string MonatWert(DateOnly d) => d.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    public static DateOnly NaechsterMonat()
    {
        var heute = DateOnly.FromDateTime(DateTime.Today);
        return new DateOnly(heute.Year, heute.Month, 1).AddMonths(1);
    }
}
