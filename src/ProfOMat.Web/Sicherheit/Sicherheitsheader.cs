using System.Threading.RateLimiting;

namespace ProfOMat.Web.Sicherheit;

/// <summary>
/// Sicherheitsheader (OWASP Secure Headers) und Rate-Limits für die ohne Anmeldung erreichbaren Pfade (Anmeldung,
/// Links der Studierenden). Übernommen aus VibeVHS/FraktionPerfekt; einstellbar über <c>Sicherheit:RateLimitProMinute</c>.
/// </summary>
internal static class Sicherheitsheader
{
    /// <summary>
    /// Content-Security-Policy ohne 'unsafe-inline' für Skripte: nur eigene Dateien plus der SHA-256-Hash der Import-Map, die Blazor
    /// inline ausgibt. Style-Elemente nur aus eigenen Dateien; Style-Attribute bleiben erlaubt (Blazor und Radzen setzen Stile per
    /// Attribut – ohne Skriptausführung unkritisch).
    /// </summary>
    public static string Csp(string? importMapHash)
        => "default-src 'self'; "
           + $"script-src 'self'{(importMapHash is null ? "" : $" 'sha256-{importMapHash}'")}; "
           + "style-src 'self' 'unsafe-inline'; style-src-elem 'self'; style-src-attr 'unsafe-inline'; "
           + "img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' ws: wss:; worker-src 'self'; manifest-src 'self'; "
           + "frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> Hashes = new();

    /// <summary>SHA-256 (Base64) der Import-Map, wie <c>&lt;ImportMap /&gt;</c> sie ausgibt – mit LF-Zeilenenden, weil der
    /// HTML-Parser CRLF vor der Hash-Prüfung zu LF normalisiert.</summary>
    public static string? ImportMapHash(HttpContext kontext)
    {
        var definition = kontext.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Components.ImportMapDefinition>();
        return definition is null
            ? null
            : Hashes.GetValue(definition, d => HashVon(((Microsoft.AspNetCore.Components.ImportMapDefinition)d).ToString()));
    }

    /// <summary>CSP-Hash eines Inline-Skripts (Zeilenenden wie im Browser normalisiert).</summary>
    public static string HashVon(string inhalt)
    {
        ArgumentNullException.ThrowIfNull(inhalt);
        var normalisiert = inhalt.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalisiert)));
    }

    /// <summary>Ohne Anmeldung erreichbar: hier greift das Rate-Limit je Adresse.</summary>
    public static readonly string[] OeffentlichePfade = ["/anmelden", "/drittpruefung", "/einladung", "/Saml2", "/signin-oidc"];

    /// <summary>Seiten mit Personendaten, Abwesenheiten oder Protokoll: nie im Browser- oder Proxy-Cache ablegen.</summary>
    private static readonly string[] OhneCache = ["/antraege", "/drittpruefung", "/konten", "/protokoll", "/postausgang", "/profil", "/klausuren", "/revision"];

    public static IApplicationBuilder UseSicherheitsheader(this IApplicationBuilder app)
        => app.Use(async (kontext, weiter) =>
        {
            kontext.Response.OnStarting(() =>
            {
                var h = kontext.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["X-Frame-Options"] = "DENY";
                h["Referrer-Policy"] = "strict-origin-when-cross-origin";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                h["Content-Security-Policy"] = Csp(ImportMapHash(kontext));
                if (OhneCache.Any(p => kontext.Request.Path.StartsWithSegments(p)))
                {
                    h["Cache-Control"] = "no-store";
                }

                return Task.CompletedTask;
            });
            await weiter();
        });

    public static IServiceCollection AddRateLimits(this IServiceCollection dienste, IConfiguration konfiguration)
    {
        var proMinute = konfiguration.GetValue("Sicherheit:RateLimitProMinute", 60);
        return dienste.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (k, _) =>
            {
                k.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(k =>
            {
                var pfad = OeffentlichePfade.FirstOrDefault(p => k.Request.Path.StartsWithSegments(p));
                return pfad is null
                    ? RateLimitPartition.GetNoLimiter("frei")
                    : RateLimitPartition.GetFixedWindowLimiter($"{pfad}|{k.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = proMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
        });
    }
}
