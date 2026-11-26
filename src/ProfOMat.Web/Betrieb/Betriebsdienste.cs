using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ProfOMat.Infrastructure.Betrieb;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Web.Betrieb;

/// <summary>
/// Betrieb nach dem Zielbild (Abschnitt 5): OpenTelemetry (Traces und Metriken per OTLP, Ziel per Konfiguration, Standard aus),
/// Prometheus-Endpunkt <c>/metrics</c> nur aus konfigurierten Monitoring-Netzen, Gesundheitsendpunkte und <c>security.txt</c>.
/// Konfiguration (Abschnitt „Betrieb“):
///   Otlp:Endpunkt          z. B. http://otel-collector:4317 – leer = kein Versand
///   Otlp:Protokoll         grpc (Vorgabe) oder http
///   Metriken:Netze         CIDR-Liste, z. B. 10.10.0.0/24 – leer = kein /metrics
///   Gesundheit:MinFreiMb   Mindestplatz im Datenordner (Vorgabe 500)
///   Gesundheit:Zertifikate weitere PEM-/CRT-Dateien, deren Ablauf überwacht wird (z. B. TLS-Zertifikat)
///   Gesundheit:WarnTage    Ablauf in weniger Tagen = „beeinträchtigt“ (Vorgabe 30)
///   SecurityTxt:Kontakt    mailto:… oder https://… – leer = kein security.txt
/// </summary>
internal static class Betriebsdienste
{
    public const string Abschnitt = "Betrieb";
    public const string Dienstname = "ProfOMat";

    public static string Version => typeof(Betriebsdienste).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static IServiceCollection AddProfOMatBetrieb(this IServiceCollection dienste, IConfiguration k, string datenOrdner, string schluesselOrdner)
    {
        var otlp = k[$"{Abschnitt}:Otlp:Endpunkt"];
        var netze = MonitoringNetze(k);
        if (!string.IsNullOrWhiteSpace(otlp) || netze.Count > 0)
        {
            var otel = dienste.AddOpenTelemetry().ConfigureResource(r => r.AddService(Dienstname, serviceVersion: Version));
            otel.WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMeter(Kennzahlen.Name);
                if (netze.Count > 0)
                {
                    m.AddPrometheusExporter();
                }

                if (!string.IsNullOrWhiteSpace(otlp))
                {
                    m.AddOtlpExporter(o => Otlp(o, k, otlp));
                }
            });
            if (!string.IsNullOrWhiteSpace(otlp))
            {
                // Traces nur mit Ziel; Pfade mit Tokens (Studierendenlinks) werden in der Spanne maskiert
                otel.WithTracing(t => t
                    .AddAspNetCoreInstrumentation(o => o.EnrichWithHttpRequest = (aktivitaet, anfrage) =>
                    {
                        aktivitaet.SetTag("url.path", Pfadmaske.Maskieren(anfrage.Path.Value));
                        aktivitaet.DisplayName = $"{anfrage.Method} {Pfadmaske.Maskieren(anfrage.Path.Value)}";
                    })
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(o => Otlp(o, k, otlp)));
            }
        }

        dienste.AddSingleton<Kennzahlen>();
        dienste.AddHealthChecks()
            .AddCheck<DatenbankPruefung>("datenbank", tags: ["bereit"])
            .AddCheck("speicherplatz", () => Betriebsregeln.PlatzPruefung(datenOrdner, k.GetValue($"{Abschnitt}:Gesundheit:MinFreiMb", 500)), tags: ["bereit"])
            .AddCheck("zertifikate", () => Betriebsregeln.ZertifikatPruefung(Zertifikatdateien(k, schluesselOrdner), k.GetValue($"{Abschnitt}:Gesundheit:WarnTage", 30), DateTimeOffset.UtcNow),
                tags: ["bereit"]);
        return dienste;
    }

    private static void Otlp(OpenTelemetry.Exporter.OtlpExporterOptions o, IConfiguration k, string endpunkt)
    {
        o.Endpoint = new Uri(endpunkt);
        o.Protocol = string.Equals(k[$"{Abschnitt}:Otlp:Protokoll"], "http", StringComparison.OrdinalIgnoreCase)
            ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf
            : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
    }

    /// <summary>Ordner der SQLite-Datei (für die Platzprüfung); bei In-Memory-Verbindungen der Inhaltsordner.</summary>
    public static string DatenOrdner(IConfiguration k, string inhaltsordner)
    {
        var quelle = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(k.GetConnectionString("ProfOMat") ?? "Data Source=data/profomat.db").DataSource;
        return string.IsNullOrEmpty(quelle) || quelle == ":memory:" || quelle.StartsWith("file:", StringComparison.Ordinal)
            ? inhaltsordner
            : Path.GetDirectoryName(Path.GetFullPath(Path.Combine(inhaltsordner, quelle))) ?? inhaltsordner;
    }

    public static IReadOnlyList<IPNetwork> MonitoringNetze(IConfiguration k) => Netze(k.GetSection($"{Abschnitt}:Metriken:Netze").Get<string[]>());

    public static IReadOnlyList<IPNetwork> Netze(IEnumerable<string>? liste) => Betriebsregeln.Netze(liste);

    public static bool InNetz(IPAddress? adresse, IReadOnlyList<IPNetwork> netze) => Betriebsregeln.InNetz(adresse, netze);

    /// <summary>Zertifikate, deren Ablauf beobachtet wird: SAML-SP-Zertifikat (falls vorhanden) und konfigurierte Dateien.</summary>
    public static IReadOnlyList<string> Zertifikatdateien(IConfiguration k, string schluesselOrdner)
    {
        var liste = new List<string>();
        var saml = k["Saml:Zertifikat"] is { Length: > 0 } p ? p : Path.Combine(schluesselOrdner, "saml-sp.pfx");
        if (File.Exists(saml))
        {
            liste.Add(saml);
        }

        liste.AddRange(k.GetSection($"{Abschnitt}:Gesundheit:Zertifikate").Get<string[]>() ?? []);
        return liste;
    }

    public static void MapProfOMatBetrieb(this WebApplication app)
    {
        // Bisher: das Deploy-Skript wartet auf „ok“ – bleibt unverändert
        app.MapGet("/gesund", async (ProfOMatDbContext db) => await db.Database.CanConnectAsync()
            ? Results.Text("ok")
            : Results.Text("Datenbank nicht erreichbar", statusCode: StatusCodes.Status503ServiceUnavailable)).AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks("/gesund/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = Antwort }).AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks("/gesund/bereit", new HealthCheckOptions { Predicate = c => c.Tags.Contains("bereit"), ResponseWriter = Antwort })
            .AllowAnonymous().DisableRateLimiting();
        app.MapGet("/gesund/version", () => Results.Json(new
        {
            dienst = Dienstname,
            version = Version,
            schemaversion = ProfOMatDbContext.Schemaversion,
            dotnet = Environment.Version.ToString(),
        })).AllowAnonymous().DisableRateLimiting();

        var netze = MonitoringNetze(app.Configuration);
        if (netze.Count > 0)
        {
            // nur aus den Monitoring-Netzen; andere Adressen sehen /metrics nicht (404)
            app.UseOpenTelemetryPrometheusScrapingEndpoint(kontext => kontext.Request.Path == "/metrics" && InNetz(kontext.Connection.RemoteIpAddress, netze));
        }

        app.MapGet("/.well-known/security.txt", (IConfiguration k) => Betriebsregeln.SecurityTxt(k[$"{Abschnitt}:SecurityTxt:Kontakt"], k[$"{Abschnitt}:SecurityTxt:Ablauf"], k[$"{Abschnitt}:SecurityTxt:Richtlinie"], DateTimeOffset.UtcNow) is { } text
            ? Results.Text(text, "text/plain; charset=utf-8")
            : Results.NotFound()).AllowAnonymous();
    }

    private static Task Antwort(HttpContext kontext, HealthReport bericht)
    {
        kontext.Response.ContentType = "application/json; charset=utf-8";
        kontext.Response.Headers.CacheControl = "no-store";
        return kontext.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = bericht.Status.ToString(),
            pruefungen = bericht.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), beschreibung = e.Value.Description, daten = e.Value.Data }),
        }));
    }
}

/// <summary>Datenbank erreichbar?</summary>
internal sealed class DatenbankPruefung(ProfOMatDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy("erreichbar") : HealthCheckResult.Unhealthy("nicht erreichbar");
}

/// <summary>Eigene Kennzahlen (Meter „ProfOMat“), ohne Personendaten.</summary>
internal sealed class Kennzahlen : IDisposable
{
    public const string Name = "ProfOMat";

    private readonly System.Diagnostics.Metrics.Meter meter = new(Name, Betriebsdienste.Version);

    public Kennzahlen()
    {
        Anmeldungen = meter.CreateCounter<long>("profomat.anmeldungen", description: "Erfolgreiche Anmeldungen je Verfahren");
        Fehlversuche = meter.CreateCounter<long>("profomat.anmeldung.fehlversuche", description: "Fehlgeschlagene Anmeldeversuche");
    }

    public System.Diagnostics.Metrics.Counter<long> Anmeldungen { get; }

    public System.Diagnostics.Metrics.Counter<long> Fehlversuche { get; }

    public void Dispose() => meter.Dispose();
}

/// <summary>Pfade für Logs und Traces: geheime Tokens maskieren (siehe Betriebsregeln).</summary>
internal static class Pfadmaske
{
    public static string Maskieren(string? pfad) => Betriebsregeln.PfadMaskieren(pfad);
}
