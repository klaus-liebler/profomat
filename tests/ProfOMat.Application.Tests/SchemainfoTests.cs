using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Application.Tests;

/// <summary>
/// Das Deploy-Skript verlässt sich auf <see cref="DatenbankInitialisierung.Schemainfo"/>: Liegt die Version der Datenbank zwischen
/// „migrierbar_ab“ und „schemaversion“, rollt es ohne -DbNeu aus. Diese Tests halten die Grenzen fest.
/// </summary>
public sealed class SchemainfoTests : IDisposable
{
    private readonly string _ordner = Directory.CreateTempSubdirectory("profomat-schema-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_ordner, recursive: true);
    }

    private ServiceProvider Dienste() => new ServiceCollection()
        .AddDbContext<ProfOMatDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_ordner, "test.db")}"))
        .BuildServiceProvider();

    private static async Task VersionSetzenAsync(ServiceProvider dienste, int version)
    {
        await using var bereich = dienste.CreateAsyncScope();
        var db = bereich.ServiceProvider.GetRequiredService<ProfOMatDbContext>();
        await db.Database.ExecuteSqlAsync($"UPDATE Schemainfo SET Version = {version}", TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Schemainfo_nennt_Version_und_aelteste_aufwertbare_Version()
    {
        DatenbankInitialisierung.Schemainfo.ShouldBe(
            FormattableString.Invariant($"schemaversion={ProfOMatDbContext.Schemaversion}\nmigrierbar_ab={DatenbankInitialisierung.AeltesteAufwertbareVersion}\n"));
        DatenbankInitialisierung.AeltesteAufwertbareVersion.ShouldBeLessThanOrEqualTo(ProfOMatDbContext.Schemaversion);
    }

    [Fact]
    public async Task Aktuelle_Datenbank_wird_ohne_Aenderung_angenommen()
    {
        await using var dienste = Dienste();
        await DatenbankInitialisierung.InitialisierenAsync(dienste, TestContext.Current.CancellationToken);
        await DatenbankInitialisierung.InitialisierenAsync(dienste, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Aeltere_Version_als_migrierbar_ab_wird_abgelehnt()
    {
        await using var dienste = Dienste();
        await DatenbankInitialisierung.InitialisierenAsync(dienste, TestContext.Current.CancellationToken);
        await VersionSetzenAsync(dienste, DatenbankInitialisierung.AeltesteAufwertbareVersion - 1);

        var fehler = await Should.ThrowAsync<InvalidOperationException>(
            () => DatenbankInitialisierung.InitialisierenAsync(dienste, TestContext.Current.CancellationToken));
        fehler.Message.ShouldContain("Datenbankschema veraltet");
    }

    [Fact]
    public async Task Neuere_Datenbank_als_das_Programm_wird_abgelehnt()
    {
        await using var dienste = Dienste();
        await DatenbankInitialisierung.InitialisierenAsync(dienste, TestContext.Current.CancellationToken);
        await VersionSetzenAsync(dienste, ProfOMatDbContext.Schemaversion + 1);

        await Should.ThrowAsync<InvalidOperationException>(
            () => DatenbankInitialisierung.InitialisierenAsync(dienste, TestContext.Current.CancellationToken));
    }
}
