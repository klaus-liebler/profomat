using System.Security.Cryptography;
using System.Text;
using ProfOMat.Application;

namespace ProfOMat.Infrastructure.Sicherheit;

/// <summary>
/// Systemsiegel mit einem ECDSA-P-256-Schlüssel, der beim ersten Start im Schlüsselordner entsteht
/// (<c>Speicher:Schluessel</c>, nie im Repository). Ältere Schlüssel (Dateien <c>systemsiegel-*.pem</c> im selben Ordner)
/// bleiben zum Prüfen alter Siegel nutzbar.
/// </summary>
public sealed class Systemsiegel : ISystemsiegel, IDisposable
{
    private readonly ECDsa aktuell;
    private readonly Dictionary<string, ECDsa> pruefschluessel = new(StringComparer.Ordinal);

    public Systemsiegel(string schluesselOrdner)
    {
        Directory.CreateDirectory(schluesselOrdner);
        var datei = Path.Combine(schluesselOrdner, "systemsiegel.pem");
        aktuell = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(datei))
        {
            aktuell.ImportFromPem(File.ReadAllText(datei));
        }
        else
        {
            File.WriteAllText(datei, aktuell.ExportPkcs8PrivateKeyPem());
        }

        SchluesselFingerabdruck = Fingerabdruck(aktuell);
        pruefschluessel[SchluesselFingerabdruck] = aktuell;
        foreach (var alt in Directory.EnumerateFiles(schluesselOrdner, "systemsiegel-*.pem"))
        {
            var k = ECDsa.Create();
            k.ImportFromPem(File.ReadAllText(alt));
            pruefschluessel.TryAdd(Fingerabdruck(k), k);
        }
    }

    public string SchluesselFingerabdruck { get; }

    public string Versiegeln(string text)
        => Convert.ToBase64String(aktuell.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256));

    public bool Pruefen(string text, string siegel, string schluesselFingerabdruck)
    {
        if (string.IsNullOrEmpty(siegel) || !pruefschluessel.TryGetValue(schluesselFingerabdruck ?? string.Empty, out var k))
        {
            return false;
        }

        try
        {
            return k.VerifyData(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(siegel), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var k in pruefschluessel.Values.Distinct())
        {
            k.Dispose();
        }
    }

    private static string Fingerabdruck(ECDsa k) => Convert.ToHexStringLower(SHA256.HashData(k.ExportSubjectPublicKeyInfo()))[..16];
}
