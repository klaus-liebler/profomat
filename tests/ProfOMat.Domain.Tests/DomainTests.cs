using ProfOMat.Domain.Drittpruefung;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Katalog;

namespace ProfOMat.Domain.Tests;

public sealed class EinladungTests
{
    private static readonly DateTimeOffset Jetzt = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Erst = Guid.NewGuid();
    private static readonly Guid Zweit = Guid.NewGuid();

    private static Einladungsangaben Gueltig() => new("Mia", "Muster", Guid.NewGuid(), Guid.NewGuid(), 14);

    private static Antrag Neu(Festlegung? vorbelegung = null)
        => Antrag.Einladen(Guid.NewGuid(), "DP-2026-000001", Gueltig(), Erst, vorbelegung, Jetzt, "hash");

    private static Signatur Sig(Antrag a, Signaturschritt schritt, DateTimeOffset zeit)
        => Signatur.MitHandschrift(a.Id, schritt, zeit, "X", null, $"Text {schritt} {zeit:O}", $"h-{schritt}-{zeit.Ticks}",
            a.LetzteGueltigeSignatur?.InhaltHash, [1], "b");

    [Fact]
    public void Gueltige_Angaben_haben_keine_Beanstandung() => Gueltig().Beanstandung().ShouldBeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Gueltigkeit_1_bis_60_Tage(int tage) => (Gueltig() with { GueltigTage = tage }).Beanstandung()!.ShouldContain("Tage");

    [Theory]
    [InlineData("12a45")]
    [InlineData("1234")]
    [InlineData("12345678901")]
    [InlineData("")]
    public void Matrikelnummer_nur_Ziffern_5_bis_10(string nr) => Antrag.StudierendenBeanstandung(nr, null).ShouldNotBeNull();

    [Fact]
    public void Terminwuensche_begrenzt() => Antrag.StudierendenBeanstandung("1234567", new string('x', 2001))!.ShouldContain("2000");

    [Fact]
    public void Abgelaufener_Link_laesst_sich_nicht_unterschreiben_und_verfaellt()
    {
        var a = Neu();
        a.KannStudierendUnterschreiben(Jetzt.AddDays(13)).Erlaubt.ShouldBeTrue();
        a.KannStudierendUnterschreiben(Jetzt.AddDays(15)).Begruendung!.ShouldContain("abgelaufen");
        a.Verfallen(Jetzt.AddDays(13), Jetzt).ShouldBeFalse();
        a.Verfallen(Jetzt.AddDays(15), Jetzt).ShouldBeTrue();
        a.Status.ShouldBe(AntragStatus.Verfallen);

        // Neuer Link holt die Einladung zurück
        a.LinkErneuern(Erst, "neu", 7, Jetzt.AddDays(15));
        a.Status.ShouldBe(AntragStatus.Eingeladen);
        a.GueltigBis.ShouldBe(Jetzt.AddDays(22));
        a.KannLinkErneuern(Zweit).Erlaubt.ShouldBeFalse();
    }

    [Fact]
    public void Ablauf_bis_extern_weitergeleitet_und_Rueckgabe_ueberholt_Pruefende()
    {
        var a = Neu(new Festlegung(null, null, Zweit, "Prof. Zwei", null, null));
        a.KannErstpruefungUnterschreiben(Erst, Jetzt).Erlaubt.ShouldBeFalse();
        a.StudierendUnterschreiben(" 1234567 ", "  ", Sig(a, Signaturschritt.Antragstellung, Jetzt.AddMinutes(1)), Jetzt.AddMinutes(1));
        a.Matrikelnummer.ShouldBe("1234567");
        a.Terminwuensche.ShouldBeNull();
        a.Status.ShouldBe(AntragStatus.BeimErstpruefer);

        // Termin fehlt noch
        a.KannErstpruefungUnterschreiben(Erst, Jetzt).Begruendung!.ShouldContain("Datum");
        a.FestlegungSetzen(Erst, a.AktuelleFestlegung with { Termin = Jetzt.AddDays(10), Ort = "SI 0012" }, Jetzt);
        a.KannErstpruefungUnterschreiben(Erst, Jetzt).Erlaubt.ShouldBeTrue();
        a.ErstpruefungUnterschreiben(Erst, Sig(a, Signaturschritt.Erstpruefung, Jetzt.AddMinutes(2)), Jetzt);
        a.Status.ShouldBe(AntragStatus.BeimZweitpruefer);
        a.KannFestlegen(Erst).Erlaubt.ShouldBeFalse();
        a.KannZweitpruefungUnterschreiben(Erst).Erlaubt.ShouldBeFalse();

        a.ZweitpruefungUnterschreiben(Zweit, Sig(a, Signaturschritt.Zweitpruefung, Jetzt.AddMinutes(3)), imSystem: false, null);
        a.Status.ShouldBe(AntragStatus.ExternWeiterleiten);
        a.KannGenehmigen(Guid.NewGuid(), true).Erlaubt.ShouldBeFalse();

        a.Zurueckgeben(Zweit, false, "Prof. Zwei", "Dekan möchte anderen Raum", Jetzt.AddMinutes(4));
        a.Status.ShouldBe(AntragStatus.BeimErstpruefer);
        a.GueltigeSignaturen.Select(s => s.Schritt).ShouldBe([Signaturschritt.Antragstellung]);
        a.Rueckfrage!.ShouldContain("anderen Raum");

        a.ErstpruefungUnterschreiben(Erst, Sig(a, Signaturschritt.Erstpruefung, Jetzt.AddMinutes(5)), Jetzt);
        a.ZweitpruefungUnterschreiben(Zweit, Sig(a, Signaturschritt.Zweitpruefung, Jetzt.AddMinutes(6)), imSystem: false, null);
        a.KannWeitergeleitetMelden(Guid.NewGuid()).Erlaubt.ShouldBeFalse();
        a.WeitergeleitetMelden(Zweit, Jetzt.AddMinutes(7));
        a.Status.ShouldBe(AntragStatus.Weitergeleitet);
        a.Status.IstOffen().ShouldBeFalse();
    }

    [Fact]
    public void Unterschrift_mit_falschem_Vorgaenger_wird_abgelehnt()
    {
        var a = Neu();
        var falsch = Signatur.MitHandschrift(a.Id, Signaturschritt.Antragstellung, Jetzt, "X", null, "T", "h", "irgendwas", [1], "b");
        Should.Throw<FachlicherFehler>(() => a.StudierendUnterschreiben("1234567", null, falsch, Jetzt)).Message.ShouldContain("geändert");
    }

    [Fact]
    public void Einladung_zuruecknehmen_nur_vor_der_Unterschrift()
    {
        var a = Neu();
        a.KannEinladungZuruecknehmen(Zweit).Erlaubt.ShouldBeFalse();
        a.KannAblehnen(Erst, false).Begruendung!.ShouldContain("zurücknehmen");
        a.EinladungZuruecknehmen(Erst, Jetzt);
        a.Status.ShouldBe(AntragStatus.Zurueckgezogen);
    }
}

public sealed class FestlegungTests
{
    private static readonly DateTimeOffset Jetzt = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Termin_in_der_Vergangenheit_wird_abgelehnt()
        => new Festlegung(Jetzt.AddHours(-1), "SI 1", null, null, null, null).Beanstandung(Guid.NewGuid(), Jetzt, false)!.ShouldContain("Zukunft");

    [Fact]
    public void Erst_und_Zweitpruefende_verschieden()
    {
        var id = Guid.NewGuid();
        new Festlegung(Jetzt.AddDays(3), "SI 1", id, "Prof. X", null, null).Beanstandung(id, Jetzt, false)!.ShouldContain("verschiedene");
    }

    [Fact]
    public void Vorbelegung_darf_unvollstaendig_sein_Unterschrift_nicht()
    {
        var f = new Festlegung(null, null, null, null, null, null);
        f.Beanstandung(Guid.NewGuid(), Jetzt, false).ShouldBeNull();
        f.Beanstandung(Guid.NewGuid(), Jetzt, true)!.ShouldContain("Datum");
        (f with { Termin = Jetzt.AddDays(1), Ort = "SI 1" }).Beanstandung(Guid.NewGuid(), Jetzt, true)!.ShouldContain("§ 16");
    }
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
