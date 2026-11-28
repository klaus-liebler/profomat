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
