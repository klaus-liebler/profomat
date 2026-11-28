using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Gefache;
using ProfOMat.Domain.Gefache;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Application.Tests;

public sealed class GefachTests
{
    private static Task<T> Mit<T>(TestUmgebung u, Func<GefachDienst, Task<T>> arbeit)
        => u.InBereichAsync(d => arbeit(d.GetRequiredService<GefachDienst>()));

    private static readonly Gefachangaben Euro = new();

    // ------------------------------------------------------------------ Fächer

    [Fact]
    public void Gleich_grosse_Faecher_teilen_den_Innenraum_abzueglich_der_Wanddicken()
    {
        Gefachplan.Faecher(Teilung.Gleich(3), 570, 3, "x").ShouldBe([188, 188, 188]);
        Gefachplan.Faecher(Teilung.Gleich(2), 370, 3, "x").ShouldBe([183.5, 183.5]);
        Gefachplan.Faecher(Teilung.Gleich(1), 370, 3, "x").ShouldBe([370]);
        Gefachplan.Mitten([188, 188, 188], 3).ShouldBe([189.5, 380.5]);
    }

    [Fact]
    public void Fachgroessen_mit_Stern_ohne_Stern_und_exakt()
    {
        Gefachplan.Faecher(new Teilung(Teilungsart.Masse, 0, "100 * *"), 570, 3, "x").ShouldBe([100, 232, 232]);
        Gefachplan.Faecher(new Teilung(Teilungsart.Masse, 0, "100; 200"), 570, 3, "x").ShouldBe([100, 200, 264]);   // Rest = eigenes Fach
        Gefachplan.Faecher(new Teilung(Teilungsart.Masse, 0, "282 285,2"), 570, 3, "x").Select(f => Math.Round(f, 6)).ShouldBe([282, 285]);   // ±0,5 mm gleicht das letzte aus
        Gefachplan.Faecher(new Teilung(Teilungsart.Masse, 0, "* 50,5"), 370, 3, "x").ShouldBe([316.5, 50.5]);
    }

    [Fact]
    public void Zu_grosse_zu_kleine_und_unlesbare_Fachgroessen_werden_begruendet_abgelehnt()
    {
        Should.Throw<FachlicherFehler>(() => Gefachplan.Faecher(new Teilung(Teilungsart.Masse, 0, "300 300"), 570, 3, "entlang der Länge"))
            .Message.ShouldContain("innen sind nur 570 mm");
        Should.Throw<FachlicherFehler>(() => Gefachplan.Faecher(new Teilung(Teilungsart.Masse, 0, "560 *"), 570, 3, "x")).Message.ShouldContain("Restfach");
        Should.Throw<FachlicherFehler>(() => Gefachplan.Faecher(Teilung.Gleich(30), 300, 8, "x")).Message.ShouldContain("mindestens 10 mm");
        Should.Throw<FachlicherFehler>(() => Teilung.MasseZerlegen("100 abc")).Message.ShouldContain("„abc“");
        Should.Throw<FachlicherFehler>(() => Teilung.MasseZerlegen("-5"));
    }

    // ------------------------------------------------------------------ Angaben

    [Fact]
    public void Gefach_darf_hoechstens_so_hoch_sein_wie_die_Stapelhoehe()
    {
        Should.Throw<FachlicherFehler>(() => (Euro with { HoeheMm = 205 }).Geprueft()).Message.ShouldContain("Stapel");
        (Euro with { HoeheMm = 120 }).Geprueft().HoeheMm.ShouldBe(120);
        Should.Throw<FachlicherFehler>(() => (Euro with { BodenLaengeMm = 580 }).Geprueft()).Message.ShouldContain("Boden");
        Should.Throw<FachlicherFehler>(() => (Euro with { DickeMm = 0.5m }).Geprueft()).Message.ShouldContain("Holzdicke");
        Should.Throw<FachlicherFehler>(() => (Euro with { Laenge = Teilung.Gleich(0) }).Geprueft()).Message.ShouldContain("Länge");
        Should.Throw<FachlicherFehler>(() => (Euro with { Breite = new Teilung(Teilungsart.Masse, 1, " ") }).Geprueft()).Message.ShouldContain("Fachgrößen");
    }

    // ------------------------------------------------------------------ Wände

    [Fact]
    public void Euro_Lagerbox_3x2_ergibt_eine_Laengswand_und_zwei_Querwaende_mit_Kreuzschlitzen()
    {
        var p = Gefachplan.Berechnen(Euro);
        p.Freigabe.Erlaubt.ShouldBeTrue(p.Freigabe.Begruendung);
        p.Waende.Select(w => w.Name).ShouldBe(["L1", "Q1", "Q2"]);
        var l = p.Laengswaende.Single();
        (l.Laenge, l.Hoehe, l.Lage).ShouldBe((568, 204, 185));
        p.Querwaende.Select(q => q.Lage).ShouldBe([189.5, 380.5]);
        p.Querwaende.First().Laenge.ShouldBe(368);

        // Längswand: zwei Schlitze von oben, 3,2 mm breit, bis zur halben Höhe; Wandkoordinaten = Kiste − Spiel (1 mm)
        var schlitzL = l.Kontur.Where(e => Math.Abs(e.Y - 102) < 1e-9).Select(e => Math.Round(e.X, 3)).Order().ToList();
        schlitzL.ShouldBe([186.9, 190.1, 377.9, 381.1]);
        // Querwand: ein Schlitz von unten in der Mitte (Längswand bei 185 − 1 = 184)
        var schlitzQ = p.Querwaende.First().Kontur.Where(e => Math.Abs(e.Y - 102) < 1e-9).Select(e => Math.Round(e.X, 3)).Order().ToList();
        schlitzQ.ShouldBe([182.4, 185.6]);
    }

    [Fact]
    public void Untere_Ecken_folgen_der_Rundung_am_Kistenboden()
    {
        var p = Gefachplan.Berechnen(Euro with { RadiusMm = 12 });
        var k = p.Laengswaende.Single().Kontur;
        k[0].X.ShouldBe(12, 1e-9);                                        // Boden beginnt hinter der Rundung
        k[0].Y.ShouldBe(0);
        var boegen = k.Where(e => e.Bulge != 0).ToList();
        boegen.Count.ShouldBe(2);
        boegen.ShouldAllBe(e => Math.Abs(e.Bulge - Math.Tan(Math.PI / 8)) < 1e-9);   // Viertelkreis gegen den Uhrzeigersinn
        k[^1].X.ShouldBe(0, 1e-9);
        k[^1].Y.ShouldBe(12, 1e-9);
        Gefachplan.Berechnen(Euro with { RadiusMm = 0 }).Laengswaende.Single().Kontur.ShouldAllBe(e => e.Bulge == 0);
    }

    [Fact]
    public void Schraege_Kistenwaende_ergeben_trapezfoermige_Waende()
    {
        var p = Gefachplan.Berechnen(Euro with { BodenLaengeMm = 550, BodenBreiteMm = 350, RadiusMm = 0 });
        p.Freigabe.Erlaubt.ShouldBeTrue(p.Freigabe.Begruendung);
        var k = p.Laengswaende.Single().Kontur;
        k.ShouldContain(new Ecke(10, 0));
        k.ShouldContain(new Ecke(558, 0));
        k.ShouldContain(new Ecke(568, 204));
        k.ShouldContain(new Ecke(0, 204));
    }

    [Fact]
    public void Wand_zu_nah_an_der_Rundung_oder_zu_viele_Faecher_wird_begruendet()
    {
        var p = Gefachplan.Berechnen(Euro with { Breite = new Teilung(Teilungsart.Masse, 0, "12 *"), RadiusMm = 10 });
        p.Freigabe.Erlaubt.ShouldBeFalse();
        p.Freigabe.Begruendung!.ShouldContain("Rundung");
        p.Waende.ShouldBeEmpty();

        var leer = Gefachplan.Berechnen(Euro with { Laenge = Teilung.Gleich(1), Breite = Teilung.Gleich(1) });
        leer.Freigabe.Begruendung!.ShouldContain("nichts zu schneiden");
    }

    // ------------------------------------------------------------------ Platten

    [Fact]
    public void Euro_Lagerbox_3x2_passt_auf_eine_Platte_800x600()
    {
        var p = Gefachplan.Berechnen(Euro);
        p.Platten.Count.ShouldBe(1);
        p.Platten[0].Teile.Count.ShouldBe(3);
        PruefeBelegung(p);
    }

    [Theory]
    [InlineData(6, 4, 800, 600, 3)]
    [InlineData(4, 3, 600, 400, 4)]
    [InlineData(2, 8, 1000, 700, 4)]
    [InlineData(5, 5, 800, 600, 3)]
    public void Belegung_ueberlappt_nie_und_bleibt_auf_der_Platte(int laenge, int breite, int pl, int pb, int abstand)
    {
        var p = Gefachplan.Berechnen(Euro with
        {
            Laenge = Teilung.Gleich(laenge), Breite = Teilung.Gleich(breite), PlatteLaengeMm = pl, PlatteBreiteMm = pb, AbstandMm = abstand,
        });
        p.Freigabe.Erlaubt.ShouldBeTrue(p.Freigabe.Begruendung);
        p.Platten.SelectMany(x => x.Teile).Count().ShouldBe(laenge - 1 + breite - 1);
        PruefeBelegung(p);
    }

    [Fact]
    public void Hohe_Waende_werden_gedreht_wenn_es_quer_nicht_passt()
    {
        // 568 mm lange Längswand passt nur hochkant auf 400 × 600
        var p = Gefachplan.Berechnen(Euro with { PlatteLaengeMm = 400, PlatteBreiteMm = 600, Laenge = Teilung.Gleich(1) });
        p.Freigabe.Erlaubt.ShouldBeTrue(p.Freigabe.Begruendung);
        p.Platten.SelectMany(x => x.Teile).ShouldAllBe(t => t.Gedreht);
        PruefeBelegung(p);
    }

    [Fact]
    public void Wand_groesser_als_die_Platte_wird_begruendet()
    {
        var p = Gefachplan.Berechnen(Euro with { PlatteLaengeMm = 500, PlatteBreiteMm = 400 });
        p.Freigabe.Erlaubt.ShouldBeFalse();
        p.Freigabe.Begruendung!.ShouldContain("passt nicht auf die Platte");
    }

    private static void PruefeBelegung(Gefachplan p)
    {
        var a = (double)p.Angaben.AbstandMm;
        foreach (var platte in p.Platten)
        {
            foreach (var t in platte.Teile)
            {
                t.X.ShouldBeGreaterThanOrEqualTo(a - 1e-9);
                t.Y.ShouldBeGreaterThanOrEqualTo(a - 1e-9);
                (t.X + t.Breite).ShouldBeLessThanOrEqualTo((double)p.Angaben.PlatteLaengeMm - a + 1e-9);
                (t.Y + t.Hoehe).ShouldBeLessThanOrEqualTo((double)p.Angaben.PlatteBreiteMm - a + 1e-9);
                foreach (var (x, y) in t.Wand.Kontur.Select(e => t.Abbilden(e.X, e.Y)))
                {
                    x.ShouldBeInRange(t.X - 1e-9, t.X + t.Breite + 1e-9);
                    y.ShouldBeInRange(t.Y - 1e-9, t.Y + t.Hoehe + 1e-9);
                }
            }

            for (var i = 0; i < platte.Teile.Count; i++)
            {
                for (var j = i + 1; j < platte.Teile.Count; j++)
                {
                    var (s, o) = (platte.Teile[i], platte.Teile[j]);
                    var getrennt = s.X + s.Breite + a <= o.X + 1e-9 || o.X + o.Breite + a <= s.X + 1e-9
                                   || s.Y + s.Hoehe + a <= o.Y + 1e-9 || o.Y + o.Hoehe + a <= s.Y + 1e-9;
                    getrennt.ShouldBeTrue($"{s.Wand.Name} und {o.Wand.Name} auf Platte {platte.Nr} überlappen");
                }
            }
        }
    }

    // ------------------------------------------------------------------ Dateien

    [Fact]
    public void SVG_in_mm_mit_Schnitt_rot_und_Gravur_blau()
    {
        var p = Gefachplan.Berechnen(Euro);
        var svg = Schnittvorlage.Svg(p, p.Platten[0], "Kiste <Keller>");
        svg.ShouldStartWith("<?xml");
        svg.ShouldContain("width=\"800mm\" height=\"600mm\"");
        svg.ShouldContain("viewBox=\"0 0 800 600\"");
        svg.ShouldContain("stroke=\"#FF0000\"");
        svg.ShouldContain("fill=\"#0000FF\"");
        svg.ShouldContain("Kiste &lt;Keller&gt;");
        System.Text.RegularExpressions.Regex.Matches(svg, "<path ").Count.ShouldBe(3);
        svg.ShouldContain(">L1 links</text>");
        svg.ShouldContain(">Q2 vorn</text>");
        svg.ShouldContain(" A10 10 0 0 0 ");   // Rundung r = 10 (Drehsinn siehe unten)

        var ohne = Gefachplan.Berechnen(Euro with { Gravur = false });
        Schnittvorlage.Svg(ohne, ohne.Platten[0], "x").ShouldNotContain("<text");
        Schnittvorlage.Svg(p, p.Platten[0], "x", vorschau: true).ShouldNotContain("mm\"");
    }

    [Fact]
    public void DXF_R12_mit_geschlossenen_Polylinien_Boegen_und_Text()
    {
        var p = Gefachplan.Berechnen(Euro);
        var dxf = Schnittvorlage.Dxf(p, p.Platten[0]);
        var zeilen = dxf.Split("\r\n");
        zeilen[..4].ShouldBe(["0", "SECTION", "2", "HEADER"]);
        zeilen.Count(z => z == "POLYLINE").ShouldBe(3);
        zeilen.Count(z => z == "SEQEND").ShouldBe(3);
        zeilen.Count(z => z == "TEXT").ShouldBe(3);
        zeilen.Count(z => z == "42").ShouldBe(6);   // je Wand zwei Rundungen
        zeilen.ShouldContain("AC1009");
        zeilen[^2].ShouldBe("EOF");
        // y ist umgekehrt: alle Koordinaten liegen auf der Platte
        for (var i = 0; i < zeilen.Length - 1; i++)
        {
            if (zeilen[i] is "10" or "20" && zeilen[i - 1] != "$EXTMIN" && zeilen[i - 1] != "$EXTMAX")
            {
                double.Parse(zeilen[i + 1], System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(0, 800);
            }
        }
    }

    [Theory]
    [InlineData(800, 600, 570, 370)]   // quer
    [InlineData(400, 600, 570, 370)]   // hochkant
    [InlineData(800, 600, 550, 350)]   // schräge Wände
    public void SVG_Boegen_runden_die_Ecken_ab_statt_sie_einzukerben(int pl, int pb, int bodenL, int bodenB)
    {
        // Rundung am Kistenboden (Innenradius der Kiste) → Wandecke nach außen abgerundet: Bogenmittelpunkt liegt im Teil, mindestens r vom Rand
        var p = Gefachplan.Berechnen(Euro with
        {
            PlatteLaengeMm = pl, PlatteBreiteMm = pb, BodenLaengeMm = bodenL, BodenBreiteMm = bodenB, RadiusMm = 15, Laenge = Teilung.Gleich(2),
        });
        p.Freigabe.Erlaubt.ShouldBeTrue(p.Freigabe.Begruendung);
        var boegen = 0;
        foreach (var t in p.Platten.SelectMany(x => x.Teile))
        {
            var pfad = Schnittvorlage.Pfad(t);
            var befehle = System.Text.RegularExpressions.Regex.Matches(pfad, "([MLA])([^MLAZ]*)");
            var (x0, y0) = (0.0, 0.0);
            foreach (System.Text.RegularExpressions.Match b in befehle)
            {
                var z = b.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var (x1, y1) = (z[^2], z[^1]);
                if (b.Groups[1].Value == "A")
                {
                    boegen++;
                    var (r, gross, sweep) = (z[0], z[3] == 1, z[4] == 1);
                    // Mittelpunkt nach SVG 1.1, Anhang F.6.5 (ohne Drehung)
                    var (hx, hy) = ((x0 - x1) / 2, (y0 - y1) / 2);
                    var f = Math.Sqrt(Math.Max(0, (r * r - hx * hx - hy * hy) / (hx * hx + hy * hy))) * (gross != sweep ? 1 : -1);
                    var (cx, cy) = (f * hy + (x0 + x1) / 2, -f * hx + (y0 + y1) / 2);
                    var randabstand = new[] { cx - t.X, t.X + t.Breite - cx, cy - t.Y, t.Y + t.Hoehe - cy }.Min();
                    randabstand.ShouldBeGreaterThan(r - 0.01, $"{t.Wand.Name}: Bogen bei ({x1:0.#}|{y1:0.#}) kerbt die Ecke ein");
                }

                (x0, y0) = (x1, y1);
            }
        }

        boegen.ShouldBe(p.Waende.Count * 2);
    }

    // ------------------------------------------------------------------ Dienst

    [Fact]
    public async Task Ablauf_anlegen_einstellen_herunterladen_kopieren_loeschen()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("liebler");
        var g = await Mit(u, d => d.AnlegenAsync("Schrauben-Kiste"));
        g.Angaben.ShouldBe(new Gefachangaben());
        g = await Mit(u, d => d.EinstellenAsync(g.Id, g.Angaben with { HoeheMm = 100, Laenge = new Teilung(Teilungsart.Masse, 3, "100 * *"), Breite = Teilung.Gleich(3) }));
        var geladen = await Mit(u, d => d.LadenAsync(g.Id));
        geladen.HoeheMm.ShouldBe(100);
        geladen.Angaben.Laenge.ShouldBe(new Teilung(Teilungsart.Masse, 3, "100 * *"));

        var zip = await Mit(u, d => d.DateiAsync(g.Id, "schnittvorlagen.zip"));
        zip.Dateiname.ShouldBe("Schrauben-Kiste.zip");
        using (var archiv = new ZipArchive(new MemoryStream(zip.Inhalt)))
        {
            var plan = Gefachplan.Berechnen(geladen.Angaben);
            archiv.Entries.Select(e => e.Name).ShouldBe(plan.Platten.SelectMany(x => new[] { $"platte-{x.Nr}.svg", $"platte-{x.Nr}.dxf" }));
        }

        var svg = await Mit(u, d => d.DateiAsync(g.Id, "platte-1.svg"));
        (svg.Typ, svg.Dateiname).ShouldBe(("image/svg+xml", "Schrauben-Kiste-platte-1.svg"));
        Encoding.UTF8.GetString(svg.Inhalt).ShouldContain("height=\"600mm\"");
        (await Mit(u, d => d.DateiAsync(g.Id, "platte-1.dxf"))).Typ.ShouldBe("application/dxf");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.DateiAsync(g.Id, "platte-99.svg")));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.DateiAsync(g.Id, "../geheim.svg")));

        var kopie = await Mit(u, d => d.KopierenAsync(g.Id));
        kopie.Bezeichnung.ShouldBe("Schrauben-Kiste (Kopie)");
        kopie.Angaben.ShouldBe(geladen.Angaben);
        await Mit<bool>(u, async d => { await d.LoeschenAsync(g.Id); return true; });
        (await Mit(u, d => d.ListeAsync())).Select(x => x.Id).ShouldBe([kopie.Id]);
    }

    [Fact]
    public async Task Ungueltige_Angaben_werden_nicht_gespeichert_und_Dateien_nur_mit_gueltigem_Plan()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("liebler");
        var g = await Mit(u, d => d.AnlegenAsync("Test"));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EinstellenAsync(g.Id, g.Angaben with { HoeheMm = 300 })));
        (await Mit(u, d => d.LadenAsync(g.Id))).HoeheMm.ShouldBe(204);

        await Mit(u, d => d.EinstellenAsync(g.Id, g.Angaben with { PlatteLaengeMm = 300, PlatteBreiteMm = 300 }));
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.DateiAsync(g.Id, "schnittvorlagen.zip")))).Message.ShouldContain("passt nicht");
    }

    [Fact]
    public async Task Nur_wer_das_Gefach_angelegt_hat_sieht_und_aendert_es()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("sekretariat");   // jede angemeldete Person darf eigene Gefache anlegen
        var g = await Mit(u, d => d.AnlegenAsync("Privat"));

        await u.AlsAsync("liebler");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.LadenAsync(g.Id)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EinstellenAsync(g.Id, new Gefachangaben())));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.UmbenennenAsync(g.Id, "Meins")));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.KopierenAsync(g.Id)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.DateiAsync(g.Id, "schnittvorlagen.zip")));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit<bool>(u, async d => { await d.LoeschenAsync(g.Id); return true; }));
        (await Mit(u, d => d.ListeAsync())).ShouldBeEmpty();

        u.Benutzer.Aktuell = Benutzer.Niemand;
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AnlegenAsync("x")));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.ListeAsync()));
    }

    [Fact]
    public void Dateiname_aus_der_Bezeichnung()
    {
        GefachDienst.Dateiname("Größe / Kiste 1").ShouldBe("Grosse-Kiste-1");
        GefachDienst.Dateiname("///").ShouldBe("gefach");
    }

    [Fact]
    public async Task Datenbank_von_Version_10_bekommt_die_Tabelle_der_Gefache_ohne_Datenverlust()
    {
        await using var u = await TestUmgebung.StartenAsync(sqlite: true);
        var konten = await u.InBereichAsync(async d =>
        {
            var db = d.GetRequiredService<ProfOMatDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Gefache\";");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Schemainfo\" SET \"Version\" = 10;");
            return await db.Set<ProfOMat.Domain.Konten.Konto>().CountAsync();
        });

        await SqliteAltbestand.InitialisierenAsync(u.Dienste, TestContext.Current.CancellationToken);

        await u.AlsAsync("liebler");
        await Mit(u, d => d.AnlegenAsync("Nach der Aufwertung"));
        (await Mit(u, d => d.ListeAsync())).Count.ShouldBe(1);
        (await u.InBereichAsync(d => d.GetRequiredService<ProfOMatDbContext>().Set<ProfOMat.Domain.Konten.Konto>().CountAsync())).ShouldBe(konten);
        (await u.InBereichAsync(d => d.GetRequiredService<ProfOMatDbContext>().Set<Schemainfo>().SingleAsync())).Version.ShouldBe(ProfOMatDbContext.Schemaversion);
    }

    [Fact]
    public async Task Datenbank_von_Version_11_bekommt_die_Spalte_Oeffentlich_ohne_Datenverlust()
    {
        await using var u = await TestUmgebung.StartenAsync(sqlite: true);
        await u.AlsAsync("liebler");
        var g = await Mit(u, d => d.AnlegenAsync("Vorher angelegt", Euro with { HoeheMm = 150 }));
        await u.InBereichAsync(async d =>
        {
            var db = d.GetRequiredService<ProfOMatDbContext>();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Gefache\" DROP COLUMN \"Oeffentlich\";");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Schemainfo\" SET \"Version\" = 11;");
        });

        await SqliteAltbestand.InitialisierenAsync(u.Dienste, TestContext.Current.CancellationToken);

        var geladen = await Mit(u, d => d.LadenAsync(g.Id));
        (geladen.HoeheMm, geladen.Oeffentlich).ShouldBe((150m, false));
        (await Mit(u, d => d.SichtbarkeitSetzenAsync(g.Id, true))).Oeffentlich.ShouldBeTrue();
        (await u.InBereichAsync(d => d.GetRequiredService<ProfOMatDbContext>().Set<Schemainfo>().SingleAsync())).Version.ShouldBe(ProfOMatDbContext.Schemaversion);
    }

    // ------------------------------------------------------------------ öffentlich sichtbar

    [Fact]
    public async Task Oeffentliche_Gefache_sehen_alle_aendern_nur_wer_sie_angelegt_hat()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("liebler");
        var g = await Mit(u, d => d.AnlegenAsync("Euro-Box Werkstatt", Euro with { Breite = Teilung.Gleich(4) }));
        var privat = await Mit(u, d => d.AnlegenAsync("Privat"));
        g.Oeffentlich.ShouldBeFalse();
        (await Mit(u, d => d.SichtbarkeitSetzenAsync(g.Id, true))).Oeffentlich.ShouldBeTrue();
        (await Mit(u, d => d.OeffentlicheAsync())).ShouldBeEmpty();   // eigene stehen unter „Meine Gefache“
        (await Mit(u, d => d.AnsehenAsync(g.Id))).Eigenes.ShouldBeTrue();

        await u.AlsAsync("sekretariat");
        var liste = await Mit(u, d => d.OeffentlicheAsync());
        liste.Select(a => (a.Gefach.Bezeichnung, a.Von, a.Eigenes)).ShouldBe([("Euro-Box Werkstatt", "Prof. Dr. Klaus Liebler", false)]);
        var sicht = await Mit(u, d => d.AnsehenAsync(g.Id));
        (sicht.Eigenes, sicht.Gefach.BreiteAnzahl).ShouldBe((false, 4));
        (await Mit(u, d => d.DateiAsync(g.Id, "schnittvorlagen.zip"))).Inhalt.Length.ShouldBeGreaterThan(0);

        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EinstellenAsync(g.Id, Euro)))).Message.ShouldContain("Vorlage");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.UmbenennenAsync(g.Id, "Meins")));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.SichtbarkeitSetzenAsync(g.Id, false)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.LadenAsync(g.Id)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit<bool>(u, async d => { await d.LoeschenAsync(g.Id); return true; }));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AnsehenAsync(privat.Id)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.UebernehmenAsync(privat.Id)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.DateiAsync(privat.Id, "schnittvorlagen.zip")));

        // als Vorlage übernehmen – mit ausprobierten, nicht gespeicherten Werten
        var vorlage = await Mit(u, d => d.UebernehmenAsync(g.Id, sicht.Gefach.Angaben with { HoeheMm = 99 }));
        (vorlage.Bezeichnung, vorlage.HoeheMm, vorlage.BreiteAnzahl, vorlage.Oeffentlich).ShouldBe(("Euro-Box Werkstatt", 99m, 4, false));
        (await Mit(u, d => d.ListeAsync())).Select(x => x.Id).ShouldBe([vorlage.Id]);
        await Mit(u, d => d.EinstellenAsync(vorlage.Id, vorlage.Angaben with { HoeheMm = 80 }));   // die eigene Kopie ist änderbar

        await u.AlsAsync("liebler");
        (await Mit(u, d => d.LadenAsync(g.Id))).HoeheMm.ShouldBe(204);   // Original unverändert
        await Mit(u, d => d.SichtbarkeitSetzenAsync(g.Id, false));

        await u.AlsAsync("sekretariat");
        (await Mit(u, d => d.OeffentlicheAsync())).ShouldBeEmpty();
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AnsehenAsync(g.Id)));

        u.Benutzer.Aktuell = Benutzer.Niemand;
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.OeffentlicheAsync()));
    }
}
