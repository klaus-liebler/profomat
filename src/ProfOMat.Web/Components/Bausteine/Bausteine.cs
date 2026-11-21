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
