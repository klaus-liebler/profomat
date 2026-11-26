using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using ProfOMat.Application;
using ProfOMat.Domain.Konten;
using ProfOMat.Infrastructure.Sicherheit;

namespace ProfOMat.Web.Sicherheit;

/// <summary>
/// Anmeldung über ASP.NET Core Identity (Cookie „profomat.anmeldung“). Die Claims erzeugt die Claims-Fabrik der Infrastruktur:
/// Konto-Id, Anzeigename, Kennung, Rollen, Sicherheitsstempel; dazu die Anmeldeart (<see cref="ClaimTypes.AuthenticationMethod"/>) und
/// „amr“ = „mfa“, wenn ein zweiter Faktor nachgewiesen ist. Siehe docs/spezifikation/architektur.md.
/// </summary>
internal static class Anmeldung
{
    public const string KontoClaim = ClaimTypes.NameIdentifier;

    /// <summary>Claim-Typ des Sicherheitsstempels (Identity-Vorgabe).</summary>
    public static readonly string StempelClaim = new ClaimsIdentityOptions().SecurityStampClaimType;

    public static Benutzer? AlsBenutzer(ClaimsPrincipal? p)
    {
        if (p?.Identity?.IsAuthenticated != true || !Guid.TryParse(p.FindFirst(KontoClaim)?.Value, out var id))
        {
            return null;
        }

        var rollen = p.FindAll(ClaimTypes.Role).Select(c => Enum.TryParse<Rolle>(c.Value, out var r) ? r : (Rolle?)null).OfType<Rolle>().ToList();
        return new Benutzer(p.FindFirst(Identitaet.KennungClaim)?.Value ?? "?", p.Identity.Name ?? "?", id, rollen);
    }
}

/// <summary>
/// Sitzung noch gültig? Das Konto muss aktiv sein und der Sicherheitsstempel von Identity im Cookie zum aktuellen Stand passen
/// (Passwortwechsel, Rollenänderung, Sperre, zweiter Faktor oder Verknüpfungen beenden alle bisherigen Sitzungen). Läuft bei jeder
/// Anfrage (eine schmale Abfrage) und jede Minute für offene Blazor-Verbindungen – strenger als der SecurityStampValidator von Identity.
/// </summary>
internal static class Sitzung
{
    public static async Task<bool> GueltigAsync(ClaimsPrincipal? p, IServiceProvider dienste, CancellationToken abbruch = default)
    {
        if (p?.Identity?.IsAuthenticated != true)
        {
            return true;   // nicht angemeldet: nichts zu prüfen
        }

        if (!Guid.TryParse(p.FindFirst(Anmeldung.KontoClaim)?.Value, out var id) || p.FindFirst(Anmeldung.StempelClaim)?.Value is not { } stempel)
        {
            return false;
        }

        var aktuell = await dienste.GetRequiredService<ProfOMat.Application.Konten.KontoDienst>().StempelAsync(id, abbruch);
        return string.Equals(aktuell, stempel, StringComparison.Ordinal);
    }
}

/// <summary>Prüft laufende Blazor-Verbindungen jede Minute nach (gesperrte Konten verlieren auch eine offene Seite).</summary>
internal sealed class Sitzungspruefung(ILoggerFactory log, IServiceScopeFactory bereiche)
    : Microsoft.AspNetCore.Components.Server.RevalidatingServerAuthenticationStateProvider(log)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState zustand, CancellationToken abbruch)
    {
        ArgumentNullException.ThrowIfNull(zustand);
        await using var bereich = bereiche.CreateAsyncScope();
        return await Sitzung.GueltigAsync(zustand.User, bereich.ServiceProvider, abbruch);
    }
}

/// <summary>
/// Schutz gegen Passwort-Raten (wie Podium): je IP-Adresse und Kennung ab dem 5. Fehlversuch eine Wartezeit,
/// die sich verdoppelt (15 s, 30 s, … höchstens 5 min). Erfolgreiche Anmeldung setzt zurück.
/// </summary>
internal sealed class Anmeldebremse(TimeProvider uhr)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Fehler, DateTimeOffset Bis)> stand = new(StringComparer.Ordinal);

    private static string Schluessel(string? ip, string kennung) => $"{ip}|{kennung.Trim().ToLowerInvariant()}";

    /// <summary>Verbleibende Wartezeit; <c>null</c> = darf versuchen.</summary>
    public TimeSpan? Wartezeit(string? ip, string kennung)
        => stand.TryGetValue(Schluessel(ip, kennung), out var s) && s.Bis > uhr.GetUtcNow() ? s.Bis - uhr.GetUtcNow() : null;

    public void Fehlversuch(string? ip, string kennung)
    {
        var jetzt = uhr.GetUtcNow();
        stand.AddOrUpdate(Schluessel(ip, kennung), _ => (1, DateTimeOffset.MinValue), (_, s) =>
        {
            var n = s.Fehler + 1;
            return (n, n >= 5 ? jetzt.AddSeconds(Math.Min(300, 15 * Math.Pow(2, n - 5))) : DateTimeOffset.MinValue);
        });
        if (stand.Count > 10_000)
        {
            foreach (var alt in stand.Where(x => x.Value.Bis < jetzt.AddHours(-1)).Select(x => x.Key).ToList())
            {
                stand.TryRemove(alt, out _);
            }
        }
    }

    public void Erfolg(string? ip, string kennung) => stand.TryRemove(Schluessel(ip, kennung), out _);
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
