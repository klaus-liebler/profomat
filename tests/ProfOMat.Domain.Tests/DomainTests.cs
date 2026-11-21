using ProfOMat.Domain.Drittpruefung;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Katalog;

namespace ProfOMat.Domain.Tests;

public sealed class AntragsangabenTests
{
    private static Antragsangaben Gueltig() => new("Mia", "Muster", "1234567", "mia@hs-osnabrueck.de", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null);

    [Fact]
    public void Gueltige_Angaben_haben_keine_Beanstandung() => Gueltig().Beanstandung().ShouldBeNull();

    [Theory]
    [InlineData("12a45")]
    [InlineData("1234")]
    [InlineData("12345678901")]
    [InlineData("")]
    public void Matrikelnummer_nur_Ziffern_5_bis_10(string nr) => (Gueltig() with { Matrikelnummer = nr }).Beanstandung().ShouldNotBeNull();

    [Fact]
    public void Freitext_begrenzt() => (Gueltig() with { Terminwuensche = new string('x', 2001) }).Beanstandung()!.ShouldContain("2000");
}

public sealed class FestlegungTests
{
    private static readonly DateTimeOffset Jetzt = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Termin_in_der_Vergangenheit_wird_abgelehnt()
        => new Festlegung(Jetzt.AddHours(-1), "SI 1", null, "Prof. X", null, null).Beanstandung(Guid.NewGuid(), Jetzt)!.ShouldContain("Zukunft");

    [Fact]
    public void Erst_und_Zweitpruefende_verschieden()
    {
        var id = Guid.NewGuid();
        new Festlegung(Jetzt.AddDays(3), "SI 1", id, "Prof. X", null, null).Beanstandung(id, Jetzt)!.ShouldContain("verschiedene");
    }

    [Fact]
    public void Zweitpruefende_Pflicht()
        => new Festlegung(Jetzt.AddDays(3), "SI 1", null, " ", null, null).Beanstandung(Guid.NewGuid(), Jetzt)!.ShouldContain("§ 16");
}

public sealed class KatalogeintragTests
{
    private static readonly DateTimeOffset Jetzt = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Abgleich_holt_automatisch_alte_Eintraege_zurueck_aber_nicht_von_Hand_archivierte()
    {
        var auto = new Pruefung(Guid.NewGuid(), "pnr:1", "1", "Mathe", null, Herkunft.Hochschulsystem);
        auto.AlsAltMarkieren(Jetzt, "nicht mehr da", vonHand: false);
        KatalogAbgleichHilfe.Gesehen(auto, Jetzt.AddDays(1)).ShouldBeTrue();
        auto.IstAlt.ShouldBeFalse();

        var hand = new Pruefung(Guid.NewGuid(), "pnr:2", "2", "Physik", null, Herkunft.Hochschulsystem);
        hand.AlsAltMarkieren(Jetzt, "PO-Wechsel", vonHand: true);
        KatalogAbgleichHilfe.Gesehen(hand, Jetzt.AddDays(1)).ShouldBeFalse();
        hand.IstAlt.ShouldBeTrue();
        hand.ZuletztAbgeglichen.ShouldBe(Jetzt.AddDays(1));
    }

    [Fact]
    public void Uebernehmen_meldet_nur_echte_Aenderungen()
    {
        var p = new Pruefung(Guid.NewGuid(), "pnr:1", "1", "Mathe", "Klausur", Herkunft.Hochschulsystem);
        p.Uebernehmen("1", "Mathe", "Klausur").ShouldBeFalse();
        p.Uebernehmen("1", "Mathematik 1", "Klausur").ShouldBeTrue();
    }
}

public sealed class FreigabeTests
{
    [Fact]
    public void Sicherstellen_wirft_mit_Begruendung()
        => Should.Throw<FachlicherFehler>(() => Freigabe.Nein("Weil.").Sicherstellen()).Message.ShouldBe("Weil.");

    [Fact]
    public void Und_liefert_die_erste_Begruendung()
        => Freigabe.Ja.Und(() => Freigabe.Nein("A")).Und(() => Freigabe.Nein("B")).Begruendung.ShouldBe("A");
}
