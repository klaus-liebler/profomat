using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ProfOMat.Infrastructure.Betrieb;

/// <summary>Regeln des Betriebs ohne HTTP-Abhängigkeit (damit sie sich testen lassen): Netze, Pfadmaske, Gesundheit, security.txt.</summary>
public static class Betriebsregeln
{
    /// <summary>Für alle erreichbar, auch wenn die Verwaltung auf Netze beschränkt ist: Studierendenlinks und was ihre Seiten brauchen.</summary>
    private static readonly string[] OeffentlichePraefixe =
    [
        "/drittpruefung/", "/hausarbeit/bestaetigen/", "/hausarbeit/kalender/", "/_blazor", "/_framework/", "/_content/",
        "/gesund", "/.well-known/", "/not-found", "/Error", "/metrics",   // /metrics prüft seine eigenen Monitoring-Netze
    ];

    public static IReadOnlyList<IPNetwork> Netze(IEnumerable<string>? liste)
        => (liste ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => IPNetwork.Parse(n.Trim())).ToList();

    public static bool InNetz(IPAddress? adresse, IReadOnlyList<IPNetwork> netze)
    {
        if (adresse is null)
        {
            return false;
        }

        var a = adresse.IsIPv4MappedToIPv6 ? adresse.MapToIPv4() : adresse;
        return netze.Any(n => n.Contains(a));
    }

    /// <summary>Öffentliche Teile: Studierendenlinks samt PDFs/Kalender, Blazor, statische Dateien im Wurzelordner, Gesundheit, security.txt.</summary>
    public static bool IstOeffentlich(string? pfad)
    {
        var p = string.IsNullOrEmpty(pfad) ? "/" : pfad;
        if (OeffentlichePraefixe.Any(o => p.StartsWith(o, StringComparison.OrdinalIgnoreCase) || p.Equals(o.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var letzter = p[(p.LastIndexOf('/') + 1)..];
        return p.LastIndexOf('/') == 0 && letzter.Contains('.', StringComparison.Ordinal)
               && !letzter.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) && !letzter.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ohne konfigurierte Netze ist alles erlaubt; sonst öffentliche Teile von überall, der Rest nur aus den Netzen.</summary>
    public static bool Erlaubt(string? pfad, IPAddress? adresse, IReadOnlyList<IPNetwork> netze)
        => netze.Count == 0 || IstOeffentlich(pfad) || InNetz(adresse, netze);

    /// <summary>Pfade für Logs und Traces: geheime Tokens (Studierendenlinks, Kalender-Abo) durch „…“ ersetzen; Ids (GUID) bleiben.</summary>
    public static string PfadMaskieren(string? pfad)
    {
        if (string.IsNullOrEmpty(pfad))
        {
            return "/";
        }

        var teile = pfad.Split('/');
        for (var i = 0; i < teile.Length; i++)
        {
            var t = teile[i];
            var ohneEndung = t.Contains('.', StringComparison.Ordinal) ? t[..t.IndexOf('.', StringComparison.Ordinal)] : t;
            if (ohneEndung.Length >= 20 && !Guid.TryParse(ohneEndung, out _) && ohneEndung.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                teile[i] = "…" + t[ohneEndung.Length..];
            }
        }

        return string.Join('/', teile);
    }

    public static HealthCheckResult PlatzPruefung(string ordner, long minFreiMb)
    {
        try
        {
            var frei = new DriveInfo(Path.GetFullPath(ordner)).AvailableFreeSpace / (1024 * 1024);
            var daten = new Dictionary<string, object> { ["frei_mb"] = frei, ["mindestens_mb"] = minFreiMb };
            return frei >= minFreiMb
                ? HealthCheckResult.Healthy($"{frei} MB frei", daten)
                : HealthCheckResult.Unhealthy($"nur {frei} MB frei (mindestens {minFreiMb})", data: daten);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Degraded("Speicherplatz nicht ermittelbar", ex);
        }
    }

    /// <summary>Abgelaufen = ungesund, Ablauf binnen <paramref name="warnTage"/> = beeinträchtigt, nicht lesbar = beeinträchtigt.</summary>
    public static HealthCheckResult ZertifikatPruefung(IReadOnlyList<string> dateien, int warnTage, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(dateien);
        var daten = new Dictionary<string, object>();
        var schlechtester = HealthStatus.Healthy;
        foreach (var datei in dateien)
        {
            try
            {
                using var z = Laden(datei);
                var tage = (int)Math.Floor((new DateTimeOffset(z.NotAfter.ToUniversalTime()) - jetzt).TotalDays);
                daten[Path.GetFileName(datei)] = $"gültig bis {z.NotAfter.ToUniversalTime():yyyy-MM-dd} ({tage} Tage)";
                var status = tage < 0 ? HealthStatus.Unhealthy : tage < warnTage ? HealthStatus.Degraded : HealthStatus.Healthy;
                schlechtester = (HealthStatus)Math.Min((int)schlechtester, (int)status);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
            {
                daten[Path.GetFileName(datei)] = "nicht lesbar";
                schlechtester = (HealthStatus)Math.Min((int)schlechtester, (int)HealthStatus.Degraded);
            }
        }

        return new HealthCheckResult(schlechtester, dateien.Count == 0 ? "keine Zertifikate konfiguriert" : $"{dateien.Count} Zertifikat(e)", data: daten);
    }

    private static X509Certificate2 Laden(string datei)
        => Path.GetExtension(datei).ToLowerInvariant() is ".pfx" or ".p12"
            ? X509CertificateLoader.LoadPkcs12FromFile(datei, null, X509KeyStorageFlags.EphemeralKeySet)
            : X509Certificate2.CreateFromPem(File.ReadAllText(datei));

    /// <summary>security.txt nach RFC 9116; <c>null</c> ohne Kontakt. „Expires“ ist Pflicht (Vorgabe: ein Jahr ab jetzt).</summary>
    public static string? SecurityTxt(string? kontakt, string? ablauf, string? richtlinie, DateTimeOffset jetzt)
    {
        var kontakte = (kontakt ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (kontakte.Length == 0)
        {
            return null;
        }

        var bis = DateTimeOffset.TryParse(ablauf, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var a) ? a : jetzt.AddYears(1);
        var zeilen = kontakte.Select(c => "Contact: " + c).ToList();
        zeilen.Add("Expires: " + bis.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        zeilen.Add("Preferred-Languages: de, en");
        if (!string.IsNullOrWhiteSpace(richtlinie))
        {
            zeilen.Add("Policy: " + richtlinie.Trim());
        }

        return string.Join('\n', zeilen) + "\n";
    }
}
