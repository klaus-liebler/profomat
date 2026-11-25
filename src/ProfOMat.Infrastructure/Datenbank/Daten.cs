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

        if (version == 9)
        {
            await TabellenErgaenzenAsync(db, 10, ProfOMatDbContext.TabellenAbVersion10, abbruch);
            version = 10;
        }

        if (version == 10)
        {
            await TabellenErgaenzenAsync(db, 11, ProfOMatDbContext.TabellenAbVersion11, abbruch);
            version = 11;
        }

        if (version == 11)
        {
            await AufVersion12Async(db, abbruch);
            version = 12;
        }

        if (version == 12)
        {
            await TabellenErgaenzenAsync(db, 13, ProfOMatDbContext.TabellenAbVersion13, abbruch);
            version = 13;
        }

        if (version == 13)
        {
            await TabellenErgaenzenAsync(db, 14, ProfOMatDbContext.TabellenAbVersion14, abbruch);
            version = 14;
        }

        if (version == 14)
        {
            await AufVersion15Async(db, abbruch);
            version = 15;
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

    /// <summary>11 → 12 ohne Datenverlust: Gefache können öffentlich sichtbar sein (bisherige bleiben privat).</summary>
    private static async Task AufVersion12Async(ProfOMatDbContext db, CancellationToken abbruch)
    {
        var typ = db.Model.FindEntityType(typeof(ProfOMat.Domain.Gefache.Gefach))!;
        var spalte = typ.FindProperty(nameof(ProfOMat.Domain.Gefache.Gefach.Oeffentlich))!.GetColumnName();
        var tabelle = typ.GetTableName()!;
        await using var transaktion = await db.Database.BeginTransactionAsync(abbruch);
        // Kam die Tabelle erst mit 10 → 11 (aus dem aktuellen Modell), hat sie die Spalte schon.
        var vorhanden = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({tabelle}) WHERE name = {spalte}")
            .SingleAsync(abbruch);
        if (vorhanden == 0)
        {
            var sql = "ALTER TABLE \"" + tabelle + "\" ADD COLUMN \"" + spalte + "\" INTEGER NOT NULL DEFAULT 0;";
            await db.Database.ExecuteSqlRawAsync(sql, abbruch);
        }

        var info = await db.Set<Schemainfo>().SingleAsync(abbruch);
        info.Version = 12;
        await db.SaveChangesAsync(abbruch);
        await transaktion.CommitAsync(abbruch);
    }

    /// <summary>14 → 15 ohne Datenverlust: neue Finanztabellen und Erinnerungs-Vorlauf der Laborteams (bisherige: 30 Tage).</summary>
    private static async Task AufVersion15Async(ProfOMatDbContext db, CancellationToken abbruch)
    {
        var typ = db.Model.FindEntityType(typeof(ProfOMat.Domain.Finanzen.Laborteam))!;
        var spalte = typ.FindProperty(nameof(ProfOMat.Domain.Finanzen.Laborteam.ErinnerungVorlaufTage))!.GetColumnName();
        var tabelle = typ.GetTableName()!;
        await using var transaktion = await db.Database.BeginTransactionAsync(abbruch);
        foreach (var anweisung in NeueTabellen(db.Database.GenerateCreateScript(), ProfOMatDbContext.TabellenAbVersion15))
        {
            await db.Database.ExecuteSqlRawAsync(anweisung, abbruch);
        }

        var vorhanden = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({tabelle}) WHERE name = {spalte}")
            .SingleAsync(abbruch);
        if (vorhanden == 0)
        {
            var sql = "ALTER TABLE \"" + tabelle + "\" ADD COLUMN \"" + spalte + "\" INTEGER NOT NULL DEFAULT "
                      + ProfOMat.Domain.Finanzen.Laborteam.StandardVorlaufTage.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
            await db.Database.ExecuteSqlRawAsync(sql, abbruch);
        }

        var info = await db.Set<Schemainfo>().SingleAsync(abbruch);
        info.Version = 15;
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

    /// <summary>
    /// Aufwertung ohne Datenverlust, wenn nur Tabellen dazukommen (9 → 10: Hausarbeiten und Mailentwürfe; 10 → 11: Gefache; 12 → 13: Finanzen; 13 → 14: Finanzplanung). Die
    /// Anweisungen stammen aus dem Erstellskript des Modells; übernommen werden nur CREATE TABLE/INDEX der neuen Tabellen.
    /// </summary>
    private static async Task TabellenErgaenzenAsync(ProfOMatDbContext db, int neueVersion, IReadOnlyCollection<string> tabellen, CancellationToken abbruch)
    {
        await using var transaktion = await db.Database.BeginTransactionAsync(abbruch);
        foreach (var anweisung in NeueTabellen(db.Database.GenerateCreateScript(), tabellen))
        {
            await db.Database.ExecuteSqlRawAsync(anweisung, abbruch);
        }

        var info = await db.Set<Schemainfo>().SingleAsync(abbruch);
        info.Version = neueVersion;
        await db.SaveChangesAsync(abbruch);
        await transaktion.CommitAsync(abbruch);
    }

    /// <summary>Aus einem Erstellskript die CREATE-Anweisungen, die eine der genannten Tabellen anlegen oder indizieren.</summary>
    internal static IReadOnlyList<string> NeueTabellen(string skript, IReadOnlyCollection<string> tabellen)
    {
        ArgumentNullException.ThrowIfNull(skript);
        ArgumentNullException.ThrowIfNull(tabellen);
        var ergebnis = new List<string>();
        foreach (var roh in skript.Split(';'))
        {
            var a = roh.Trim();
            var ziel = a.StartsWith("CREATE TABLE \"", StringComparison.Ordinal) ? a["CREATE TABLE \"".Length..].Split('"')[0]
                : a.StartsWith("CREATE ", StringComparison.Ordinal) && a.Contains(" INDEX ", StringComparison.Ordinal) && a.Contains(" ON \"", StringComparison.Ordinal)
                    ? a[(a.IndexOf(" ON \"", StringComparison.Ordinal) + 5)..].Split('"')[0]
                    : null;
            if (ziel is not null && tabellen.Contains(ziel))
            {
                // IF NOT EXISTS: eine (teilweise) vorhandene Tabelle bricht die Aufwertung nicht ab
                ergebnis.Add(a.Replace("CREATE TABLE \"", "CREATE TABLE IF NOT EXISTS \"", StringComparison.Ordinal)
                    .Replace(" INDEX \"", " INDEX IF NOT EXISTS \"", StringComparison.Ordinal) + ";");
            }
        }

        return ergebnis;
    }
}
