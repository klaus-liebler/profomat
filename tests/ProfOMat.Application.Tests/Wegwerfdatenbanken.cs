using System.Security.Cryptography;
using ProfOMat.Infrastructure;
using System.Text;
using ProfOMat.Infrastructure.Datenbank;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ProfOMat.Application.Tests;

/// <summary>
/// Vorlage und Wegwerf-Datenbanken für Tests (PostgreSQL im Entwicklungscontainer, deploy/docker-compose.yml): Die Vorlage wird einmal je
/// Migrationsstand gebaut, jeder Test bekommt eine Kopie (CREATE DATABASE … TEMPLATE, Sekundenbruchteile) und löscht sie danach.
/// </summary>
internal static class Wegwerfdatenbanken
{
    public static readonly string Basisverbindung = Environment.GetEnvironmentVariable("PROFOMAT_TEST_DB")
        ?? "Host=127.0.0.1;Port=5443;Username=profomat;Password=entwicklung";

    private static readonly SemaphoreSlim Sperre = new(1, 1);
    private static string? _vorlage;

    public static string Mit(string datenbank, bool pooling = true)
        => new NpgsqlConnectionStringBuilder(Basisverbindung) { Database = datenbank, Pooling = pooling }.ConnectionString;

    /// <summary>Legt eine Kopie der Vorlage an (vollständig migriert, ohne Daten) und gibt ihre Verbindungszeichenfolge zurück.</summary>
    public static async Task<string> NeuAsync(string praefix = "pm_test")
    {
        var vorlage = await VorlageAsync();
        var name = Name(praefix);
        await VerwaltenAsync($"CREATE DATABASE \"{name}\" TEMPLATE \"{vorlage}\"");
        return Mit(name);
    }

    /// <summary>Name für eine Datenbank, die es noch nicht gibt (die Anwendung legt sie selbst an).</summary>
    public static string Name(string praefix = "pm_test") => $"{praefix}_{Guid.NewGuid():N}"[..Math.Min(60, praefix.Length + 33)];

    public static async Task LoeschenAsync(string verbindung)
    {
        NpgsqlConnection.ClearAllPools();
        await VerwaltenAsync($"DROP DATABASE IF EXISTS \"{new NpgsqlConnectionStringBuilder(verbindung).Database}\" WITH (FORCE)");
    }

    private static async Task<string> VorlageAsync()
    {
        if (_vorlage is not null)
        {
            return _vorlage;
        }

        await Sperre.WaitAsync();
        try
        {
            if (_vorlage is not null)
            {
                return _vorlage;
            }

            string stand;
            await using (var probe = new ProfOMatDbContext(new DbContextOptionsBuilder<ProfOMatDbContext>().UseProfOMatPostgres(Mit("postgres", pooling: false)).Options))
            {
                stand = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", probe.Database.GetMigrations()))))[..16];
            }

            var name = $"profomat_testvorlage_{stand}";
            // Mehrere Testprozesse gleichzeitig: Sperre auf dem Server, damit die Vorlage nur einmal gebaut wird.
            await using var admin = new NpgsqlConnection(Mit("postgres", pooling: false));
            await admin.OpenAsync();
            await Ausfuehren(admin, "SELECT pg_advisory_lock(4817)");
            try
            {
                await using var pruefen = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", admin);
                pruefen.Parameters.AddWithValue("n", name);
                if (await pruefen.ExecuteScalarAsync() is null)
                {
                    var bau = name + "_bau";
                    await Ausfuehren(admin, $"DROP DATABASE IF EXISTS \"{bau}\" WITH (FORCE)");
                    await using (var db = new ProfOMatDbContext(new DbContextOptionsBuilder<ProfOMatDbContext>().UseProfOMatPostgres(Mit(bau, pooling: false)).Options))
                    {
                        await db.Database.MigrateAsync();
                    }

                    await Ausfuehren(admin, $"ALTER DATABASE \"{bau}\" RENAME TO \"{name}\"");
                    await Ausfuehren(admin, $"ALTER DATABASE \"{name}\" WITH IS_TEMPLATE true");
                    // Alte Vorlagen früherer Migrationsstände aufräumen.
                    await using var alt = new NpgsqlCommand("SELECT datname FROM pg_database WHERE datname LIKE 'profomat\\_testvorlage\\_%' AND datname <> @n", admin);
                    alt.Parameters.AddWithValue("n", name);
                    var alte = new List<string>();
                    await using (var r = await alt.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            alte.Add(r.GetString(0));
                        }
                    }

                    foreach (var a in alte)
                    {
                        await Ausfuehren(admin, $"ALTER DATABASE \"{a}\" WITH IS_TEMPLATE false");
                        await Ausfuehren(admin, $"DROP DATABASE IF EXISTS \"{a}\" WITH (FORCE)");
                    }
                }
            }
            finally
            {
                await Ausfuehren(admin, "SELECT pg_advisory_unlock(4817)");
            }

            return _vorlage = name;
        }
        finally
        {
            Sperre.Release();
        }
    }

    private static async Task VerwaltenAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(Mit("postgres", pooling: false));
        await admin.OpenAsync();
        await Ausfuehren(admin, sql);
    }

    private static async Task Ausfuehren(NpgsqlConnection con, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, con) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }
}
