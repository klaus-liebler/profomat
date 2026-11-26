using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace ProfOMat.Web.Components.Bausteine;

/// <summary>
/// Navigationslink, der bei Präfix-Abgleich einen Unterpfad ausnehmen kann – z. B. „Anträge“ (antraege) nicht hervorheben,
/// wenn „Einladung erstellen“ (antraege/neu) gerade offen ist.
/// </summary>
public sealed class MenueLink : NavLink
{
    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    /// <summary>Relativer Pfad, bei dem dieser Link nicht als aktiv gilt (samt Unterpfaden).</summary>
    [Parameter]
    public string? Ausser { get; set; }

    protected override bool ShouldMatch(string uriAbsolute)
    {
        if (!base.ShouldMatch(uriAbsolute))
        {
            return false;
        }

        if (string.IsNullOrEmpty(Ausser))
        {
            return true;
        }

        var pfad = Navigation.ToBaseRelativePath(uriAbsolute).Split('?', '#')[0].TrimEnd('/');
        return !(pfad.Equals(Ausser, StringComparison.OrdinalIgnoreCase) || pfad.StartsWith(Ausser + "/", StringComparison.OrdinalIgnoreCase));
    }
}
