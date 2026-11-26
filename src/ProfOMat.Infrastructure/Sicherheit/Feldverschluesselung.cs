using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProfOMat.Infrastructure.Sicherheit;

/// <summary>Schlüsselring der Feldverschlüsselung: je Version ein Hauptschlüssel (32 Byte); verschlüsselt wird mit der höchsten.</summary>
public sealed class Feldschluesselring
{
    private readonly Dictionary<int, (byte[] Enc, byte[] Mac)> schluessel = [];

    public Feldschluesselring(IEnumerable<(int Version, byte[] Haupt)> versionen)
    {
        foreach (var (v, haupt) in versionen)
        {
            if (v < 1 || haupt.Length != 32)
            {
                throw new InvalidOperationException("Feldschlüssel: Version ≥ 1 und genau 32 Byte je Schlüssel.");
            }

            // HKDF: getrennte Schlüssel für AES-GCM und für die abgeleitete Nonce (deterministische Felder); je Instanz ein Mandant
            schluessel[v] = (HKDF.DeriveKey(HashAlgorithmName.SHA256, haupt, 32, info: Encoding.UTF8.GetBytes($"ProfOMat Feld AES-256-GCM v{v}")),
                HKDF.DeriveKey(HashAlgorithmName.SHA256, haupt, 32, info: Encoding.UTF8.GetBytes($"ProfOMat Feld Nonce v{v}")));
        }

        if (schluessel.Count == 0)
        {
            throw new InvalidOperationException("Feldschlüssel: kein Schlüssel vorhanden.");
        }

        AktuelleVersion = schluessel.Keys.Max();
    }

    public int AktuelleVersion { get; }

    public IReadOnlyCollection<int> Versionen => schluessel.Keys;

    internal (byte[] Enc, byte[] Mac) Holen(int version)
        => schluessel.TryGetValue(version, out var s) ? s : throw new CryptographicException($"Feldschlüssel Version {version} fehlt.");

    /// <summary>Format der Schlüsseldatei: je Zeile „&lt;Version&gt;:&lt;Base64 von 32 Byte&gt;“, „#“ = Kommentar.</summary>
    public static Feldschluesselring AusText(string text)
        => new(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(z => !z.StartsWith('#'))
            .Select(z => z.Split(':', 2))
            .Select(t => (int.Parse(t[0], CultureInfo.InvariantCulture), Convert.FromBase64String(t[1].Trim()))));

    public static string NeueZeile(int version) => $"{version.ToString(CultureInfo.InvariantCulture)}:{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
}

/// <summary>
/// Feldverschlüsselung für besonders schutzbedürftige Daten (Zielbild Abschnitt 4): AES-256-GCM in der Anwendung, transparent über
/// EF-Core-Wertkonverter – Fachcode, Signaturtexte und PDFs sehen Klartext, Datenbank und Sicherungen nur Geheimtext.
/// Jeder Wert trägt die Schlüsselversion („enc:v2:…“), damit ein Schlüsselwechsel im laufenden Betrieb möglich ist.
/// Deterministische Felder (Matrikelnummern: eindeutige Indizes) leiten die Nonce aus dem Klartext ab (SIV-artig: gleiche Werte ergeben
/// gleichen Geheimtext, mehr verrät der Geheimtext nicht); alle anderen bekommen eine zufällige Nonce.
/// Schlüssel: systemd-Credential (<c>$CREDENTIALS_DIRECTORY/profomat-feldschluessel</c>), Docker Secret
/// (<c>/run/secrets/profomat-feldschluessel</c>), <c>Feldverschluesselung:Schluesseldatei</c>, sonst <c>feldschluessel.txt</c> im
/// Schlüsselordner (wird angelegt – für die Entwicklung; im Betrieb mit Warnung).
/// </summary>
public static class Feldverschluesselung
{
    public const string Praefix = "enc:v";
    public const string Dateiname = "profomat-feldschluessel";
    private static readonly byte[] Kontext = "profomat-feld"u8.ToArray();
    private static Feldschluesselring? ring;

    /// <summary>Der Ring gilt prozessweit (EF-Modelle werden zwischengespeichert); der erste gewinnt.</summary>
    public static void Einrichten(Feldschluesselring r) => Interlocked.CompareExchange(ref ring, r, null);

    public static Feldschluesselring? Ring => ring;

    public static bool IstVerschluesselt(string? wert) => wert is not null && wert.StartsWith(Praefix, StringComparison.Ordinal);

    public static int? Version(string? wert)
    {
        if (!IstVerschluesselt(wert))
        {
            return null;
        }

        var ende = wert!.IndexOf(':', Praefix.Length);
        return ende > 0 && int.TryParse(wert.AsSpan(Praefix.Length, ende - Praefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static string Verschluesseln(string klar, bool deterministisch, Feldschluesselring? mit = null)
    {
        ArgumentNullException.ThrowIfNull(klar);
        var r = mit ?? ring ?? throw new InvalidOperationException("Feldverschlüsselung nicht eingerichtet (kein Schlüssel).");
        var v = r.AktuelleVersion;
        var (enc, mac) = r.Holen(v);
        var daten = Encoding.UTF8.GetBytes(klar);
        var nonce = deterministisch ? HMACSHA256.HashData(mac, daten)[..12] : RandomNumberGenerator.GetBytes(12);
        var geheim = new byte[daten.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(enc, 16))
        {
            aes.Encrypt(nonce, daten, geheim, tag, Kontext);
        }

        return $"{Praefix}{v.ToString(CultureInfo.InvariantCulture)}:{Convert.ToBase64String([.. nonce, .. geheim, .. tag])}";
    }

    /// <summary>Entschlüsselt; unverschlüsselte Altwerte (vor der Aufwertung) kommen unverändert zurück.</summary>
    public static string Entschluesseln(string wert, Feldschluesselring? mit = null)
    {
        ArgumentNullException.ThrowIfNull(wert);
        if (!IstVerschluesselt(wert))
        {
            return wert;
        }

        var r = mit ?? ring ?? throw new InvalidOperationException("Feldverschlüsselung nicht eingerichtet (kein Schlüssel).");
        var v = Version(wert) ?? throw new CryptographicException("Verschlüsselter Wert ohne Version.");
        var roh = Convert.FromBase64String(wert[(wert.IndexOf(':', Praefix.Length) + 1)..]);
        if (roh.Length < 28)
        {
            throw new CryptographicException("Verschlüsselter Wert zu kurz.");
        }

        var klar = new byte[roh.Length - 28];
        using (var aes = new AesGcm(r.Holen(v).Enc, 16))
        {
            aes.Decrypt(roh.AsSpan(0, 12), roh.AsSpan(12, klar.Length), roh.AsSpan(roh.Length - 16), klar, Kontext);
        }

        return Encoding.UTF8.GetString(klar);
    }

    /// <summary>EF-Konverter für Texte (Klartext im Modell, Geheimtext in der Datenbank).</summary>
    public static ValueConverter<string, string> Text(bool deterministisch)
        => new(v => Verschluesseln(v, deterministisch, null), v => Entschluesseln(v, null));

    /// <summary>EF-Konverter für optionale Texte.</summary>
    public static ValueConverter<string?, string?> TextOptional(bool deterministisch)
        => new(v => v == null ? null : Verschluesseln(v, deterministisch, null), v => v == null ? null : Entschluesseln(v, null));

    /// <summary>EF-Konverter für Dezimalzahlen (Noten); in SQLite ohnehin als Text gespeichert.</summary>
    public static ValueConverter<decimal, string> Zahl()
        => new(v => Verschluesseln(v.ToString(CultureInfo.InvariantCulture), false, null),
            v => decimal.Parse(Entschluesseln(v, null), NumberStyles.Number, CultureInfo.InvariantCulture));

    /// <summary>Schlüssel laden (Reihenfolge siehe Klasse); fehlt jeder, entsteht <c>feldschluessel.txt</c> im Schlüsselordner.</summary>
    public static Feldschluesselring Laden(IConfiguration k, string schluesselOrdner, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(k);
        var kandidaten = new List<string>();
        if (Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY") is { Length: > 0 } cred)
        {
            kandidaten.Add(Path.Combine(cred, Dateiname));
        }

        kandidaten.Add(Path.Combine("/run/secrets", Dateiname));
        if (k["Feldverschluesselung:Schluesseldatei"] is { Length: > 0 } datei)
        {
            kandidaten.Add(datei);
        }

        foreach (var pfad in kandidaten.Where(File.Exists))
        {
            return Feldschluesselring.AusText(File.ReadAllText(pfad));
        }

        var lokal = Path.Combine(schluesselOrdner, "feldschluessel.txt");
        if (!File.Exists(lokal))
        {
            Directory.CreateDirectory(schluesselOrdner);
            File.WriteAllText(lokal, "# Feldschlüssel von ProfOMat – nie ins Repository; im Betrieb als systemd-Credential ablegen (docs/deploy.md)\n"
                                     + Feldschluesselring.NeueZeile(1) + "\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(lokal, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        log?.LogWarning("Feldschlüssel aus dem Schlüsselordner (feldschluessel.txt) – im Betrieb besser als systemd-Credential bzw. Docker Secret ablegen");
        return Feldschluesselring.AusText(File.ReadAllText(lokal));
    }
}
