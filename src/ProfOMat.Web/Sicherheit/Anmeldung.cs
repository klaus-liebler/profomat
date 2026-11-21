using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using ProfOMat.Application;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Web.Sicherheit;

/// <summary>
/// Anmeldung per Cookie. Heute: lokale Konten (Kennung + Passwort) und in der Entwicklung die Testanmeldung.
/// Später zusätzlich SAML (Shibboleth-IdP der Hochschule) – dieselben Claims, siehe docs/spezifikation/architektur.md.
/// </summary>
internal static class Anmeldung
{
    public const string KontoClaim = ClaimTypes.NameIdentifier;
    public const string ArtClaim = "profomat:anmeldeart";

    public static ClaimsPrincipal Principal(Benutzer benutzer, string art, string schema)
    {
        ArgumentNullException.ThrowIfNull(benutzer);
        var claims = new List<Claim>
        {
            new(KontoClaim, benutzer.KontoIdPflicht.ToString("D")),
            new(ClaimTypes.Name, benutzer.Name),
            new("profomat:kennung", benutzer.Kennung),
            new(ArtClaim, art),
        };
        claims.AddRange(benutzer.Rollen.Select(r => new Claim(ClaimTypes.Role, r.ToString())));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, schema, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static Benutzer? AlsBenutzer(ClaimsPrincipal? p)
    {
        if (p?.Identity?.IsAuthenticated != true || !Guid.TryParse(p.FindFirst(KontoClaim)?.Value, out var id))
        {
            return null;
        }

        var rollen = p.FindAll(ClaimTypes.Role).Select(c => Enum.TryParse<Rolle>(c.Value, out var r) ? r : (Rolle?)null).OfType<Rolle>().ToList();
        return new Benutzer(p.FindFirst("profomat:kennung")?.Value ?? "?", p.Identity.Name ?? "?", id, rollen);
    }
}

/// <summary>Hält den Benutzer für Arbeitsbereiche, die außerhalb der Blazor-Verbindung erzeugt werden.</summary>
internal sealed class BenutzerKontext
{
    public Benutzer? Gesetzt { get; set; }
}

/// <summary>Ermittelt die angemeldete Person: gesetzter Kontext → Blazor-Anmeldung → HTTP-Anfrage → niemand.</summary>
internal sealed class AktuellerBenutzer(BenutzerKontext kontext, IServiceProvider dienste, IHttpContextAccessor http) : IAktuellerBenutzer
{
    public async ValueTask<Benutzer> ErmittelnAsync(CancellationToken abbruch = default)
    {
        if (kontext.Gesetzt is { } gesetzt)
        {
            return gesetzt;
        }

        if (dienste.GetService<AuthenticationStateProvider>() is { } anmeldung)
        {
            try
            {
                if (Anmeldung.AlsBenutzer((await anmeldung.GetAuthenticationStateAsync()).User) is { } ausBlazor)
                {
                    return ausBlazor;
                }
            }
            catch (InvalidOperationException)
            {
                // außerhalb einer Blazor-Verbindung
            }
        }

        return Anmeldung.AlsBenutzer(http.HttpContext?.User) ?? Benutzer.Niemand;
    }
}

/// <summary>
/// Führt eine Aktion in einem eigenen, kurzlebigen Arbeitsbereich (eigener DbContext) aus und behält den angemeldeten
/// Benutzer bei. Interaktive Seiten laden und speichern nur über Vorgänge (wie FraktionPerfekt).
/// </summary>
internal sealed class Vorgang(IServiceScopeFactory bereiche, IAktuellerBenutzer benutzer)
{
    public async Task<T> AusfuehrenAsync<T>(Func<IServiceProvider, Task<T>> arbeit)
    {
        ArgumentNullException.ThrowIfNull(arbeit);
        var wer = await benutzer.ErmittelnAsync();
        await using var bereich = bereiche.CreateAsyncScope();
        bereich.ServiceProvider.GetRequiredService<BenutzerKontext>().Gesetzt = wer;
        return await arbeit(bereich.ServiceProvider);
    }

    public Task AusfuehrenAsync(Func<IServiceProvider, Task> arbeit)
        => AusfuehrenAsync<bool>(async d =>
        {
            await arbeit(d);
            return true;
        });

    public ValueTask<Benutzer> BenutzerAsync() => benutzer.ErmittelnAsync();
}

/// <summary>Rücksprungziele nur innerhalb der Anwendung (kein offener Redirect).</summary>
internal static class Ruecksprung
{
    /// <summary>Nur lokale Ziele; die Pfad-Basis (z. B. „/profomat/“) wird entfernt.</summary>
    public static string Lokal(string? ziel, string basis = "/")
    {
        if (string.IsNullOrEmpty(ziel) || !ziel.StartsWith('/') || ziel.StartsWith("//", StringComparison.Ordinal) || ziel.Contains('\\', StringComparison.Ordinal))
        {
            return "./";
        }

        if (basis.Length > 1 && ziel.StartsWith(basis, StringComparison.OrdinalIgnoreCase))
        {
            ziel = "/" + ziel[basis.Length..];
        }

        return ziel.StartsWith("/anmelden", StringComparison.OrdinalIgnoreCase) ? "./" : ziel.TrimStart('/');
    }
}
