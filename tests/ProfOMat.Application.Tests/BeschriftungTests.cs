using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Beschriftung;
using ProfOMat.Domain.Beschriftung;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Application.Tests;

public sealed class BeschriftungTests
{
    private static Task<T> Mit<T>(TestUmgebung u, Func<BeschriftungDienst, Task<T>> arbeit)
        => u.InBereichAsync(d => arbeit(d.GetRequiredService<BeschriftungDienst>()));

    private static GeraetAngaben Angaben(Geraet g, string? text, string? zusatz = null, Symbol symbol = Symbol.Keins)
        => new(g.Typ, g.Breite, g.Kennzeichen, text, zusatz, symbol, g.Hintergrund, g.Schrift);

    [Fact]
    public async Task Ablauf_speichert_Felder_Geraete_und_erzeugt_PDF()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("liebler");
        var s = await Mit(u, d => d.AnlegenAsync("Hauptverteiler Keller"));
        var feld1 = s.FelderSortiert.First().Id;
        s = await Mit(u, d => d.FeldHinzufuegenAsync(s.Id, 3));
        var feld2 = s.FelderSortiert.Last().Id;
        var (_, fi) = await Mit(u, d => d.GeraetEinsetzenAsync(s.Id, feld1, 1, 0, Geraetetyp.FI));
        var (_, ls) = await Mit(u, d => d.GeraetEinsetzenAsync(s.Id, feld1, 1, 4, Geraetetyp.LS));
        var geladen = await Mit(u, d => d.LadenAsync(s.Id));
        var lsGeraet = geladen.AlleGeraete.Single(g => g.Id == ls);
        await Mit(u, d => d.GeraetBeschriftenAsync(s.Id, ls, Angaben(lsGeraet, "Küche Steckdosen", "B16", Symbol.Steckdose)));
        var (_, kopie) = await Mit(u, d => d.GeraetKopierenAsync(s.Id, ls));
        await Mit(u, d => d.GeraetVerschiebenAsync(s.Id, kopie, feld2, 2, 0));
        await Mit(u, d => d.GeraetEntfernenAsync(s.Id, fi));

        geladen = await Mit(u, d => d.LadenAsync(s.Id));
        geladen.Felder.Count.ShouldBe(2);
        geladen.AlleGeraete.Select(g => (g.FeldId, g.Reihe, g.Start, g.Text)).OrderBy(x => x.Start)
            .ShouldBe([(feld2, 2, 0, "Küche Steckdosen"), (feld1, 1, 4, "Küche Steckdosen")]);

        var (pdf, name) = await Mit(u, d => d.PdfAsync(s.Id, [new ReiheWahl(feld1, 1), new ReiheWahl(feld2, 2)]));
        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
        name.ShouldBe("Beschriftung.pdf");
        (await Mit(u, d => d.ListeAsync())).Single().Bezeichnung.ShouldBe("Hauptverteiler Keller");
    }

    [Fact]
    public async Task Nur_wer_den_Schrank_angelegt_hat_sieht_und_aendert_ihn()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("sekretariat");   // jede angemeldete Person darf eigene Schränke anlegen
        var s = await Mit(u, d => d.AnlegenAsync("Privat"));
        var feld = s.Felder.Single().Id;

        await u.AlsAsync("liebler");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.LadenAsync(s.Id)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.GeraetEinsetzenAsync(s.Id, feld, 1, 0, Geraetetyp.LS)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.PdfAsync(s.Id, [new ReiheWahl(feld, 1)])));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit<bool>(u, async d => { await d.LoeschenAsync(s.Id); return true; }));
        (await Mit(u, d => d.ListeAsync())).ShouldBeEmpty();

        u.Benutzer.Aktuell = Benutzer.Niemand;
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AnlegenAsync("x")));
    }

    [Fact]
    public async Task PDF_nur_mit_Auswahl_und_wenn_die_Reihe_auf_A4_quer_passt()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("liebler");
        var s = await Mit(u, d => d.AnlegenAsync("Breit"));
        var feld = s.Felder.Single().Id;
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.PdfAsync(s.Id, [])))).Message.ShouldContain("mindestens");
        await Mit(u, d => d.EinstellenAsync(s.Id, "Breit", 16, 18m, 10m));   // 288 mm
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.PdfAsync(s.Id, [new ReiheWahl(feld, 1)])))).Message.ShouldContain("A4");
    }

    [Fact]
    public async Task Anlegen_mit_eigenen_Massen_und_spaeter_aendern()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("liebler");
        var s = await Mit(u, d => d.AnlegenAsync("Unterverteiler", 12, 17.5m, 12m));
        (s.TeBreiteMm, s.HoeheMm, s.ReiheBreiteMm).ShouldBe((17.5m, 12m, 210m));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AnlegenAsync("x", 12, 50m, 10m)));
        s = await Mit(u, d => d.EinstellenAsync(s.Id, "Unterverteiler", 12, 18m, 9m));
        (await Mit(u, d => d.LadenAsync(s.Id))).HoeheMm.ShouldBe(9m);
    }

    [Fact]
    public async Task Loeschen_entfernt_Felder_und_Geraete()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("liebler");
        var s = await Mit(u, d => d.AnlegenAsync("Weg"));
        await Mit(u, d => d.GeraetEinsetzenAsync(s.Id, s.Felder.Single().Id, 1, 0, Geraetetyp.LS));
        await Mit<bool>(u, async d => { await d.LoeschenAsync(s.Id); return true; });
        (await u.InBereichAsync(d => d.GetRequiredService<IDaten>().AnzahlAsync(d.GetRequiredService<IDaten>().Menge<Geraet>()))).ShouldBe(0);
        (await u.InBereichAsync(d => d.GetRequiredService<IDaten>().AnzahlAsync(d.GetRequiredService<IDaten>().Menge<Feld>()))).ShouldBe(0);
    }
}

public sealed class StreifenlayoutTests
{
    private static async Task<(Streifenlayout Layout, ITextmass Mass, TestUmgebung U)> LayoutAsync()
    {
        var u = await TestUmgebung.StartenAsync();
        return (u.Dienste.GetRequiredService<Streifenlayout>(), u.Dienste.GetRequiredService<ITextmass>(), u);
    }

    private static Geraet Geraet(Schaltschrank s, Geraetetyp typ, string? text, string? zusatz = null, Symbol symbol = Symbol.Keins, string? kz = "F1")
    {
        var f = s.Felder.Single();
        var g = s.GeraetEinsetzen(f.Id, 1, s.FreiAb(f.Id, 1, 0) == 12 ? 0 : f.Geraete.Max(x => x.Ende), typ);
        s.GeraetBeschriften(g.Id, typ, g.Breite, kz, text, zusatz, symbol, g.Hintergrund, "#000000");
        return g;
    }

    [Fact]
    public async Task Text_bleibt_in_Breite_und_Hoehe_des_Etiketts()
    {
        var (layout, mass, u) = await LayoutAsync();
        await using var _ = u;
        var s = new Schaltschrank(Guid.NewGuid(), "x", DateTimeOffset.UnixEpoch);
        foreach (var (typ, text) in new[]
                 {
                     (Geraetetyp.LS, "Küche Steckdosen Arbeitsplatte links"), (Geraetetyp.LS, "Donaudampfschifffahrtsgesellschaft"),
                     (Geraetetyp.FI, "Bad, Kinderzimmer, Flur | Obergeschoss"), (Geraetetyp.Zaehler, "Wärmepumpe"),
                 })
        {
            var g = Geraet(s, typ, text, "B16 · 30 mA", Symbol.Steckdose);
            var b = g.Breite * 18.0;
            var elemente = layout.Etikett(g, 0, b, 10);
            foreach (var t in elemente.OfType<Schrift>())
            {
                var w = mass.BreiteMm(t.Text, t.GroessePt, t.Fett);
                var links = t.Mittig ? t.X - (w / 2) : t.X;
                links.ShouldBeGreaterThanOrEqualTo(0.5, $"{text}: „{t.Text}“");
                (links + w).ShouldBeLessThanOrEqualTo(b - 0.5, $"{text}: „{t.Text}“");
                t.Y.ShouldBeLessThanOrEqualTo(10 - 0.5, $"{text}: „{t.Text}“");
                t.GroessePt.ShouldBeGreaterThanOrEqualTo(3.5 * 0.8);
            }

            elemente.OfType<Bild>().Single().Groesse.ShouldBeGreaterThan(1.5);
        }
    }

    [Fact]
    public async Task Kurzer_Text_auf_breitem_Geraet_wird_gross_gesetzt()
    {
        var (layout, _, u) = await LayoutAsync();
        await using var _ = u;
        var s = new Schaltschrank(Guid.NewGuid(), "x", DateTimeOffset.UnixEpoch);
        var g = Geraet(s, Geraetetyp.Automation, "KNX", kz: null);
        layout.Etikett(g, 0, 72, 10).OfType<Schrift>().Single().GroessePt.ShouldBeGreaterThan(9);
    }

    [Fact]
    public async Task Reihe_setzt_Trennlinien_an_Geraetegrenzen()
    {
        var (layout, _, u) = await LayoutAsync();
        await using var _ = u;
        var s = new Schaltschrank(Guid.NewGuid(), "x", DateTimeOffset.UnixEpoch);
        var f = s.Felder.Single();
        s.GeraetEinsetzen(f.Id, 1, 0, Geraetetyp.FI);
        s.GeraetEinsetzen(f.Id, 1, 4, Geraetetyp.LS);
        var r = layout.Reihe(s, f, 1);
        (r.Breite, r.Hoehe).ShouldBe((216.0, 10.0));
        r.Elemente.OfType<Strecke>().Select(l => l.X1).Order().ShouldBe([72.0, 90.0]);
        r.Elemente.OfType<Flaeche>().Single().B.ShouldBe(72.0);   // nur der FI ist farbig
    }
}
