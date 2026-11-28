using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ProfOMat.Infrastructure.Datenbank;

/// <summary>Was ein Start dieser Version mit der Datenbank tun würde (Vertrag mit dem Deploy-Kit, deploy/server/README.md).</summary>
public enum Standart
{
    /// <summary>Datenbank fehlt oder ist leer: wird beim Start angelegt und vollständig migriert.</summary>
    Neu,

    /// <summary>Alle Migrationen dieser Version sind eingespielt.</summary>
    Aktuell,

    /// <summary>Es gibt neue Migrationen, die der Start einspielt (Datenbank wird verändert – Sicherung vorher!).</summary>
    Migrierbar,

    /// <summary>
    /// Die Datenbank kennt Migrationen, die diese Version nicht hat (ältere Programmversion) oder hat Tabellen ohne Migrationsverlauf.
    /// Ein Start wäre gefährlich – abbrechen (für den Weg zurück: Rückweg des Kits).
    /// </summary>
    Fremd,

    /// <summary>Server nicht erreichbar oder Anmeldung abgelehnt.</summary>
    NichtLesbar,
}

/// <param name="Rolle">Wofür die Datenbank da ist (bei ProfOMat nur „anwendung“).</param>
public sealed record Datenbankzustand(string Rolle, string Datenbank, Standart Stand, int Eingespielt, IReadOnlyList<string> Offen, string Hinweis)
{
    /// <summary>Eine Zeile für das Deploy-Kit: <c>DATENBANKSTAND|rolle|datenbank|zustand|eingespielt|offen|hinweis</c>.</summary>
    public string Zeile => string.Join('|', "DATENBANKSTAND", Rolle, Datenbank, Stand.ToString(),
        Eingespielt.ToString(System.Globalization.CultureInfo.InvariantCulture), Offen.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Hinweis.Replace('|', '/').Replace('\n', ' '));
}

/// <summary>
/// Vorabprüfung beim Ausrollen (<c>ProfOMat.Web --datenbankstand</c>): Was würde die NEUE Version mit der vorhandenen Datenbank tun?
/// Liest nur – legt keine Datenbank an und ändert nichts. Gleicher Vertrag in allen vier Systemen (Deploy-Kit).
/// </summary>
public static class Datenbankstand
{
    /// <summary>
    /// Exitcodes für das Deploy-Kit: 0 = startbereit (auch mit Migrationen oder neu), 3 = nur mit neu aufgebauter Datenbank (-DbNeu),
    /// 4 = nicht startbar, -DbNeu hilft nicht bzw. ist verboten (fremder Stand), 5 = keine Verbindung.
    /// </summary>
    public static int Exitcode(IReadOnlyList<Datenbankzustand> zustaende)
    {
        ArgumentNullException.ThrowIfNull(zustaende);
        return zustaende.Any(z => z.Stand == Standart.NichtLesbar) ? 5
            : zustaende.Any(z => z.Stand == Standart.Fremd) ? 4
            : 0;
    }

    public static async Task<IReadOnlyList<Datenbankzustand>> PruefenAsync(IServiceProvider dienste, CancellationToken abbruch = default)
    {
        await using var bereich = dienste.CreateAsyncScope();
        return [await PruefenAsync("anwendung", bereich.ServiceProvider.GetRequiredService<ProfOMatDbContext>(), abbruch)];
    }

    public static async Task<Datenbankzustand> PruefenAsync(string rolle, ProfOMatDbContext db, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var bekannt = db.Database.GetMigrations().ToList();
        var verbindung = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false };
        var name = verbindung.Database ?? "?";
        var neuHinweis = $"fehlt – wird beim Start angelegt ({bekannt.Count} Migrationen)";
        List<string> eingespielt = [];
        long tabellen;
        try
        {
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(verbindung.ConnectionString) { Database = "postgres" }.ConnectionString);
            await admin.OpenAsync(abbruch);
            await using (var vorhanden = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", admin))
            {
                vorhanden.Parameters.AddWithValue("n", name);
                if (await vorhanden.ExecuteScalarAsync(abbruch) is null)
                {
                    return new(rolle, name, Standart.Neu, 0, bekannt, neuHinweis);
                }
            }

            await using var con = new NpgsqlConnection(verbindung.ConnectionString);
            await con.OpenAsync(abbruch);
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory'", con);
            tabellen = (long)(await cmd.ExecuteScalarAsync(abbruch))!;
            cmd.CommandText = "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL";
            if (await cmd.ExecuteScalarAsync(abbruch) is true)
            {
                cmd.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
                await using var leser = await cmd.ExecuteReaderAsync(abbruch);
                while (await leser.ReadAsync(abbruch))
                {
                    eingespielt.Add(leser.GetString(0));
                }
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return new(rolle, name, Standart.NichtLesbar, 0, [], ex.Message);
        }

        if (eingespielt.Count == 0)
        {
            return tabellen == 0
                ? new(rolle, name, Standart.Neu, 0, bekannt, neuHinweis.Replace("fehlt", "leer", StringComparison.Ordinal))
                : new(rolle, name, Standart.Fremd, 0, bekannt, "Datenbank hat Tabellen, aber keinen Migrationsverlauf – nicht von dieser Anwendung angelegt?");
        }

        var unbekannt = eingespielt.Except(bekannt, StringComparer.Ordinal).ToList();
        var offen = bekannt.Except(eingespielt, StringComparer.Ordinal).ToList();
        if (unbekannt.Count > 0)
        {
            return new(rolle, name, Standart.Fremd, eingespielt.Count, offen,
                $"Datenbank kennt Migrationen, die diese Version nicht hat ({string.Join(", ", unbekannt)}) – ältere Programmversion?");
        }

        return offen.Count == 0
            ? new(rolle, name, Standart.Aktuell, eingespielt.Count, [], $"passt ({eingespielt.Count} Migrationen)")
            : new(rolle, name, Standart.Migrierbar, eingespielt.Count, offen, $"{offen.Count} neue Migration(en): {string.Join(", ", offen)}");
    }
}
