using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProfOMat.Infrastructure.Datenbank;

/// <summary>
/// Datenbank beim Start bereitstellen (PostgreSQL): fehlende Datenbank anlegen und alle Migrationen einspielen, danach die
/// Feldverschlüsselung nachziehen. Was ein Start tun würde, ohne etwas zu ändern, sagt <see cref="Datenbankstand"/> (Ausrollen).
/// </summary>
public static class DatenbankInitialisierung
{
    public static async Task InitialisierenAsync(IServiceProvider dienste, CancellationToken abbruch = default)
    {
        await using var bereich = dienste.CreateAsyncScope();
        var db = bereich.ServiceProvider.GetRequiredService<ProfOMatDbContext>();
        var log = bereich.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(DatenbankInitialisierung));
        var offen = (await db.Database.GetPendingMigrationsAsync(abbruch)).ToList();
        await db.Database.MigrateAsync(abbruch);
        if (offen.Count > 0)
        {
            log?.LogInformation("Datenbank {Datenbank}: {Anzahl} Migration(en) eingespielt ({Namen})",
                new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()).Database, offen.Count, string.Join(", ", offen));
        }

        if (ProfOMat.Infrastructure.Sicherheit.Feldverschluesselung.Ring is not null)
        {
            await BestandVerschluesselnAsync(db, abbruch);
        }
    }

    /// <summary>
    /// Feldverschlüsselung nachziehen: Bestandswerte im Klartext (vor Version 20) und Werte mit älterer Schlüsselversion (nach einem
    /// Schlüsselwechsel) neu schreiben – EF verschlüsselt sie dabei mit dem aktuellen Schlüssel. Läuft bei jedem Start, ist idempotent.
    /// </summary>
    /// <returns>Anzahl neu verschlüsselter Werte.</returns>
    public static async Task<int> BestandVerschluesselnAsync(ProfOMatDbContext db, CancellationToken abbruch = default)
    {
        var ring = ProfOMat.Infrastructure.Sicherheit.Feldverschluesselung.Ring ?? throw new InvalidOperationException("Feldverschlüsselung nicht eingerichtet.");
        var aktuell = ProfOMat.Infrastructure.Sicherheit.Feldverschluesselung.Praefix + ring.AktuelleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":%";
        var anzahl = 0;
        await using var transaktion = await db.Database.BeginTransactionAsync(abbruch);
        foreach (var (tabelle, spalte) in ProfOMatDbContext.VerschluesselteSpalten)
        {
            var typ = db.Model.GetEntityTypes().First(e => e.GetTableName() == tabelle);
            var eigenschaft = typ.GetProperties().First(p => p.GetColumnName() == spalte);
            var sql = "SELECT \"Id\" AS \"Value\" FROM \"" + tabelle + "\" WHERE \"" + spalte + "\" IS NOT NULL AND \"" + spalte + "\" NOT LIKE {0}";
            var ids = await db.Database.SqlQueryRaw<Guid>(sql, aktuell).ToListAsync(abbruch);
            foreach (var id in ids)
            {
                if (await db.FindAsync(typ.ClrType, [id], abbruch) is { } eintrag)
                {
                    db.Entry(eintrag).Property(eigenschaft.Name).IsModified = true;
                    anzahl++;
                }
            }
        }

        await db.SaveChangesAsync(abbruch);
        await transaktion.CommitAsync(abbruch);
        db.ChangeTracker.Clear();
        return anzahl;
    }
}

/// <summary>Nur für „dotnet ef migrations add …“ zur Entwicklungszeit (Verbindung zum Entwicklungscontainer, wird dabei nicht geöffnet).</summary>
internal sealed class DesignZeitFabrik : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<ProfOMatDbContext>
{
    public ProfOMatDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<ProfOMatDbContext>().UseProfOMatPostgres(Registrierung.Entwicklungsverbindung).Options);
}
