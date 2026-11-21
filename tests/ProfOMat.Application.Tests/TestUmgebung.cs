using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ProfOMat.Adapters.Mocks;
using ProfOMat.Application;
using ProfOMat.Application.Konten;
using ProfOMat.Application.Signaturen;
using ProfOMat.Domain.Drittpruefung;
using ProfOMat.Domain.Konten;
using ProfOMat.Infrastructure;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Application.Tests;

/// <summary>Angemeldete Person für Tests, frei umschaltbar.</summary>
internal sealed class TestBenutzer : IAktuellerBenutzer
{
    public Benutzer Aktuell { get; set; } = Benutzer.Niemand;

    public ValueTask<Benutzer> ErmittelnAsync(CancellationToken abbruch = default) => ValueTask.FromResult(Aktuell);
}

/// <summary>Vollständige Anwendung mit SQLite in-memory (Schema wie im Betrieb), Demodaten, fester Uhr.</summary>
internal sealed class TestUmgebung : IAsyncDisposable
{
    private readonly SqliteConnection verbindung;
    private readonly string ordner;

    private TestUmgebung(ServiceProvider dienste, SqliteConnection verbindung, string ordner)
    {
        Dienste = dienste;
        this.verbindung = verbindung;
        this.ordner = ordner;
    }

    public ServiceProvider Dienste { get; }

    public FakeTimeProvider Uhr => (FakeTimeProvider)Dienste.GetRequiredService<TimeProvider>();

    public TestBenutzer Benutzer => (TestBenutzer)Dienste.GetRequiredService<IAktuellerBenutzer>();

    public static async Task<TestUmgebung> StartenAsync(IDictionary<string, string?>? einstellungen = null)
    {
        var ordner = Path.Combine(Path.GetTempPath(), "profomat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ordner);
        var verbindungstext = $"Data Source=file:profomat-{Guid.NewGuid():N}?mode=memory&cache=shared";
        var verbindung = new SqliteConnection(verbindungstext);   // hält die In-Memory-Datenbank am Leben
        await verbindung.OpenAsync();
        var werte = new Dictionary<string, string?>
        {
            ["ConnectionStrings:ProfOMat"] = verbindungstext,
            ["Speicher:Schluessel"] = Path.Combine(ordner, "schluessel"),
            ["ProfOMat:OeffentlicheAdresse"] = "https://profomat.test/",
            ["ProfOMat:SekretariatEmail"] = "sekretariat@hs.test",
        };
        foreach (var (k, v) in einstellungen ?? new Dictionary<string, string?>())
        {
            werte[k] = v;
        }

        var konfiguration = new ConfigurationBuilder().AddInMemoryCollection(werte).Build();
        var dienste = new ServiceCollection().AddLogging();
        dienste.AddProfOMatAnwendung(konfiguration);
        dienste.AddProfOMatInfrastruktur(konfiguration, ordner, hintergrundjobs: false);
        dienste.AddProfOMatMocks();
        dienste.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero)));
        dienste.AddSingleton<TestBenutzer>();
        dienste.AddSingleton<IAktuellerBenutzer>(d => d.GetRequiredService<TestBenutzer>());
        var anbieter = dienste.BuildServiceProvider();
        await DatenbankInitialisierung.InitialisierenAsync(anbieter);
        await using (var b = anbieter.CreateAsyncScope())
        {
            await b.ServiceProvider.GetRequiredService<Demodaten>().AnlegenAsync();
        }

        return new TestUmgebung(anbieter, verbindung, ordner);
    }

    /// <summary>Führt Arbeit in einem frischen Bereich (eigener DbContext) aus – wie eine Anfrage.</summary>
    public async Task<T> InBereichAsync<T>(Func<IServiceProvider, Task<T>> arbeit)
    {
        await using var b = Dienste.CreateAsyncScope();
        return await arbeit(b.ServiceProvider);
    }

    public Task InBereichAsync(Func<IServiceProvider, Task> arbeit) => InBereichAsync(async d =>
    {
        await arbeit(d);
        return true;
    });

    public async Task<Konto> KontoAsync(string kennung) => await InBereichAsync(async d =>
    {
        var daten = d.GetRequiredService<IDaten>();
        return (await daten.ErsterAsync(daten.Menge<Konto>().Where(k => k.Kennung == kennung)))!;
    });

    public async Task AlsAsync(string kennung) => Benutzer.Aktuell = KontoDienst.AlsBenutzer(await KontoAsync(kennung));

    public async ValueTask DisposeAsync()
    {
        await Dienste.DisposeAsync();
        await verbindung.DisposeAsync();
        try
        {
            Directory.Delete(ordner, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Schlüssel und Unterschriften wie aus dem Browser (WebCrypto-Formate).</summary>
internal static class Unterschriften
{
    public static Unterschrift MitSchluessel(ECDsa schluessel, string text)
    {
        var p = schluessel.ExportParameters(false);
        var jwk = $"{{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"{B64Url(p.Q.X!)}\",\"y\":\"{B64Url(p.Q.Y!)}\"}}";
        var sig = schluessel.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new Unterschrift(Signaturart.Schluessel, "ECDSA-SHA256", Convert.ToBase64String(sig), jwk, null);
    }

    public static Unterschrift MitRsa(RSA schluessel, string text)
    {
        var p = schluessel.ExportParameters(false);
        var jwk = $"{{\"kty\":\"RSA\",\"n\":\"{B64Url(p.Modulus!)}\",\"e\":\"{B64Url(p.Exponent!)}\"}}";
        var sig = schluessel.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new Unterschrift(Signaturart.Schluessel, "RSASSA-PKCS1-v1_5-SHA256", Convert.ToBase64String(sig), jwk, null);
    }

    public static Unterschrift Gezeichnet() => new(Signaturart.Handschrift, null, null, null, Png(300, 100));

    /// <summary>Weißes RGB-PNG mit einer schwarzen Linie.</summary>
    public static byte[] Png(int breite, int hoehe)
    {
        var roh = new byte[hoehe * ((breite * 3) + 1)];
        for (var y = 0; y < hoehe; y++)
        {
            var zeile = y * ((breite * 3) + 1);
            for (var x = 0; x < breite; x++)
            {
                var schwarz = y == hoehe / 2 && x > 20 && x < breite - 20;
                roh[zeile + 1 + (x * 3)] = roh[zeile + 2 + (x * 3)] = roh[zeile + 3 + (x * 3)] = schwarz ? (byte)0 : (byte)255;
            }
        }

        using var komprimiert = new MemoryStream();
        using (var z = new ZLibStream(komprimiert, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(roh);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, breite);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), hoehe);
        ihdr[8] = 8;   // Bittiefe
        ihdr[9] = 2;   // RGB
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", komprimiert.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string typ, byte[] daten)
    {
        var laenge = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(laenge, daten.Length);
        s.Write(laenge);
        var typDaten = Encoding.ASCII.GetBytes(typ).Concat(daten).ToArray();
        s.Write(typDaten);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typDaten));
        s.Write(crc);
    }

    private static uint Crc32(byte[] daten)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in daten)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static string B64Url(byte[] d) => Convert.ToBase64String(d).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
