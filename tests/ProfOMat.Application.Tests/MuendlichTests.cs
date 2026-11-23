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

    [Fact]
    public async Task Protokoll_mit_Pruefenden_Studiengang_Bemerkung_und_Note()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("liebler");
        var liebler = await u.KontoAsync("liebler");
        var niemeyer = await u.KontoAsync("niemeyer");
        liebler.Anzeigename.ShouldBe("Prof. Dr. Klaus Liebler");
        niemeyer.Anzeigename.ShouldBe("Prof. Dr. Philip Niemeyer");

        var p = await Mit(u, d => d.AnlegenAsync("Regelungstechnik"));
        var (_, f1) = await Mit(u, d => d.FrageHinzufuegenAsync(p.Id, "4", 10m));
        await Mit(u, d => d.FrageBewertenAsync(p.Id, f1, 7.5m, false));
        await Mit(u, d => d.FrageBemerkenAsync(p.Id, f1, "Bode-Diagramm sicher"));

        var pruefende = await Mit(u, d => d.PruefendeAsync());
        pruefende.Select(k => k.KontoId).ShouldContain(niemeyer.Id);
        pruefende.ShouldNotContain(k => k.Name.Contains("Sekretariat"));
        (await Mit(u, d => d.StudiengaengeAsync())).ShouldNotBeEmpty();

        // PDF erst nach dem Festlegen
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.ProtokollPdfAsync(p.Id)));

        var angaben = new ProtokollAngaben("Mia Muster", "1234567", "Elektrotechnik (B.Sc., PO 2023)", niemeyer.Id, liebler.Id, 2.0m, "souverän");
        var fest = await Mit(u, d => d.ProtokollErstellenAsync(p.Id, angaben));
        (fest.Endnote, fest.Notenvorschlag, fest.WeichtAb, fest.ErstpruefendeId).ShouldBe((2.0m, 2.3m, true, niemeyer.Id));

        var (inhalt, name) = await Mit(u, d => d.ProtokollPdfAsync(p.Id));
        System.Text.Encoding.ASCII.GetString(inhalt, 0, 5).ShouldBe("%PDF-");
        name.ShouldEndWith(".pdf");
        name.ShouldNotContain("Muster");   // Dateiname ohne Personendaten

        var geladen = await Mit(u, d => d.LadenAsync(p.Id));
        (geladen.Pruefling, geladen.Studiengang, geladen.Fragen.Single().Bemerkung).ShouldBe(("Mia Muster", "Elektrotechnik (B.Sc., PO 2023)", "Bode-Diagramm sicher"));

        var eintraege = await u.InBereichAsync(d => d.GetRequiredService<IDaten>().ListeAsync(d.GetRequiredService<IDaten>().Menge<Protokolleintrag>()
            .Where(e => e.Aktion == "Muendlich.Note")));
        eintraege.Single().Text.ShouldNotContain("Muster");
    }

    [Fact]
    public async Task Protokoll_Rechte_und_nur_Pruefende_als_Pruefer()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("liebler");
        var liebler = await u.KontoAsync("liebler");
        var sekretariat = await u.KontoAsync("sekretariat");
        var p = await Mit(u, d => d.AnlegenAsync("Prüfung C"));

        // ohne Fragen keine Note
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.ProtokollErstellenAsync(p.Id, new ProtokollAngaben(null, null, null, liebler.Id, null, 1.0m, null))));
        var (_, f) = await Mit(u, d => d.FrageHinzufuegenAsync(p.Id, "1", 5m));
        await Mit(u, d => d.FrageBewertenAsync(p.Id, f, 5m, false));

        // Sekretariat ist kein Prüfende-Konto
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.ProtokollErstellenAsync(p.Id,
            new ProtokollAngaben(null, null, null, liebler.Id, sekretariat.Id, 1.0m, null))))).Message.ShouldContain("Prüfende");
        (await Mit(u, d => d.LadenAsync(p.Id))).Endnote.ShouldBeNull();

        await Mit(u, d => d.ProtokollErstellenAsync(p.Id, new ProtokollAngaben(null, null, null, liebler.Id, null, 1.0m, null)));

        await u.AlsAsync("niemeyer");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.ProtokollPdfAsync(p.Id)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.FrageBemerkenAsync(p.Id, f, "x")));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.ProtokollErstellenAsync(p.Id, new ProtokollAngaben(null, null, null, liebler.Id, null, 5.0m, null))));

        u.Benutzer.Aktuell = Benutzer.Niemand;
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.PruefendeAsync()));
    }
}
