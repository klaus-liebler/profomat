using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Muendlich;
using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Muendlich;

namespace ProfOMat.Application.Tests;

public sealed class MuendlichTests
{
    private static Task<T> Mit<T>(TestUmgebung u, Func<MuendlichDienst, Task<T>> arbeit)
        => u.InBereichAsync(d => arbeit(d.GetRequiredService<MuendlichDienst>()));

    [Fact]
    public async Task Ablauf_speichert_Zeilen_und_rechnet_die_Note()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("mustermann");
        var p = await Mit(u, d => d.AnlegenAsync("Regelungstechnik – Kandidatin 1"));
        var (_, f1) = await Mit(u, d => d.FrageHinzufuegenAsync(p.Id, "4", 10m));
        var (_, f2) = await Mit(u, d => d.FrageHinzufuegenAsync(p.Id, "11", 10m));
        await Mit(u, d => d.FrageBewertenAsync(p.Id, f1, 8m, false));
        await Mit(u, d => d.FrageBewertenAsync(p.Id, f2, null, true));

        var geladen = await Mit(u, d => d.LadenAsync(p.Id));
        geladen.FragenSortiert.Select(f => (f.Nummer, f.Stand)).ShouldBe([("4", FrageStand.Bewertet), ("11", FrageStand.Zurueckgestellt)]);
        geladen.Aktuell.Note.ShouldBe(2.0m);   // 8 von 10

        var nachgeholt = await Mit(u, d => d.FrageBewertenAsync(p.Id, f2, 10m, false));
        nachgeholt.Aktuell.Note.ShouldBe(1.3m);   // 18 von 20 = 90 %
        (await Mit(u, d => d.ListeAsync())).Single().Id.ShouldBe(p.Id);

        var protokoll = await u.InBereichAsync(d => d.GetRequiredService<IDaten>().ListeAsync(d.GetRequiredService<IDaten>().Menge<Protokolleintrag>()
            .Where(e => e.Aktion.StartsWith("Muendlich"))));
        protokoll.Single().Text.ShouldNotContain("Kandidatin");   // keine Personendaten im Protokoll
    }

    [Fact]
    public async Task Nur_Pruefende_legen_an_und_nur_die_eigene_Person_sieht_das_Protokoll()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("sekretariat");
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AnlegenAsync("x")))).Message.ShouldContain("Prüfende");

        await u.AlsAsync("mustermann");
        var p = await Mit(u, d => d.AnlegenAsync("Prüfung A"));
        var (_, f) = await Mit(u, d => d.FrageHinzufuegenAsync(p.Id, "1", 5m));

        foreach (var andere in new[] { "probst", "verwaltung" })
        {
            await u.AlsAsync(andere);
            await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.LadenAsync(p.Id)));
            await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.FrageBewertenAsync(p.Id, f, 5m, false)));
            await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.FrageHinzufuegenAsync(p.Id, "2", 5m)));
            await Should.ThrowAsync<FachlicherFehler>(() => Mit<bool>(u, async d => { await d.LoeschenAsync(p.Id); return true; }));
            (await Mit(u, d => d.ListeAsync())).ShouldBeEmpty();
        }

        u.Benutzer.Aktuell = Benutzer.Niemand;
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.LadenAsync(p.Id)));
    }

    [Fact]
    public async Task Loeschen_entfernt_Protokoll_mit_Fragen()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("mustermann");
        var p = await Mit(u, d => d.AnlegenAsync("Prüfung B"));
        await Mit(u, d => d.FrageHinzufuegenAsync(p.Id, "1", 5m));
        await Mit<bool>(u, async d => { await d.LoeschenAsync(p.Id); return true; });
        (await Mit(u, d => d.ListeAsync())).ShouldBeEmpty();
        (await u.InBereichAsync(d => d.GetRequiredService<IDaten>().AnzahlAsync(d.GetRequiredService<IDaten>().Menge<GestellteFrage>()))).ShouldBe(0);
    }
}
