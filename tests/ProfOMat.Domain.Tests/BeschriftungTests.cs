using ProfOMat.Domain.Beschriftung;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Tests;

public sealed class SchaltschrankTests
{
    private static readonly DateTimeOffset Jetzt = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    private static (Schaltschrank S, Feld F) Schrank()
    {
        var s = new Schaltschrank(Guid.NewGuid(), "Hauptverteiler", Jetzt);
        return (s, s.Felder.Single());
    }

    [Fact]
    public void Neuer_Schrank_hat_ein_Feld_mit_vier_Reihen_zu_12_TE()
    {
        var (s, f) = Schrank();
        (s.TeProReihe, s.TeBreiteMm, s.HoeheMm, f.Reihen, s.ReiheBreiteMm).ShouldBe((12, 18m, 10m, 4, 216m));
    }

    [Fact]
    public void Geraete_bekommen_Breite_und_Farbe_ihrer_Art_aber_kein_Kennzeichen()
    {
        var (s, f) = Schrank();
        var fi = s.GeraetEinsetzen(f.Id, 1, 0, Geraetetyp.FI);
        var ls1 = s.GeraetEinsetzen(f.Id, 1, 4, Geraetetyp.LS);
        var ls2 = s.GeraetEinsetzen(f.Id, 1, 5, Geraetetyp.LS);
        var zaehler = s.GeraetEinsetzen(f.Id, 2, 0, Geraetetyp.Zaehler);
        (fi.Breite, fi.Kennzeichen, fi.Hintergrund).ShouldBe((4, null, "#FFF3B0"));   // Kennzeichen kommen aus dem Stromlaufplan
        (ls1.Breite, ls1.Kennzeichen, ls2.Kennzeichen, zaehler.Kennzeichen, zaehler.Breite).ShouldBe((1, null, null, null, 4));
        s.FreiAb(f.Id, 1, 6).ShouldBe(6);
        s.FreiAb(f.Id, 1, 2).ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 0, "schon")]     // belegt durch den FI
    [InlineData(3, 0, "schon")]     // überlappt das Ende des FI
    [InlineData(10, 0, "passt nicht")]   // 4 TE ab Platz 11
    [InlineData(-1, 0, "passt nicht")]
    [InlineData(4, 5, "Reihen")]    // Reihe 5 gibt es nicht
    public void Kein_Ueberlappen_und_nichts_ausserhalb(int start, int reiheVersatz, string meldung)
    {
        var (s, f) = Schrank();
        s.GeraetEinsetzen(f.Id, 1, 0, Geraetetyp.FI);
        Should.Throw<FachlicherFehler>(() => s.GeraetEinsetzen(f.Id, 1 + reiheVersatz, start, Geraetetyp.FI)).Message.ShouldContain(meldung);
    }

    [Fact]
    public void Verschieben_in_ein_anderes_Feld_und_Breite_aendern()
    {
        var (s, f1) = Schrank();
        var f2 = s.FeldHinzufuegen(3);
        var g = s.GeraetEinsetzen(f1.Id, 1, 0, Geraetetyp.RCBO);
        var h = s.GeraetEinsetzen(f1.Id, 1, 2, Geraetetyp.LS);
        Should.Throw<FachlicherFehler>(() => s.GeraetBeschriften(g.Id, Geraetetyp.RCBO, 3, null, "x", null, Symbol.Keins, "#FFFFFF", "#000000"));
        s.GeraetVerschieben(g.Id, f2.Id, 3, 10);
        (g.FeldId, g.Reihe, g.Start).ShouldBe((f2.Id, 3, 10));
        f1.Geraete.ShouldBe([h]);
        f2.Geraete.ShouldBe([g]);
        Should.Throw<FachlicherFehler>(() => s.GeraetVerschieben(g.Id, f2.Id, 3, 11));
        s.GeraetVerschieben(h.Id, f1.Id, 1, 0);   // auf sich selbst überlappend ist erlaubt
    }

    [Fact]
    public void Beschriften_prueft_Farben_und_kuerzt_Texte()
    {
        var (s, f) = Schrank();
        var g = s.GeraetEinsetzen(f.Id, 1, 0, Geraetetyp.LS);
        Should.Throw<FachlicherFehler>(() => s.GeraetBeschriften(g.Id, Geraetetyp.LS, 1, null, null, null, Symbol.Licht, "rot", "#000000"));
        s.GeraetBeschriften(g.Id, Geraetetyp.LS, 1, "  F9 ", new string('a', 80), " ", Symbol.Licht, "#ffeeaa", "#000000");
        (g.Kennzeichen, g.Text!.Length, g.Zusatz, g.Hintergrund, g.Symbol).ShouldBe(("F9", Geraet.TextMaxLaenge, null, "#FFEEAA", Symbol.Licht));
    }

    [Fact]
    public void Artwechsel_uebernimmt_Voreinstellungsfarbe_nur_wenn_unveraendert()
    {
        var (s, f) = Schrank();
        var g = s.GeraetEinsetzen(f.Id, 1, 0, Geraetetyp.LS);
        s.GeraetBeschriften(g.Id, Geraetetyp.Schuetz, 2, null, null, null, Symbol.Keins, g.Hintergrund, "#000000");
        g.Hintergrund.ShouldBe(Geraetearten.Von(Geraetetyp.Schuetz).Hintergrund);
        s.GeraetBeschriften(g.Id, Geraetetyp.Schuetz, 2, null, null, null, Symbol.Keins, "#123456", "#000000");
        s.GeraetBeschriften(g.Id, Geraetetyp.LS, 2, null, null, null, Symbol.Keins, "#123456", "#000000");
        g.Hintergrund.ShouldBe("#123456");
    }

    [Fact]
    public void Kopie_rechts_daneben_ohne_Kennzeichen()
    {
        var (s, f) = Schrank();
        var g = s.GeraetEinsetzen(f.Id, 1, 0, Geraetetyp.LS);
        s.GeraetBeschriften(g.Id, Geraetetyp.LS, 1, "F1", "Küche", "B16", Symbol.Steckdose, "#FFFFFF", "#000000");
        s.GeraetEinsetzen(f.Id, 1, 1, Geraetetyp.LS);
        var k = s.GeraetKopieren(g.Id);
        (k.Start, k.Text, k.Zusatz, k.Symbol, k.Kennzeichen).ShouldBe((2, "Küche", "B16", Symbol.Steckdose, null));
        s.GeraetEinsetzen(f.Id, 1, 3, Geraetetyp.Automation);
        s.GeraetEinsetzen(f.Id, 1, 7, Geraetetyp.Automation);
        s.GeraetEinsetzen(f.Id, 1, 11, Geraetetyp.LS);
        Should.Throw<FachlicherFehler>(() => s.GeraetKopieren(g.Id)).Message.ShouldContain("kein Platz");
    }

    [Fact]
    public void Reihen_und_Teilung_nur_aendern_wenn_keine_Geraete_wegfallen()
    {
        var (s, f) = Schrank();
        s.GeraetEinsetzen(f.Id, 4, 10, Geraetetyp.RCBO);
        Should.Throw<FachlicherFehler>(() => s.FeldAendern(f.Id, null, 3)).Message.ShouldContain("Reihe 4");
        Should.Throw<FachlicherFehler>(() => s.Einstellen("x", 11, 18m, 10m));
        Should.Throw<FachlicherFehler>(() => s.Einstellen("x", 12, 40m, 10m));
        Should.Throw<FachlicherFehler>(() => s.Einstellen(" ", 12, 18m, 10m));
        s.Einstellen("Unterverteiler", 24, 17.5m, 12m);
        s.FeldAendern(f.Id, "links", 6);
        (s.ReiheBreiteMm, f.Bezeichnung, f.Reihen).ShouldBe((420m, "links", 6));
        Should.Throw<FachlicherFehler>(() => s.FeldEntfernen(f.Id)).Message.ShouldContain("Mindestens");
    }

    [Fact]
    public void Jedes_Symbol_hat_Name_und_Zeichnung_im_Raster()
    {
        foreach (var sym in Enum.GetValues<Symbol>().Where(x => x != Symbol.Keins))
        {
            Symbolzeichnung.Name(sym).ShouldNotBe(sym.ToString() + "?");
            var striche = Symbolzeichnung.Von(sym);
            striche.ShouldNotBeEmpty(sym.ToString());
            var punkte = striche.SelectMany(s => s switch
            {
                Linienzug l => l.Punkte,
                Vieleck v => v.Punkte,
                Kreis k => [k.X - k.R, k.Y - k.R, k.X + k.R, k.Y + k.R],
                Rechteck r => [r.X, r.Y, r.X + r.B, r.Y + r.H],
                Zeichen z => [z.X, z.Y],
                _ => [],
            });
            punkte.ShouldAllBe(p => p >= 0 && p <= 10, sym.ToString());
        }
    }
}
