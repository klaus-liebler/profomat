using System.Security.Cryptography;
using System.Text;
using ProfOMat.Application;
using ProfOMat.Application.Konten;
using ProfOMat.Infrastructure.Datenbank;
using ProfOMat.Infrastructure.Sicherheit;
using ProfOMat.Web.Betrieb;

namespace ProfOMat.Web.Sicherheit;

/// <summary>Sicherheitsprotokoll aus der Web-Schicht: Exporte, Konfigurationsänderungen, Sicherheitsbericht.</summary>
internal static class Sicherheitsereignisse
{
    /// <summary>Jeder Download (PDF, CSV, XLSX, ZIP, .eml, .ics) erscheint im Sicherheitsprotokoll – mit maskiertem Pfad und Konto-Id.</summary>
    public static RouteHandlerBuilder Exportprotokoll(this RouteHandlerBuilder endpunkt)
        => endpunkt.AddEndpointFilter(async (kontext, weiter) =>
        {
            var http = kontext.HttpContext;
            var ergebnis = await weiter(kontext);
            await http.RequestServices.GetRequiredService<ISicherheitsprotokoll>().EintragenAsync("Export",
                Anmeldung.AlsBenutzer(http.User)?.KontoId, Pfadmaske.Maskieren(http.Request.Path.Value), http.RequestAborted);
            return ergebnis;
        });

    /// <summary>Sicherheitsrelevante Schalter (ohne Geheimnisse); ihre Änderung wird beim Start protokolliert.</summary>
    public static readonly string[] Abschnitte = ["Anmeldung", "Saml", "Oidc", "Sicherheit", "Betrieb", "ReverseProxy", "PathBase", "ProfOMat:OeffentlicheAdresse"];

    private static readonly string[] Geheim = ["ClientSecret", "Passwort", "Password", "Secret", "Schluessel"];

    public static IReadOnlyDictionary<string, string> Schalter(IConfiguration k)
    {
        var werte = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in Abschnitte)
        {
            if (k[a] is { } einzeln)
            {
                werte[a] = einzeln;
            }

            foreach (var e in k.GetSection(a).AsEnumerable().Where(e => e.Value is not null))
            {
                werte[e.Key] = Geheim.Any(g => e.Key.Contains(g, StringComparison.OrdinalIgnoreCase)) ? "(gesetzt)" : e.Value!;
            }
        }

        return werte;
    }

    public static string Fingerabdruck(IReadOnlyDictionary<string, string> schalter)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', schalter.Select(s => $"{s.Key}={s.Value}"))))).ToLowerInvariant()[..16];

    /// <summary>Beim Start: Haben sich die Schalter seit dem letzten protokollierten Stand geändert? Dann ein Eintrag „Konfiguration“.</summary>
    public static async Task KonfigurationPruefenAsync(IServiceProvider dienste, IConfiguration k)
    {
        await using var bereich = dienste.CreateAsyncScope();
        var protokoll = bereich.ServiceProvider.GetRequiredService<ISicherheitsprotokoll>();
        var schalter = Schalter(k);
        var abdruck = Fingerabdruck(schalter);
        var letzte = (await protokoll.LetzteAsync(5000)).FirstOrDefault(e => e.Art == "Konfiguration");
        if (letzte?.Text.StartsWith(abdruck, StringComparison.Ordinal) == true)
        {
            return;
        }

        var vorher = letzte is null ? null : letzte.Text.Split('\n', 2) is [_, var rest] ? rest.Split('\n').ToHashSet(StringComparer.Ordinal) : [];
        var geaendert = vorher is null
            ? "erster protokollierter Stand"
            : string.Join(", ", schalter.Select(s => $"{s.Key}={s.Value}").Where(z => !vorher.Contains(z)).Select(z => z.Split('=')[0])
                .Concat(vorher.Select(z => z.Split('=')[0]).Where(n => !schalter.ContainsKey(n)))
                .Distinct(StringComparer.Ordinal));
        await protokoll.EintragenAsync("Konfiguration", null,
            abdruck + " (" + (string.IsNullOrEmpty(geaendert) ? "Reihenfolge" : geaendert) + ")\n" + string.Join('\n', schalter.Select(s => $"{s.Key}={s.Value}")));
    }

    /// <summary>Sicherheitsbericht für Prüfende (JSON): Verfahren, Schalter, Versionen, letzte Sicherung, Zertifikate, Protokollkette.</summary>
    public static async Task<object> BerichtAsync(IServiceProvider dienste, IConfiguration k, IWebHostEnvironment umgebung, string schluesselOrdner)
    {
        var angebot = dienste.GetRequiredService<Anmeldeangebot>();
        var kette = await dienste.GetRequiredService<ISicherheitsprotokoll>().PruefenAsync();
        var zertifikate = ProfOMat.Infrastructure.Betrieb.Betriebsregeln.ZertifikatPruefung(Betriebsdienste.Zertifikatdateien(k, schluesselOrdner), k.GetValue("Betrieb:Gesundheit:WarnTage", 30),
            DateTimeOffset.UtcNow);
        var sicherungsdatei = k["Betrieb:Sicherung:Statusdatei"] ?? "/var/backups/profomat/letzte-sicherung.txt";
        return new
        {
            erstellt = DateTimeOffset.UtcNow,
            dienst = Betriebsdienste.Dienstname,
            version = Betriebsdienste.Version,
            schemaversion = ProfOMatDbContext.Schemaversion,
            dotnet = Environment.Version.ToString(),
            umgebung = umgebung.EnvironmentName,
            anmeldung = new
            {
                verfahren = angebot.Verfahren.Select(v => v.ToString()).Order(),
                testanmeldung = angebot.Testanmeldung,
                zweiterFaktorPflicht = Anmelderegeln.RollenMitPflichtZumZweitenFaktor.Select(r => r.ToString()).Order(),
                mfaBeiSso = k.GetValue("Anmeldung:MfaBeiSsoVerlangen", true),
            },
            netz = new
            {
                verwaltungNetze = k.GetSection(Netzbeschraenkung.Schluessel).Get<string[]>() ?? [],
                monitoringNetze = k.GetSection("Betrieb:Metriken:Netze").Get<string[]>() ?? [],
                otlp = !string.IsNullOrWhiteSpace(k["Betrieb:Otlp:Endpunkt"]),
                hinterProxy = k.GetValue("ReverseProxy:Aktiv", false),
            },
            feldverschluesselung = new
            {
                aktiv = Feldverschluesselung.Ring is not null,
                schluesselversion = Feldverschluesselung.Ring?.AktuelleVersion,
                quelle = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY") is { Length: > 0 } c && File.Exists(Path.Combine(c, Feldverschluesselung.Dateiname))
                    ? "systemd-Credential"
                    : File.Exists(Path.Combine("/run/secrets", Feldverschluesselung.Dateiname)) ? "Docker Secret" : "Schlüsselordner",
                spalten = ProfOMatDbContext.VerschluesselteSpalten.Select(s => $"{s.Tabelle}.{s.Spalte}"),
            },
            letzteSicherung = File.Exists(sicherungsdatei) ? File.ReadAllText(sicherungsdatei).Trim() : "unbekannt (keine Statusdatei)",
            zertifikate = new { status = zertifikate.Status.ToString(), zertifikate.Description, details = zertifikate.Data },
            sicherheitsprotokoll = new { kette.Intakt, kette.Anzahl, kette.ErsterFehler, kette.Grund },
            schalter = Schalter(k),
        };
    }
}
