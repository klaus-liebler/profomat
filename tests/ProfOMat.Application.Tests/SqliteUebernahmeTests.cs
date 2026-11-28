using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Domain.Konten;
using ProfOMat.Infrastructure;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Application.Tests;

/// <summary>Einmalige Übernahme alter SQLite-Datenbanken nach PostgreSQL (<see cref="SqliteUebernahme"/>).</summary>
public sealed class SqliteUebernahmeTests
{
    /// <summary>SQLite-Datei mit Demodaten (aktueller SQLite-Stand) als Quelle.</summary>
    private static async Task<string> QuelleAsync(string ordner, Func<ProfOMatDbContext, Task>? veraendern = null)
    {
        await using var u = await TestUmgebung.StartenAsync(sqlite: true);
        if (veraendern is not null)
        {
            await u.InBereichAsync(d => veraendern(d.GetRequiredService<ProfOMatDbContext>()));
        }

        var datei = Path.Combine(ordner, "profomat.db");
        await using var ziel = new SqliteConnection($"Data Source={datei};Pooling=False");
        await ziel.OpenAsync(TestContext.Current.CancellationToken);
        u.Sqlite!.BackupDatabase(ziel);
        return datei;
    }

    private static ServiceProvider Ziel(string verbindung) => new ServiceCollection()
        .AddDbContext<ProfOMatDbContext>(o => o.UseProfOMatPostgres(verbindung))
        .BuildServiceProvider();

    [Fact]
    public async Task Alle_Tabellen_kommen_vollstaendig_und_gleich_an_und_die_Anwendung_liest_sie()
    {
        var ordner = Directory.CreateTempSubdirectory("profomat-uebernahme-test-").FullName;
        var verbindung = Wegwerfdatenbanken.Mit(Wegwerfdatenbanken.Name("pm_uebernahme"));
        try
        {
            var quelle = await QuelleAsync(ordner);
            var vorher = await File.ReadAllBytesAsync(quelle, TestContext.Current.CancellationToken);
            await using var dienste = Ziel(verbindung);
            var zeilen = new List<string>();

            var probe = await SqliteUebernahme.UebernehmenAsync(quelle, dienste, probe: true, zeilen.Add, TestContext.Current.CancellationToken);
            probe.Erfolgreich.ShouldBeTrue();
            probe.Probe.ShouldBeTrue();
            (await Datenbankstand.PruefenAsync(dienste, TestContext.Current.CancellationToken)).Single().Stand.ShouldBe(Standart.Neu);   // Probe ließ das Ziel unberührt

            var bericht = await SqliteUebernahme.UebernehmenAsync(quelle, dienste, probe: false, zeilen.Add, TestContext.Current.CancellationToken);
            bericht.Erfolgreich.ShouldBeTrue();
            bericht.Tabellen.ShouldAllBe(t => t.Gleich);
            bericht.Zeilen.ShouldBeGreaterThan(20);
            bericht.Tabellen.Single(t => t.Tabelle == "Konten").ZeilenZiel.ShouldBeGreaterThan(3);
            zeilen.ShouldContain(z => z.StartsWith("UEBERNAHME|Konten|", StringComparison.Ordinal));
            (await File.ReadAllBytesAsync(quelle, TestContext.Current.CancellationToken)).ShouldBe(vorher);   // Quelle unverändert

            // Ziel ist jetzt aktuell migriert und für die Anwendung lesbar
            (await Datenbankstand.PruefenAsync(dienste, TestContext.Current.CancellationToken)).Single().Stand.ShouldBe(Standart.Aktuell);
            await using var b = dienste.CreateAsyncScope();
            var db = b.ServiceProvider.GetRequiredService<ProfOMatDbContext>();
            var konto = await db.Set<Konto>().OrderBy(k => k.Kennung).FirstAsync(TestContext.Current.CancellationToken);
            konto.Rollen.ShouldNotBeEmpty();

            // ein zweites Mal geht nicht: Ziel ist nicht mehr leer
            var fehler = await Should.ThrowAsync<InvalidOperationException>(
                () => SqliteUebernahme.UebernehmenAsync(quelle, dienste, probe: false, _ => { }, TestContext.Current.CancellationToken));
            fehler.Message.ShouldContain("nicht leer");
        }
        finally
        {
            await Wegwerfdatenbanken.LoeschenAsync(verbindung);
            SqliteConnection.ClearAllPools();
            Directory.Delete(ordner, recursive: true);
        }
    }

    [Fact]
    public async Task Aeltere_SQLite_Datei_wird_auf_einer_Kopie_aufgewertet()
    {
        var ordner = Directory.CreateTempSubdirectory("profomat-uebernahme-test-").FullName;
        var verbindung = Wegwerfdatenbanken.Mit(Wegwerfdatenbanken.Name("pm_uebernahme"));
        try
        {
            // Stand von Version 19: ohne Sicherheitsprotokoll
            var quelle = await QuelleAsync(ordner, async db =>
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Sicherheitsprotokoll\"; UPDATE \"Schemainfo\" SET \"Version\" = 19;");
            });
            await using var dienste = Ziel(verbindung);
            var bericht = await SqliteUebernahme.UebernehmenAsync(quelle, dienste, probe: false, _ => { }, TestContext.Current.CancellationToken);
            bericht.QuellVersion.ShouldBe(19);
            bericht.Erfolgreich.ShouldBeTrue();

            // Quelle selbst bleibt auf 19
            await using var q = new SqliteConnection($"Data Source={quelle};Mode=ReadOnly;Pooling=False");
            await q.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = q.CreateCommand();
            cmd.CommandText = "SELECT \"Version\" FROM \"Schemainfo\"";
            Convert.ToInt32(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(19);
        }
        finally
        {
            await Wegwerfdatenbanken.LoeschenAsync(verbindung);
            SqliteConnection.ClearAllPools();
            Directory.Delete(ordner, recursive: true);
        }
    }

    [Fact]
    public async Task Fremde_Datei_wird_abgelehnt()
    {
        var ordner = Directory.CreateTempSubdirectory("profomat-uebernahme-test-").FullName;
        try
        {
            var datei = Path.Combine(ordner, "fremd.db");
            await using (var c = new SqliteConnection($"Data Source={datei};Pooling=False"))
            {
                await c.OpenAsync(TestContext.Current.CancellationToken);
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE x (a INTEGER)";
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using var dienste = Ziel(Wegwerfdatenbanken.Mit(Wegwerfdatenbanken.Name("pm_uebernahme")));
            var fehler = await Should.ThrowAsync<InvalidOperationException>(
                () => SqliteUebernahme.UebernehmenAsync(datei, dienste, probe: true, _ => { }, TestContext.Current.CancellationToken));
            fehler.Message.ShouldContain("keine ProfOMat-Datenbank");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(ordner, recursive: true);
        }
    }
}
