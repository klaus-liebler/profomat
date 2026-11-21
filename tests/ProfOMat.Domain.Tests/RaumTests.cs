using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Klausuren;

namespace ProfOMat.Domain.Tests;

public sealed class RaumTests
{
    // 2 Reihen × 3 Tische mit Lücke: (0,0) (2,0) (4,0) / (0,2) (2,2) (4,2)
    private static readonly IReadOnlyList<(int X, int Y)> Tische = Raum.Tischmatrix(0, 0, 2, 3, 1, 1).ToList();

    private static string Folge(Startecke e, Laufrichtung r, bool schlange)
        => string.Join(" ", Raum.Nummerieren(Tische, e, r, schlange).Select(p => $"{p.X}/{p.Y}"));

    [Fact]
    public void Matrix_mit_Luecken() => Tische.ShouldBe([(0, 0), (2, 0), (4, 0), (0, 2), (2, 2), (4, 2)]);

    [Fact]
    public void Reihenweise_von_oben_links() => Folge(Startecke.ObenLinks, Laufrichtung.Reihen, false).ShouldBe("0/0 2/0 4/0 0/2 2/2 4/2");

    [Fact]
    public void Schlangenlinie_kehrt_jede_zweite_Reihe_um() => Folge(Startecke.ObenLinks, Laufrichtung.Reihen, true).ShouldBe("0/0 2/0 4/0 4/2 2/2 0/2");

    [Fact]
    public void Von_hinten_rechts() => Folge(Startecke.UntenRechts, Laufrichtung.Reihen, false).ShouldBe("4/2 2/2 0/2 4/0 2/0 0/0");

    [Fact]
    public void Spaltenweise_in_Schlangenlinie() => Folge(Startecke.ObenLinks, Laufrichtung.Spalten, true).ShouldBe("0/0 0/2 2/2 2/0 4/0 4/2");

    [Fact]
    public void Reihe_und_Platz_in_der_Reihe()
    {
        var p = Raum.Nummerieren(Tische, Startecke.ObenLinks, Laufrichtung.Reihen, true);
        p[3].ShouldBe(new Sitzplatz(4, 2, 1, 4, 2));
    }

    [Fact]
    public void Streuen_laesst_gleichmaessige_Luecken()
    {
        var plaetze = Enumerable.Range(1, 10).Select(n => new Sitzplatz(n, 0, 0, -1, -1)).ToList();
        Raum.Streuen(plaetze, 5).Select(p => p.Nummer).ShouldBe([2, 4, 6, 8, 10]);
        Raum.Streuen(plaetze, 10).Select(p => p.Nummer).ShouldBe(Enumerable.Range(1, 10));
        Raum.Streuen(plaetze, 1).Single().Nummer.ShouldBe(6);
        Should.Throw<FachlicherFehler>(() => Raum.Streuen(plaetze, 11));
    }

    [Fact]
    public void Grundriss_setzt_Platzzahl_und_prueft_Ueberlappung()
    {
        var raum = new Raum("X 1", null, 99);
        raum.GrundrissSetzen(new Raumplan(10, 10, [.. Tische.Select(t => (Raumelementart.Tisch, t.X, t.Y, 1, 1)), (Raumelementart.Tafel, 0, 9, 5, 1)],
            Startecke.ObenLinks, Laufrichtung.Reihen, false));
        raum.Plaetze.ShouldBe(6);
        raum.Sitzplaetze().Count.ShouldBe(6);

        Should.Throw<FachlicherFehler>(() => raum.GrundrissSetzen(new Raumplan(10, 10,
            [(Raumelementart.Tisch, 1, 1, 1, 1), (Raumelementart.Pult, 0, 1, 3, 1)], Startecke.ObenLinks, Laufrichtung.Reihen, false))).Message.ShouldContain("übereinander");
        Should.Throw<FachlicherFehler>(() => raum.GrundrissSetzen(new Raumplan(10, 10,
            [(Raumelementart.Tafel, 8, 0, 5, 1)], Startecke.ObenLinks, Laufrichtung.Reihen, false))).Message.ShouldContain("außerhalb");
        // Wandelemente dürfen sich überlagern (Projektion vor der Tafel)
        raum.GrundrissSetzen(new Raumplan(10, 10, [(Raumelementart.Tafel, 2, 0, 6, 1), (Raumelementart.Projektion, 3, 0, 4, 1), (Raumelementart.Tisch, 2, 3, 1, 1)],
            Startecke.ObenLinks, Laufrichtung.Reihen, false));
        raum.Plaetze.ShouldBe(1);
    }
}
