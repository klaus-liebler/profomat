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
    /// <summary>
    /// Älteste Schemaversion, die <see cref="InitialisierenAsync"/> ohne Datenverlust aufwerten kann (Kette 7 → 8 → … unten).
    /// Beim Entfernen einer Aufwertung nachziehen: Das Deploy-Skript entscheidet damit, ob eine vorhandene Datenbank übernommen
    /// wird („Migration vorhanden“) oder nur mit <c>-DbNeu</c> ausgerollt werden darf.
    /// </summary>
    public const int AeltesteAufwertbareVersion = 7;

    /// <summary>Für deploy.ps1 und ausrollen.sh (<c>ProfOMat.Web --schemainfo</c>): je eine Zeile „schlüssel=wert“.</summary>
    public static string Schemainfo =>
        FormattableString.Invariant($"schemaversion={ProfOMatDbContext.Schemaversion}\nmigrierbar_ab={AeltesteAufwertbareVersion}\n");

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
            await NurAnfuegenAsync(db, abbruch);
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

        if (version == 15)
        {
            await AufVersion16Async(db, abbruch);
            version = 16;
        }

        if (version == 16)
        {
            await SpaltenErgaenzenAsync(db, 17,
            [
                (typeof(ProfOMat.Domain.Finanzen.Topf), nameof(ProfOMat.Domain.Finanzen.Topf.Zweck), "TEXT NULL"),
                (typeof(ProfOMat.Domain.Finanzen.Topf), nameof(ProfOMat.Domain.Finanzen.Topf.Begruendung), "TEXT NULL"),
                (typeof(ProfOMat.Domain.Finanzen.Topf), nameof(ProfOMat.Domain.Finanzen.Topf.Anschaffung), "TEXT NULL"),
                (typeof(ProfOMat.Domain.Finanzen.Topf), nameof(ProfOMat.Domain.Finanzen.Topf.Laborbereich), "TEXT NULL"),
                (typeof(ProfOMat.Domain.Finanzen.Topf), nameof(ProfOMat.Domain.Finanzen.Topf.Beschluss), "TEXT NULL"),
            ], abbruch);
            version = 17;
        }

        if (version == 17)
        {
            // Drittprüfung per Einladungslink: Verfahren (bisherige Anträge: altes Verfahren) und Gültigkeit des Links
            await SpaltenErgaenzenAsync(db, 18,
            [
                (typeof(ProfOMat.Domain.Drittpruefung.Antrag), nameof(ProfOMat.Domain.Drittpruefung.Antrag.Verfahren), "TEXT NOT NULL DEFAULT 'Antrag'"),
                (typeof(ProfOMat.Domain.Drittpruefung.Antrag), nameof(ProfOMat.Domain.Drittpruefung.Antrag.GueltigBis), "INTEGER NULL"),
            ], abbruch);
            version = 18;
        }

        if (version == 18)
        {
            await AufVersion19Async(db, abbruch);
            version = 19;
        }

        if (version == 19)
        {
            await TabellenErgaenzenAsync(db, 20, ProfOMatDbContext.TabellenAbVersion20, abbruch);
            version = 20;
        }

        if (version != ProfOMatDbContext.Schemaversion)
        {
            throw new InvalidOperationException(
                $"Datenbankschema veraltet (Datei: {version?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unbekannt"}, erwartet: {ProfOMatDbContext.Schemaversion}) – "
                + "Datei löschen oder beiseitelegen (Entwicklungsphase ohne Migrationen).");
        }

        await NurAnfuegenAsync(db, abbruch);
        if (ProfOMat.Infrastructure.Sicherheit.Feldverschluesselung.Ring is not null)
        {
            await BestandVerschluesselnAsync(db, abbruch);
        }
    }

    /// <summary>Sicherheitsprotokoll: Änderungen und Löschungen verbietet die Datenbank selbst (nur anfügen).</summary>
    internal static async Task NurAnfuegenAsync(ProfOMatDbContext db, CancellationToken abbruch)
    {
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER IF NOT EXISTS \"Sicherheitsprotokoll_nur_anfuegen_update\" BEFORE UPDATE ON \"Sicherheitsprotokoll\" "
            + "BEGIN SELECT RAISE(ABORT, 'Sicherheitsprotokoll: nur anfügen'); END;", abbruch);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER IF NOT EXISTS \"Sicherheitsprotokoll_nur_anfuegen_delete\" BEFORE DELETE ON \"Sicherheitsprotokoll\" "
            + "BEGIN SELECT RAISE(ABORT, 'Sicherheitsprotokoll: nur anfügen'); END;", abbruch);
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

    /// <summary>
    /// 18 → 19 ohne Datenverlust: Anmeldung über ASP.NET Core Identity. Konten bekommen Sicherheitsstempel (je Konto zufällig – bisherige
    /// Sitzungen enden einmalig), Zähler und Ende der Sperre nach Fehlversuchen, zweiten Faktor (TOTP) und Wiederherstellungscodes; neu sind
    /// die Tabellen der verknüpften Identitätsdienste und der Passkeys. Vorhandene Passwort-Hashes (PBKDF2, Identity-Format) bleiben gültig.
    /// </summary>
    private static async Task AufVersion19Async(ProfOMatDbContext db, CancellationToken abbruch)
    {
        await using var transaktion = await db.Database.BeginTransactionAsync(abbruch);
        foreach (var anweisung in NeueTabellen(db.Database.GenerateCreateScript(), ProfOMatDbContext.TabellenAbVersion19))
        {
            await db.Database.ExecuteSqlRawAsync(anweisung, abbruch);
        }

        var konto = db.Model.FindEntityType(typeof(ProfOMat.Domain.Konten.Konto))!;
        var tabelle = konto.GetTableName()!;
        (string Eigenschaft, string Sql)[] spalten =
        [
            (nameof(ProfOMat.Domain.Konten.Konto.SicherheitsStempel), "TEXT NOT NULL DEFAULT ''"),
            (nameof(ProfOMat.Domain.Konten.Konto.Fehlversuche), "INTEGER NOT NULL DEFAULT 0"),
            (nameof(ProfOMat.Domain.Konten.Konto.GesperrtBis), "INTEGER NULL"),
            (nameof(ProfOMat.Domain.Konten.Konto.ZweiterFaktorAktiv), "INTEGER NOT NULL DEFAULT 0"),
            (nameof(ProfOMat.Domain.Konten.Konto.AuthenticatorSchluessel), "TEXT NULL"),
            (nameof(ProfOMat.Domain.Konten.Konto.Wiederherstellungscodes), "TEXT NULL"),
        ];
        foreach (var (eigenschaft, sqlTyp) in spalten)
        {
            var spalte = konto.FindProperty(eigenschaft)!.GetColumnName();
            var vorhanden = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({tabelle}) WHERE name = {spalte}").SingleAsync(abbruch);
            if (vorhanden == 0)
            {
                var sql = "ALTER TABLE \"" + tabelle + "\" ADD COLUMN \"" + spalte + "\" " + sqlTyp + ";";
                await db.Database.ExecuteSqlRawAsync(sql, abbruch);
            }
        }

        var stempel = konto.FindProperty(nameof(ProfOMat.Domain.Konten.Konto.SicherheitsStempel))!.GetColumnName();
        var stempelSql = "UPDATE \"" + tabelle + "\" SET \"" + stempel + "\" = upper(hex(randomblob(16))) WHERE \"" + stempel + "\" = '';";
        await db.Database.ExecuteSqlRawAsync(stempelSql, abbruch);

        var info = await db.Set<Schemainfo>().SingleAsync(abbruch);
        info.Version = 19;
        await db.SaveChangesAsync(abbruch);
        await transaktion.CommitAsync(abbruch);
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

    /// <summary>
    /// 15 → 16 ohne Datenverlust: Gesprächsplanung der Hausarbeiten (Einschränkungen und mitgeteilter Termin je Teammitglied, Termin je Team,
    /// Dauer/Tage/Zeitfenster/Ort/Mailvorlagen für Termin und Bestätigung/Kalender-Token je Hausarbeit). Bisherige Bestätigungen: keine Einschränkungen gemeldet.
    /// </summary>
    private static async Task AufVersion16Async(ProfOMatDbContext db, CancellationToken abbruch)
    {
        await using var transaktion = await db.Database.BeginTransactionAsync(abbruch);
        (Type Typ, string Eigenschaft, string Sql)[] spalten =
        [
            (typeof(ProfOMat.Domain.Hausarbeiten.Teammitglied), nameof(ProfOMat.Domain.Hausarbeiten.Teammitglied.Einschraenkungen), "TEXT NULL"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Teammitglied), nameof(ProfOMat.Domain.Hausarbeiten.Teammitglied.TerminMitgeteilt), "TEXT NULL"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Team), nameof(ProfOMat.Domain.Hausarbeiten.Team.Termin), "TEXT NULL"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Hausarbeit), nameof(ProfOMat.Domain.Hausarbeiten.Hausarbeit.GespraechMinuten), "INTEGER NOT NULL DEFAULT 30"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Hausarbeit), nameof(ProfOMat.Domain.Hausarbeiten.Hausarbeit.GespraechTageText), "TEXT NULL"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Hausarbeit), nameof(ProfOMat.Domain.Hausarbeiten.Hausarbeit.TagBeginnMinuten), "INTEGER NOT NULL DEFAULT 480"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Hausarbeit), nameof(ProfOMat.Domain.Hausarbeiten.Hausarbeit.TagEndeMinuten), "INTEGER NOT NULL DEFAULT 1080"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Hausarbeit), nameof(ProfOMat.Domain.Hausarbeiten.Hausarbeit.GespraechOrt), "TEXT NULL"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Hausarbeit), nameof(ProfOMat.Domain.Hausarbeiten.Hausarbeit.TerminBetreff), "TEXT NULL"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Hausarbeit), nameof(ProfOMat.Domain.Hausarbeiten.Hausarbeit.TerminVorlage), "TEXT NULL"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Hausarbeit), nameof(ProfOMat.Domain.Hausarbeiten.Hausarbeit.KalenderTokenHash), "TEXT NULL"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Hausarbeit), nameof(ProfOMat.Domain.Hausarbeiten.Hausarbeit.BestaetigungBetreff), "TEXT NULL"),
            (typeof(ProfOMat.Domain.Hausarbeiten.Hausarbeit), nameof(ProfOMat.Domain.Hausarbeiten.Hausarbeit.BestaetigungVorlage), "TEXT NULL"),
        ];
        foreach (var (typ, eigenschaft, sqlTyp) in spalten)
        {
            var et = db.Model.FindEntityType(typ)!;
            var tabelle = et.GetTableName()!;
            var spalte = et.FindProperty(eigenschaft)!.GetColumnName();
            var vorhanden = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({tabelle}) WHERE name = {spalte}").SingleAsync(abbruch);
            if (vorhanden == 0)
            {
                var sql = "ALTER TABLE \"" + tabelle + "\" ADD COLUMN \"" + spalte + "\" " + sqlTyp + ";";
                await db.Database.ExecuteSqlRawAsync(sql, abbruch);
            }
        }

        var info = await db.Set<Schemainfo>().SingleAsync(abbruch);
        info.Version = 16;
        await db.SaveChangesAsync(abbruch);
        await transaktion.CommitAsync(abbruch);
    }

    /// <summary>
    /// Aufwertung ohne Datenverlust, wenn nur Spalten dazukommen (16 → 17: Verwendungszweck, Begründung, Neu/Ersatz, Laborbereich und Beschluss
    /// der Töpfe aus der Beschlussliste der Investitionsanträge). Vorhandene Spalten werden übersprungen.
    /// </summary>
    private static async Task SpaltenErgaenzenAsync(ProfOMatDbContext db, int neueVersion, IReadOnlyList<(Type Typ, string Eigenschaft, string Sql)> spalten, CancellationToken abbruch)
    {
        await using var transaktion = await db.Database.BeginTransactionAsync(abbruch);
        foreach (var (typ, eigenschaft, sqlTyp) in spalten)
        {
            var et = db.Model.FindEntityType(typ)!;
            var tabelle = et.GetTableName()!;
            var spalte = et.FindProperty(eigenschaft)!.GetColumnName();
            var vorhanden = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({tabelle}) WHERE name = {spalte}").SingleAsync(abbruch);
            if (vorhanden == 0)
            {
                var sql = "ALTER TABLE \"" + tabelle + "\" ADD COLUMN \"" + spalte + "\" " + sqlTyp + ";";
                await db.Database.ExecuteSqlRawAsync(sql, abbruch);
            }
        }

        var info = await db.Set<Schemainfo>().SingleAsync(abbruch);
        info.Version = neueVersion;
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
