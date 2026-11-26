using System.Net;
using ProfOMat.Web.Betrieb;

namespace ProfOMat.Web.Sicherheit;

/// <summary>
/// Verwaltung auf Netze beschränken (Zielbild Abschnitt 1, je Instanz schaltbar, Standard aus): Ist <c>Sicherheit:VerwaltungNetze</c>
/// gesetzt (CIDR-Liste, z. B. Hochschulnetz und VPN), beantwortet ProfOMat Anfragen von anderswo nur für die öffentlichen Teile –
/// die persönlichen Links der Studierenden samt dem, was ihre Seiten zum Laufen brauchen – und sonst mit 403.
/// Hinter dem Proxy zählt die Adresse aus X-Forwarded-For (nur aus vertrauenswürdigen Netzen, siehe ReverseProxy).
/// </summary>
internal static class Netzbeschraenkung
{
    public const string Schluessel = "Sicherheit:VerwaltungNetze";

    public static bool Erlaubt(PathString pfad, IPAddress? adresse, IReadOnlyList<IPNetwork> netze)
        => ProfOMat.Infrastructure.Betrieb.Betriebsregeln.Erlaubt(pfad.Value, adresse, netze);

    public static IApplicationBuilder UseNetzbeschraenkung(this IApplicationBuilder app, IConfiguration k)
    {
        var netze = Betriebsdienste.Netze(k.GetSection(Schluessel).Get<string[]>());
        if (netze.Count == 0)
        {
            return app;
        }

        return app.Use(async (kontext, weiter) =>
        {
            if (Erlaubt(kontext.Request.Path, kontext.Connection.RemoteIpAddress, netze))
            {
                await weiter();
                return;
            }

            kontext.Response.StatusCode = StatusCodes.Status403Forbidden;
            kontext.Response.ContentType = "text/plain; charset=utf-8";
            await kontext.Response.WriteAsync("Die Verwaltung von ProfOMat ist nur aus dem Hochschulnetz bzw. VPN erreichbar. "
                                              + "Persönliche Links für Studierende funktionieren von überall.");
        });
    }
}
