using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ProfOMat.Infrastructure.Datenbank;

/// <summary>Ergebnis je Tabelle: Zeilen und Prüfsumme in Quelle (SQLite) und Ziel (PostgreSQL).</summary>
public sealed record Tabellenvergleich(string Tabelle, long ZeilenQuelle, long ZeilenZiel, string PruefsummeQuelle, string PruefsummeZiel)
{
    public bool Gleich => ZeilenQuelle == ZeilenZiel && PruefsummeQuelle == PruefsummeZiel;

    /// <summary>Eine Zeile für Skripte: <c>UEBERNAHME|tabelle|zeilen-quelle|zeilen-ziel|gleich</c>.</summary>
    public string Zeile => FormattableString.Invariant($"UEBERNAHME|{Tabelle}|{ZeilenQuelle}|{ZeilenZiel}|{(Gleich ? "gleich" : "ABWEICHUNG")}");
}

/// <param name="Probe">true = in eine Wegwerf-Datenbank übertragen, verglichen und wieder gelöscht; die Zieldatenbank blieb unberührt.</param>
public sealed record Uebernahmebericht(string Quelle, int QuellVersion, string Ziel, bool Probe, IReadOnlyList<Tabellenvergleich> Tabellen, int Entitaeten)
{
    public bool Erfolgreich => Tabellen.All(t => t.Gleich);

    public long Zeilen => Tabellen.Sum(t => t.ZeilenZiel);
}

/// <summary>
/// Einmalige, prüfbare Übernahme einer alten SQLite-Datenbank (bis 11/2026) nach PostgreSQL (<c>ProfOMat.Web --sqlite-uebernehmen &lt;datei&gt; [--probe]</c>).
/// <list type="number">
/// <item>Die Quelle wird nie verändert: Gearbeitet wird auf einer konsistenten Kopie (SQLite-Backup, auch bei WAL), die vorher mit der
/// bekannten Aufwertungskette auf den letzten SQLite-Stand (<see cref="ProfOMatDbContext.Schemaversion"/>) gebracht wird.</item>
/// <item>Das Ziel muss leer sein (neu oder nur migriert). Es wird vollständig migriert und dann Tabelle für Tabelle per COPY befüllt –
/// alles in EINER Transaktion. Werte werden roh übertragen (verschlüsselte Felder bleiben Geheimtext, Zeitpunkte bleiben Millisekunden).</item>
/// <item>Prüfung vor dem Festschreiben: je Tabelle Zeilenzahl und SHA-256 über alle Zeilen (unabhängig von der Reihenfolge), einmal aus der
/// Quelle, einmal aus dem Ziel zurückgelesen. Weicht etwas ab, wird zurückgerollt. Danach lädt EF jede Entität einmal (Materialisierung).</item>
/// </list>
/// Mit <c>--probe</c> läuft alles in eine Wegwerf-Datenbank neben dem Ziel, die danach gelöscht wird (Trockenlauf vor dem Ausrollen).
/// </summary>
public static class SqliteUebernahme
{
    private const string Trenner = "\u001f";

    public static async Task<Uebernahmebericht> UebernehmenAsync(string sqliteDatei, IServiceProvider dienste, bool probe, Action<string> ausgabe,
        CancellationToken abbruch = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqliteDatei);
        ArgumentNullException.ThrowIfNull(dienste);
        ArgumentNullException.ThrowIfNull(ausgabe);
        var quelle = Path.GetFullPath(sqliteDatei);
        if (!File.Exists(quelle))
        {
            throw new InvalidOperationException($"SQLite-Datei {quelle} gibt es nicht.");
        }

        string zielVerbindung;
        await using (var b = dienste.CreateAsyncScope())
        {
            zielVerbindung = b.ServiceProvider.GetRequiredService<ProfOMatDbContext>().Database.GetConnectionString()
                             ?? throw new InvalidOperationException("Keine Verbindung zu PostgreSQL konfiguriert.");
        }

        var ziel = new NpgsqlConnectionStringBuilder(zielVerbindung) { Pooling = false };
        var zielName = ziel.Database ?? throw new InvalidOperationException("Die PostgreSQL-Verbindung nennt keine Datenbank.");
        if (probe)
        {
            ziel.Database = (zielName + "_uebernahmeprobe")[..Math.Min(63, zielName.Length + 16)];
            await VerwaltenAsync(ziel, $"DROP DATABASE IF EXISTS \"{ziel.Database}\" WITH (FORCE)", abbruch);
        }

        var arbeitsordner = Directory.CreateTempSubdirectory("profomat-uebernahme-").FullName;
        try
        {
            // 1. Konsistente Kopie der Quelle (inklusive WAL-Inhalt) und Aufwertung der KOPIE auf den letzten SQLite-Stand
            var kopie = Path.Combine(arbeitsordner, "quelle.db");
            await using (var q = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = quelle, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString))
            await using (var k = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = kopie, Pooling = false }.ConnectionString))
            {
                await q.OpenAsync(abbruch);
                await k.OpenAsync(abbruch);
                q.BackupDatabase(k);
            }

            var kopieVerbindung = new SqliteConnectionStringBuilder { DataSource = kopie, Pooling = false }.ConnectionString;
            var version = await QuellversionAsync(kopieVerbindung, abbruch);
            ausgabe(FormattableString.Invariant($"Quelle: {quelle} (Schemaversion {version}{(version < ProfOMatDbContext.Schemaversion ? $", wird auf der Kopie auf {ProfOMatDbContext.Schemaversion} aufgewertet" : "")})"));
            if (version > ProfOMatDbContext.Schemaversion || version < SqliteAltbestand.AeltesteAufwertbareVersion)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Die SQLite-Datei hat Schemaversion {version}; übernehmen lassen sich nur Versionen {SqliteAltbestand.AeltesteAufwertbareVersion} bis {ProfOMatDbContext.Schemaversion}."));
            }

            if (version < ProfOMatDbContext.Schemaversion)
            {
                await using var alt = new ServiceCollection()
                    .AddDbContext<ProfOMatDbContext>(o => o.UseSqlite(kopieVerbindung).ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, ModellJeAnbieter>())
                    .BuildServiceProvider();
                await SqliteAltbestand.InitialisierenAsync(alt, abbruch);
            }

            // 2. Ziel: muss leer sein, dann vollständig migrieren
            var optionen = new DbContextOptionsBuilder<ProfOMatDbContext>().UseProfOMatPostgres(ziel.ConnectionString).Options;
            await using (var db = new ProfOMatDbContext(optionen))
            {
                var stand = await Datenbankstand.PruefenAsync("anwendung", db, abbruch);
                if (stand.Stand is Standart.Fremd or Standart.NichtLesbar)
                {
                    throw new InvalidOperationException($"Zieldatenbank {ziel.Database}: {stand.Stand} – {stand.Hinweis}");
                }

                await db.Database.MigrateAsync(abbruch);
            }

            ausgabe($"Ziel: {ziel.Host}:{ziel.Port}/{ziel.Database}{(probe ? " (Probe, wird danach gelöscht)" : "")}");
            var vergleiche = await KopierenAsync(kopieVerbindung, ziel.ConnectionString, optionen, ausgabe, abbruch);

            // 3. Materialisierung: Jede Entität einmal über EF laden (Konverter, Enums, Pflichtfelder)
            var entitaeten = 0;
            try
            {
                await using var db = new ProfOMatDbContext(optionen);
                foreach (var typ in db.Model.GetEntityTypes().Where(t => !t.IsOwned() && t.GetTableName() is not null && t.ClrType != typeof(Schemainfo)))
                {
                    var menge = (IQueryable<object>)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(typ.ClrType).Invoke(db, null)!;
                    entitaeten += (await menge.AsNoTracking().IgnoreAutoIncludes().ToListAsync(abbruch)).Count;
                }
            }
            catch (Exception ex) when (!probe)
            {
                ausgabe("EF kann die übertragenen Daten nicht lesen – Ziel wird wieder geleert: " + ex.Message);
                await LeerenAsync(ziel.ConnectionString, abbruch);
                throw;
            }

            ausgabe(FormattableString.Invariant($"EF-Probe: {entitaeten} Datensätze gelesen"));
            return new Uebernahmebericht(quelle, version, ziel.Database!, probe, vergleiche, entitaeten);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            NpgsqlConnection.ClearAllPools();
            if (probe)
            {
                await VerwaltenAsync(ziel, $"DROP DATABASE IF EXISTS \"{ziel.Database}\" WITH (FORCE)", CancellationToken.None);
            }

            try
            {
                Directory.Delete(arbeitsordner, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<int> QuellversionAsync(string verbindung, CancellationToken abbruch)
    {
        await using var con = new SqliteConnection(verbindung);
        await con.OpenAsync(abbruch);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT \"Version\" FROM \"Schemainfo\" LIMIT 1";
        try
        {
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(abbruch) ?? throw new InvalidOperationException("Tabelle Schemainfo ist leer."), CultureInfo.InvariantCulture);
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException("Die Datei ist keine ProfOMat-Datenbank (Tabelle Schemainfo fehlt).", ex);
        }
    }

    /// <summary>Spalte im Ziel: Name, PostgreSQL-Typ für COPY und CLR-Typ, in dem der Wert gelesen und verglichen wird.</summary>
    private sealed record Spalte(string Name, string Datentyp, Type Typ);

    private sealed record Tabelle(string Name, IReadOnlyList<Spalte> Spalten);

    private static async Task<IReadOnlyList<Tabellenvergleich>> KopierenAsync(string quelle, string ziel, DbContextOptions<ProfOMatDbContext> optionen,
        Action<string> ausgabe, CancellationToken abbruch)
    {
        IReadOnlyList<Tabelle> tabellen;
        await using (var db = new ProfOMatDbContext(optionen))
        {
            tabellen = Tabellen(db.Model);
        }

        await using var q = new SqliteConnection(quelle);
        await q.OpenAsync(abbruch);
        await using var z = new NpgsqlConnection(ziel);
        await z.OpenAsync(abbruch);
        await using var tx = await z.BeginTransactionAsync(abbruch);
        foreach (var t in tabellen)
        {
            await using var leer = new NpgsqlCommand($"SELECT count(*) FROM \"{t.Name}\"", z, tx);
            if ((long)(await leer.ExecuteScalarAsync(abbruch))! != 0)
            {
                throw new InvalidOperationException($"Zieldatenbank ist nicht leer (Tabelle {t.Name}) – übernehmen nur in eine neue Datenbank.");
            }
        }

        var vergleiche = new List<Tabellenvergleich>();
        foreach (var t in tabellen)
        {
            var spaltenliste = string.Join(", ", t.Spalten.Select(s => "\"" + s.Name + "\""));
            var quellHashes = new List<byte[]>();
            await using (var lesen = q.CreateCommand())
            {
                lesen.CommandText = $"SELECT {spaltenliste} FROM \"{t.Name}\"";
                await using var r = await lesen.ExecuteReaderAsync(abbruch);
                await using var copy = await z.BeginBinaryImportAsync($"COPY \"{t.Name}\" ({spaltenliste}) FROM STDIN (FORMAT BINARY)", abbruch);
                var werte = new object?[t.Spalten.Count];
                while (await r.ReadAsync(abbruch))
                {
                    await copy.StartRowAsync(abbruch);
                    for (var i = 0; i < t.Spalten.Count; i++)
                    {
                        werte[i] = r.IsDBNull(i) ? null : AusSqlite(r, i, t.Spalten[i].Typ);
                        if (werte[i] is null)
                        {
                            await copy.WriteNullAsync(abbruch);
                        }
                        else
                        {
                            await copy.WriteAsync(werte[i], t.Spalten[i].Datentyp, abbruch);
                        }
                    }

                    quellHashes.Add(Zeilenhash(werte));
                }

                await copy.CompleteAsync(abbruch);
            }

            var zielHashes = new List<byte[]>();
            await using (var zurueck = new NpgsqlCommand($"SELECT {spaltenliste} FROM \"{t.Name}\"", z, tx))
            await using (var r = await zurueck.ExecuteReaderAsync(abbruch))
            {
                var werte = new object?[t.Spalten.Count];
                while (await r.ReadAsync(abbruch))
                {
                    for (var i = 0; i < t.Spalten.Count; i++)
                    {
                        werte[i] = r.IsDBNull(i) ? null : r.GetFieldValue<object>(i) is var o && o.GetType() == t.Spalten[i].Typ ? o : AusPostgres(r, i, t.Spalten[i].Typ);
                    }

                    zielHashes.Add(Zeilenhash(werte));
                }
            }

            var v = new Tabellenvergleich(t.Name, quellHashes.Count, zielHashes.Count, Gesamthash(quellHashes), Gesamthash(zielHashes));
            vergleiche.Add(v);
            ausgabe(v.Zeile);
        }

        // Zähler von Identitätsspalten (falls vorhanden) hinter den größten übernommenen Wert setzen
        foreach (var t in tabellen)
        {
            foreach (var s in t.Spalten.Where(s => s.Typ == typeof(int) || s.Typ == typeof(long)))
            {
                await using var folge = new NpgsqlCommand(
                    $"SELECT setval(seq, (SELECT COALESCE(max(\"{s.Name}\"), 0) + 1 FROM \"{t.Name}\"), false) FROM pg_get_serial_sequence('\"{t.Name}\"', '{s.Name}') AS seq WHERE seq IS NOT NULL",
                    z, tx);
                await folge.ExecuteNonQueryAsync(abbruch);
            }
        }

        if (vergleiche.Any(x => !x.Gleich))
        {
            await tx.RollbackAsync(abbruch);
            throw new InvalidOperationException("Übernahme abgebrochen und zurückgerollt – Abweichung in: "
                                                + string.Join(", ", vergleiche.Where(x => !x.Gleich).Select(x => x.Tabelle)));
        }

        await tx.CommitAsync(abbruch);   // hier erst greifen die aufgeschobenen Fremdschlüsselprüfungen
        return vergleiche;
    }

    /// <summary>
    /// Alle Tabellen des PostgreSQL-Modells mit Spalten, Eltern vor Kindern (Fremdschlüssel prüft PostgreSQL sofort beim Einfügen).
    /// </summary>
    private static IReadOnlyList<Tabelle> Tabellen(IModel modell)
    {
        var alle = modell.GetRelationalModel().Tables.Where(t => t.Schema is null or "public").OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var reihenfolge = new List<ITable>();
        var fertig = new HashSet<string>(StringComparer.Ordinal);
        while (reihenfolge.Count < alle.Count)
        {
            var bereit = alle.Where(t => !fertig.Contains(t.Name)
                                         && t.ForeignKeyConstraints.All(f => f.PrincipalTable.Name == t.Name || fertig.Contains(f.PrincipalTable.Name))).ToList();
            if (bereit.Count == 0)
            {
                throw new InvalidOperationException("Fremdschlüssel bilden einen Zyklus: " + string.Join(", ", alle.Where(t => !fertig.Contains(t.Name)).Select(t => t.Name)));
            }

            foreach (var t in bereit)
            {
                reihenfolge.Add(t);
                fertig.Add(t.Name);
            }
        }

        return reihenfolge.Select(t => new Tabelle(t.Name, t.Columns.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c =>
            {
                var mapping = c.PropertyMappings.First().TypeMapping;
                var typ = mapping.Converter?.ProviderClrType ?? mapping.ClrType;
                return new Spalte(c.Name, Basistyp(c.StoreType), Nullable.GetUnderlyingType(typ) ?? typ);
            }).ToList()))
            .ToList();
    }

    /// <summary>„character varying(40)“ → „character varying“ (COPY braucht den Typnamen ohne Länge).</summary>
    private static string Basistyp(string storeType) => storeType.Split('(')[0].Trim();

    private static object AusSqlite(SqliteDataReader r, int i, Type typ) => typ switch
    {
        _ when typ == typeof(string) => r.GetString(i),
        _ when typ == typeof(Guid) => r.GetGuid(i),
        _ when typ == typeof(bool) => r.GetBoolean(i),
        _ when typ == typeof(int) => r.GetInt32(i),
        _ when typ == typeof(long) => r.GetInt64(i),
        _ when typ == typeof(short) => r.GetInt16(i),
        _ when typ == typeof(byte) => r.GetByte(i),
        _ when typ == typeof(double) => r.GetDouble(i),
        _ when typ == typeof(float) => r.GetFloat(i),
        _ when typ == typeof(decimal) => r.GetDecimal(i),
        _ when typ == typeof(DateTime) => DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Unspecified),
        _ when typ == typeof(DateOnly) => r.GetFieldValue<DateOnly>(i),
        _ when typ == typeof(TimeOnly) => r.GetFieldValue<TimeOnly>(i),
        _ when typ == typeof(TimeSpan) => r.GetTimeSpan(i),
        _ when typ == typeof(byte[]) => r.GetFieldValue<byte[]>(i),
        _ => throw new NotSupportedException($"Spaltentyp {typ.Name} wird bei der Übernahme nicht unterstützt (Spalte {r.GetName(i)})."),
    };

    private static object AusPostgres(NpgsqlDataReader r, int i, Type typ)
        => typeof(NpgsqlDataReader).GetMethod(nameof(NpgsqlDataReader.GetFieldValue))!.MakeGenericMethod(typ).Invoke(r, [i])!;

    /// <summary>SHA-256 einer Zeile in kanonischer Textform (gleich für Quelle und Ziel, unabhängig vom Datenbanksystem).</summary>
    private static byte[] Zeilenhash(object?[] werte)
    {
        var sb = new StringBuilder();
        foreach (var w in werte)
        {
            sb.Append(w switch
            {
                null => "∅",
                string s => "s:" + s,
                Guid g => "g:" + g.ToString("D"),
                bool b => b ? "b:1" : "b:0",
                decimal d => "d:" + d.ToString("G29", CultureInfo.InvariantCulture),
                double d => "f:" + d.ToString("R", CultureInfo.InvariantCulture),
                float f => "f:" + ((double)f).ToString("R", CultureInfo.InvariantCulture),
                DateTime t => "t:" + t.Ticks.ToString(CultureInfo.InvariantCulture),
                DateOnly t => "t:" + t.DayNumber.ToString(CultureInfo.InvariantCulture),
                TimeOnly t => "t:" + t.Ticks.ToString(CultureInfo.InvariantCulture),
                TimeSpan t => "t:" + t.Ticks.ToString(CultureInfo.InvariantCulture),
                byte[] a => "x:" + Convert.ToBase64String(a),
                IFormattable f => "n:" + f.ToString(null, CultureInfo.InvariantCulture),
                _ => "?:" + w,
            });
            sb.Append(Trenner);
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>Prüfsumme über alle Zeilen einer Tabelle, unabhängig von ihrer Reihenfolge.</summary>
    private static string Gesamthash(List<byte[]> zeilen)
    {
        zeilen.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var z in zeilen)
        {
            sha.AppendData(z);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Nach einem Fehlschlag: Ziel wieder in den Zustand „leer“ bringen (das Sicherheitsprotokoll lässt sich nicht leeren, nur verwerfen).</summary>
    private static async Task LeerenAsync(string ziel, CancellationToken abbruch)
    {
        await using var z = new NpgsqlConnection(ziel);
        await z.OpenAsync(abbruch);
        await using var cmd = new NpgsqlCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", z);
        await cmd.ExecuteNonQueryAsync(abbruch);
    }

    private static async Task VerwaltenAsync(NpgsqlConnectionStringBuilder ziel, string sql, CancellationToken abbruch)
    {
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ziel.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString);
        await admin.OpenAsync(abbruch);
        await using var cmd = new NpgsqlCommand(sql, admin);
        await cmd.ExecuteNonQueryAsync(abbruch);
    }
}
