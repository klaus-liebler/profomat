using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application;

namespace ProfOMat.Infrastructure.Datenbank;

/// <summary>EF-Core-Umsetzung des allgemeinen Datenzugangs.</summary>
internal sealed class Daten(ProfOMatDbContext db) : IDaten
{
    public IQueryable<T> Menge<T>()
        where T : class => db.Set<T>();

    public void Hinzufuegen<T>(T eintrag)
        where T : class => db.Set<T>().Add(eintrag);

    public void Entfernen<T>(T eintrag)
        where T : class => db.Set<T>().Remove(eintrag);

    public async Task<T?> FindenAsync<T>(Guid id, CancellationToken abbruch = default)
        where T : class
        => db.ChangeTracker.Entries<T>().FirstOrDefault(e => e.Property<Guid>("Id").CurrentValue == id)?.Entity
           ?? await db.Set<T>().FirstOrDefaultAsync(e => EF.Property<Guid>(e, "Id") == id, abbruch);

    public Task<List<T>> ListeAsync<T>(IQueryable<T> abfrage, CancellationToken abbruch = default) => abfrage.ToListAsync(abbruch);

    public Task<T?> ErsterAsync<T>(IQueryable<T> abfrage, CancellationToken abbruch = default) => abfrage.FirstOrDefaultAsync(abbruch);

    public Task<int> AnzahlAsync<T>(IQueryable<T> abfrage, CancellationToken abbruch = default) => abfrage.CountAsync(abbruch);

    public Task SpeichernAsync(CancellationToken abbruch = default) => db.SaveChangesAsync(abbruch);
}

/// <summary>Legt die Datenbank an bzw. prüft, ob die vorhandene Datei zum Modell passt.</summary>
public static class DatenbankInitialisierung
{
    public static async Task InitialisierenAsync(IServiceProvider dienste, CancellationToken abbruch = default)
    {
        await using var bereich = dienste.CreateAsyncScope();
        var db = bereich.ServiceProvider.GetRequiredService<ProfOMatDbContext>();
        if (db.Database.GetDbConnection().DataSource is { Length: > 0 } datei && datei != ":memory:" && !datei.StartsWith("file:", StringComparison.Ordinal)
            && Path.GetDirectoryName(Path.GetFullPath(datei)) is { } ordner)
        {
            Directory.CreateDirectory(ordner);
        }

        if (await db.Database.EnsureCreatedAsync(abbruch))
        {
            db.Add(new Schemainfo { Version = ProfOMatDbContext.Schemaversion });
            await db.SaveChangesAsync(abbruch);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", abbruch);
            return;
        }

        int? version;
        try
        {
            version = (await db.Set<Schemainfo>().AsNoTracking().FirstOrDefaultAsync(abbruch))?.Version;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            version = null;
        }

        if (version == 7)
        {
            await AufVersion8Async(db, abbruch);
            version = 8;
        }

        if (version == 8)
        {
            await AufVersion9Async(db, abbruch);
            version = 9;
        }

        if (version != ProfOMatDbContext.Schemaversion)
        {
            throw new InvalidOperationException(
                $"Datenbankschema veraltet (Datei: {version?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unbekannt"}, erwartet: {ProfOMatDbContext.Schemaversion}) – "
                + "Datei löschen oder beiseitelegen (Entwicklungsphase ohne Migrationen).");
        }
    }

    /// <summary>
    /// 7 → 8 ohne Datenverlust: Prüfende ohne Konto (Name als Text) in mündlichen Prüfungen. Kleine Aufwertungen dieser Art,
    /// bis vor dem Produktivbetrieb EF-Migrationen kommen; alles oder nichts (eine Transaktion).
    /// </summary>
    private static async Task AufVersion8Async(ProfOMatDbContext db, CancellationToken abbruch)
    {
        var typ = db.Model.FindEntityType(typeof(ProfOMat.Domain.Muendlich.MuendlichePruefung))!;
        var tabelle = typ.GetTableName()!;
        await using var transaktion = await db.Database.BeginTransactionAsync(abbruch);
        foreach (var name in new[] { nameof(ProfOMat.Domain.Muendlich.MuendlichePruefung.ErstpruefendeName), nameof(ProfOMat.Domain.Muendlich.MuendlichePruefung.ZweitpruefendeName) })
        {
            var sql = "ALTER TABLE \"" + tabelle + "\" ADD COLUMN \"" + typ.FindProperty(name)!.GetColumnName() + "\" TEXT NULL;";
            await db.Database.ExecuteSqlRawAsync(sql, abbruch);
        }

        var info = await db.Set<Schemainfo>().SingleAsync(abbruch);
        info.Version = 8;
        await db.SaveChangesAsync(abbruch);
        await transaktion.CommitAsync(abbruch);
    }

    /// <summary>8 → 9 ohne Datenverlust: Schlüssel der Teilnahmen = letzte Stellen der Matrikelnummer statt zufälliger Klausur-ID.</summary>
    private static async Task AufVersion9Async(ProfOMatDbContext db, CancellationToken abbruch)
    {
        await using var transaktion = await db.Database.BeginTransactionAsync(abbruch);
        foreach (var k in await db.Set<ProfOMat.Domain.Klausuren.Klausur>().ToListAsync(abbruch))
        {
            k.SchluesselVergeben();
        }

        var info = await db.Set<Schemainfo>().SingleAsync(abbruch);
        info.Version = 9;
        await db.SaveChangesAsync(abbruch);
        await transaktion.CommitAsync(abbruch);
    }
}
