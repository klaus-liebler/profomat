using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProfOMat.Application;
using ProfOMat.Domain.Betrieb;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Infrastructure.Sicherheit;

/// <summary>
/// Sicherheitsprotokoll in der Tabelle „Sicherheitsprotokoll“: eigener kurzlebiger Datenbankkontext je Eintrag (unabhängig von der
/// laufenden Arbeit), prozessweit nacheinander (lückenlose Nummern, Kette ohne Verzweigung). UPDATE und DELETE verhindern Trigger.
/// </summary>
internal sealed partial class Sicherheitsprotokoll(DbContextOptions<ProfOMatDbContext> optionen, TimeProvider uhr, ILogger<Sicherheitsprotokoll> log) : ISicherheitsprotokoll
{
    private static readonly SemaphoreSlim Reihenfolge = new(1, 1);

    public async Task EintragenAsync(string art, Guid? kontoId, string text, CancellationToken abbruch = default)
    {
        await Reihenfolge.WaitAsync(abbruch);
        try
        {
            await using var db = new ProfOMatDbContext(optionen);
            var letzter = await db.Set<Sicherheitsereignis>().AsNoTracking().OrderByDescending(e => e.Nummer).FirstOrDefaultAsync(abbruch);
            var e = new Sicherheitsereignis((letzter?.Nummer ?? 0) + 1, uhr.GetUtcNow(), art, kontoId, text, letzter?.Hash ?? "");
            db.Add(e);
            await db.SaveChangesAsync(abbruch);
            LogEreignis(log, e.Nummer, art, kontoId is { } k ? ProfOMat.Domain.Gemeinsam.Pseudonym.Id(k) : "-", text);
        }
        finally
        {
            Reihenfolge.Release();
        }
    }

    public async Task<IReadOnlyList<Sicherheitsereignis>> LetzteAsync(int anzahl, CancellationToken abbruch = default)
    {
        await using var db = new ProfOMatDbContext(optionen);
        return await db.Set<Sicherheitsereignis>().AsNoTracking().OrderByDescending(e => e.Nummer).Take(anzahl).ToListAsync(abbruch);
    }

    public async Task<Kettenpruefung> PruefenAsync(CancellationToken abbruch = default)
    {
        await using var db = new ProfOMatDbContext(optionen);
        long anzahl = 0;
        var vorher = "";
        await foreach (var e in db.Set<Sicherheitsereignis>().AsNoTracking().OrderBy(x => x.Nummer).AsAsyncEnumerable().WithCancellation(abbruch))
        {
            anzahl++;
            if (e.Nummer != anzahl)
            {
                return new Kettenpruefung(false, anzahl, e.Nummer, $"Lücke: Eintrag {anzahl} fehlt");
            }

            if (e.VorgaengerHash != vorher)
            {
                return new Kettenpruefung(false, anzahl, e.Nummer, "Verkettung unterbrochen");
            }

            if (!e.Stimmt)
            {
                return new Kettenpruefung(false, anzahl, e.Nummer, "Inhalt verändert");
            }

            vorher = e.Hash;
        }

        return new Kettenpruefung(true, anzahl, null, null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sicherheitsprotokoll {Nummer}: {Art} (Konto {Konto}) {Text}")]
    private static partial void LogEreignis(ILogger logger, long nummer, string art, string konto, string text);
}
