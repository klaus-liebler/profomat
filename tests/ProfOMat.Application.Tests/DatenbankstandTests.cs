using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Infrastructure;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Application.Tests;

/// <summary>Vorabprüfung für das Deploy-Kit (<c>--datenbankstand</c>): Zustände und Exitcodes.</summary>
public sealed class DatenbankstandTests
{
    [Fact]
    public async Task Neu_Aktuell_und_Fremd_werden_erkannt()
    {
        var t = TestContext.Current.CancellationToken;
        var verbindung = Wegwerfdatenbanken.Mit(Wegwerfdatenbanken.Name("pm_stand"));
        await using var dienste = new ServiceCollection().AddDbContext<ProfOMatDbContext>(o => o.UseProfOMatPostgres(verbindung)).BuildServiceProvider();
        try
        {
            var neu = await Datenbankstand.PruefenAsync(dienste, t);
            neu.Single().Stand.ShouldBe(Standart.Neu);
            Datenbankstand.Exitcode(neu).ShouldBe(0);
            neu.Single().Zeile.ShouldStartWith("DATENBANKSTAND|anwendung|pm_stand_");

            await DatenbankInitialisierung.InitialisierenAsync(dienste, t);
            (await Datenbankstand.PruefenAsync(dienste, t)).Single().Stand.ShouldBe(Standart.Aktuell);

            await using (var b = dienste.CreateAsyncScope())
            {
                var db = b.ServiceProvider.GetRequiredService<ProfOMatDbContext>();
                await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" VALUES ('29991231000000_Zukunft', '11.0.0')", t);
            }

            var fremd = await Datenbankstand.PruefenAsync(dienste, t);
            fremd.Single().Stand.ShouldBe(Standart.Fremd);
            Datenbankstand.Exitcode(fremd).ShouldBe(4);
        }
        finally
        {
            await Wegwerfdatenbanken.LoeschenAsync(verbindung);
        }
    }

    [Fact]
    public async Task Ohne_Server_meldet_die_Pruefung_keine_Verbindung()
    {
        var verbindung = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2";
        await using var dienste = new ServiceCollection().AddDbContext<ProfOMatDbContext>(o => o.UseProfOMatPostgres(verbindung)).BuildServiceProvider();
        var stand = await Datenbankstand.PruefenAsync(dienste, TestContext.Current.CancellationToken);
        stand.Single().Stand.ShouldBe(Standart.NichtLesbar);
        Datenbankstand.Exitcode(stand).ShouldBe(5);
    }
}
