using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Application.Tests;

/// <summary>
/// Aufwertungskette alter SQLite-Dateien (<see cref="SqliteAltbestand"/>): Die Übernahme nach PostgreSQL bringt eine Kopie der Quelle damit
/// auf den letzten SQLite-Stand. Diese Tests halten die Grenzen fest.
/// </summary>
public sealed class SqliteAltbestandTests : IDisposable
{
    private readonly string _ordner = Directory.CreateTempSubdirectory("profomat-schema-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_ordner, recursive: true);
    }

    private ServiceProvider Dienste() => new ServiceCollection()
        .AddDbContext<ProfOMatDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_ordner, "test.db")}")
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, ModellJeAnbieter>())
        .BuildServiceProvider();

    private static async Task VersionSetzenAsync(ServiceProvider dienste, int version)
    {
        await using var bereich = dienste.CreateAsyncScope();
        var db = bereich.ServiceProvider.GetRequiredService<ProfOMatDbContext>();
        await db.Database.ExecuteSqlAsync($"UPDATE Schemainfo SET Version = {version}", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Aktuelle_Datenbank_wird_ohne_Aenderung_angenommen()
    {
        await using var dienste = Dienste();
        await SqliteAltbestand.InitialisierenAsync(dienste, TestContext.Current.CancellationToken);
        await SqliteAltbestand.InitialisierenAsync(dienste, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Aeltere_Version_als_migrierbar_ab_wird_abgelehnt()
    {
        await using var dienste = Dienste();
        await SqliteAltbestand.InitialisierenAsync(dienste, TestContext.Current.CancellationToken);
        await VersionSetzenAsync(dienste, SqliteAltbestand.AeltesteAufwertbareVersion - 1);

        var fehler = await Should.ThrowAsync<InvalidOperationException>(
            () => SqliteAltbestand.InitialisierenAsync(dienste, TestContext.Current.CancellationToken));
        fehler.Message.ShouldContain("Datenbankschema veraltet");
    }

    [Fact]
    public async Task Neuere_Datenbank_als_das_Programm_wird_abgelehnt()
    {
        await using var dienste = Dienste();
        await SqliteAltbestand.InitialisierenAsync(dienste, TestContext.Current.CancellationToken);
        await VersionSetzenAsync(dienste, ProfOMatDbContext.Schemaversion + 1);

        await Should.ThrowAsync<InvalidOperationException>(
            () => SqliteAltbestand.InitialisierenAsync(dienste, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 18 → 19 (ASP.NET Core Identity): Eine Datenbank im alten Stand – ohne Stempel-, Sperr- und TOTP-Spalten, ohne Tabellen für
    /// Identitätsdienste und Passkeys – wird ohne Datenverlust aufgewertet; das vorhandene Passwort bleibt gültig.
    /// </summary>
    [Fact]
    public async Task Version_18_wird_auf_Identity_aufgewertet_und_Passwoerter_bleiben_gueltig()
    {
        var abbruch = TestContext.Current.CancellationToken;
        await using var dienste = Dienste();
        await SqliteAltbestand.InitialisierenAsync(dienste, abbruch);
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<ProfOMat.Domain.Konten.Konto>();
        Guid id;
        string hash;
        await using (var bereich = dienste.CreateAsyncScope())
        {
            var db = bereich.ServiceProvider.GetRequiredService<ProfOMatDbContext>();
            var konto = new ProfOMat.Domain.Konten.Konto("alt", "Prof. Dr.", "Anna", "Alt", "alt@hs.test");
            konto.RolleGeben(ProfOMat.Domain.Konten.Rolle.Systemverwaltung);
            hash = hasher.HashPassword(konto, "ein-langes-Passwort-1");
            konto.PasswortSetzen(hash);
            db.Add(konto);
            await db.SaveChangesAsync(abbruch);
            id = konto.Id;

            // Stand von Version 18 nachbauen
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ExterneAnmeldungen; DROP TABLE Passkeys;", abbruch);
            foreach (var spalte in new[] { "SicherheitsStempel", "Fehlversuche", "GesperrtBis", "ZweiterFaktorAktiv", "AuthenticatorSchluessel", "Wiederherstellungscodes" })
            {
                var sql = "ALTER TABLE Konten DROP COLUMN \"" + spalte + "\";";
                await db.Database.ExecuteSqlRawAsync(sql, abbruch);
            }

            await db.Database.ExecuteSqlAsync($"UPDATE Schemainfo SET Version = {18}", abbruch);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await SqliteAltbestand.InitialisierenAsync(dienste, abbruch);

        await using (var bereich = dienste.CreateAsyncScope())
        {
            var db = bereich.ServiceProvider.GetRequiredService<ProfOMatDbContext>();
            (await db.Set<Schemainfo>().SingleAsync(abbruch)).Version.ShouldBe(ProfOMatDbContext.Schemaversion);
            var konto = await db.Set<ProfOMat.Domain.Konten.Konto>().SingleAsync(k => k.Id == id, abbruch);
            konto.PasswortHash.ShouldBe(hash);
            hasher.VerifyHashedPassword(konto, konto.PasswortHash!, "ein-langes-Passwort-1").ShouldNotBe(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed);
            konto.SicherheitsStempel.Length.ShouldBe(32);
            konto.Fehlversuche.ShouldBe(0);
            konto.GesperrtBis.ShouldBeNull();
            konto.ZweiterFaktorAktiv.ShouldBeFalse();
            konto.Rollen.Single().Rolle.ShouldBe(ProfOMat.Domain.Konten.Rolle.Systemverwaltung);

            // neue Tabellen sind nutzbar
            konto.AnmeldungVerknuepfen("SAML", "alt@hs-osnabrueck.de", null, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(abbruch);
            (await db.Set<ProfOMat.Domain.Konten.ExterneAnmeldung>().CountAsync(abbruch)).ShouldBe(1);
        }

        await SqliteAltbestand.InitialisierenAsync(dienste, abbruch);   // aktuelle Version: nichts mehr zu tun
    }
}
