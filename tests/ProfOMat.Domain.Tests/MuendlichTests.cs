using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Muendlich;

namespace ProfOMat.Domain.Tests;

public sealed class NotenstandTests
{
    [Theory]
    [InlineData("0", "10", "5.0")]
    [InlineData("4.9", "10", "5.0")]     // 49 % → nicht bestanden
    [InlineData("4.95", "10", "4.0")]    // genau 49,5 %
    [InlineData("5.45", "10", "3.7")]
    [InlineData("29", "40", "2.7")]      // 72,5 %
    [InlineData("37.75", "40", "1.3")]   // 94,375 %
    [InlineData("37.8", "40", "1.0")]    // 94,5 %
    [InlineData("40", "40", "1.0")]
    public void Standardschluessel_in_Prozent(string erreicht, string moeglich, string note)
        => Notenstand.Berechnen(D(erreicht), D(moeglich)).Note.ShouldBe(D(note));

    private static decimal D(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Kleine_Summen_haben_keine_zusammenfallenden_Grenzen()
    {
        // Nach der ersten Frage mit 5 Punkten: 2,5 → 50 % → 4,0; 2,75 → 55 % → 3,7 (bei Rundung auf halbe Punkte fielen beide auf 2,5).
        Notenstand.Berechnen(2.5m, 5m).Note.ShouldBe(4.0m);
        Notenstand.Berechnen(2.75m, 5m).Note.ShouldBe(3.7m);
    }

    [Fact]
    public void Abstand_zur_naechsten_Note()
    {
        var n = Notenstand.Berechnen(15m, 20m);   // 75 % → 2,3; 2,0 ab 79,5 % = 15,9 Punkte
        n.Note.ShouldBe(2.3m);
        n.Prozent.ShouldBe(75m);
        n.NaechsteNote.ShouldBe(2.0m);
        n.FehlendePunkte.ShouldBe(0.9m);
        Notenstand.Berechnen(20m, 20m).NaechsteNote.ShouldBeNull();
    }

    [Fact]
    public void Ohne_Bewertung_keine_Note() => Notenstand.Berechnen(0m, 0m).Note.ShouldBeNull();
}

public sealed class MuendlichePruefungTests
{
    private static MuendlichePruefung Neu() => new(Ids.Neu(), "Regelungstechnik", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Note_ueber_bewertete_Fragen_offene_und_zurueckgestellte_getrennt()
    {
        var p = Neu();
        var a = p.FrageHinzufuegen("12", 10m);
        var b = p.FrageHinzufuegen("3b", 10m);
        var c = p.FrageHinzufuegen("7", 5m);
        p.FrageBewerten(a.Id, 9m, false);
        p.FrageBewerten(b.Id, null, zurueckstellen: true);

        b.Stand.ShouldBe(FrageStand.Zurueckgestellt);
        c.Stand.ShouldBe(FrageStand.Offen);
        p.Aktuell.Note.ShouldBe(1.3m);          // 9 von 10
        p.OhneOffene.Moeglich.ShouldBe(25m);
        p.OhneOffene.Note.ShouldBe(5.0m);       // 9 von 25 = 36 %
        p.Unbewertet("3B")!.Id.ShouldBe(b.Id);
        p.Unbewertet("12").ShouldBeNull();      // schon bewertet

        p.FrageBewerten(b.Id, 7m, false);       // nachgeholt: Vermerk bleibt
        b.Stand.ShouldBe(FrageStand.Bewertet);
        b.Zurueckgestellt.ShouldBeTrue();
        p.Aktuell.Erreicht.ShouldBe(16m);
        p.Aktuell.Moeglich.ShouldBe(20m);

        p.FrageBewerten(b.Id, null, false);     // Feld geleert: wieder offen, Vermerk weg
        b.Stand.ShouldBe(FrageStand.Offen);
        b.Zurueckgestellt.ShouldBeFalse();
    }

    [Fact]
    public void Grenzfaelle_werden_abgelehnt()
    {
        var p = Neu();
        Should.Throw<FachlicherFehler>(() => p.FrageHinzufuegen(" ", 5m));
        Should.Throw<FachlicherFehler>(() => p.FrageHinzufuegen("1", 0m));
        Should.Throw<FachlicherFehler>(() => p.FrageHinzufuegen(new string('9', 21), 5m));
        var f = p.FrageHinzufuegen("1", 5m);
        Should.Throw<FachlicherFehler>(() => p.FrageBewerten(f.Id, 5.5m, false)).Message.ShouldContain("zwischen 0 und 5");
        Should.Throw<FachlicherFehler>(() => p.FrageBewerten(f.Id, -1m, false));
        p.FrageBewerten(f.Id, 4m, false);
        Should.Throw<FachlicherFehler>(() => p.FrageAendern(f.Id, "1", 3m)).Message.ShouldContain("4 Punkte");
        Should.Throw<FachlicherFehler>(() => new MuendlichePruefung(Ids.Neu(), "  ", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Reihenfolge_bleibt_beim_Entfernen_erhalten()
    {
        var p = Neu();
        var a = p.FrageHinzufuegen("1", 5m);
        p.FrageHinzufuegen("2", 5m);
        p.FrageEntfernen(a.Id);
        p.FrageHinzufuegen("3", 5m);
        p.FragenSortiert.Select(f => f.Nummer).ShouldBe(["2", "3"]);
    }

    [Theory]
    [InlineData("2,5", "2.5")]
    [InlineData(" 3.25 ", "3.25")]
    [InlineData("10", "10")]
    public void Zahlen_mit_Komma_oder_Punkt(string eingabe, string wert)
        => MuendlichePruefung.ZahlLesen(eingabe).ShouldBe(decimal.Parse(wert, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("")]
    [InlineData("z")]
    [InlineData("1,234")]
    [InlineData("-1")]
    [InlineData("1e3")]
    public void Keine_Zahl(string eingabe) => MuendlichePruefung.ZahlLesen(eingabe).ShouldBeNull();
}

public sealed class MuendlichProtokollTests
{
    private static readonly DateTimeOffset Jetzt = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    private static (MuendlichePruefung P, GestellteFrage F1, GestellteFrage F2) Pruefung()
    {
        var p = new MuendlichePruefung(Guid.NewGuid(), "Regelungstechnik", Jetzt);
        var f1 = p.FrageHinzufuegen("4", 10m);
        var f2 = p.FrageHinzufuegen("7", 10m);
        p.FrageBewerten(f1.Id, 8m, false);
        p.FrageBewerten(f2.Id, null, true);
        p.AngabenSetzen(null, null, null, Guid.NewGuid(), null);
        return (p, f1, f2);
    }

    [Fact]
    public void Vorschlag_zaehlt_offene_Fragen_mit_null_Punkten()
    {
        var (p, _, _) = Pruefung();
        p.Aktuell.Note.ShouldBe(2.0m);        // 8 von 10
        p.Notenvorschlag.ShouldBe(5.0m);      // 8 von 20 = 40 %
    }

    [Fact]
    public void Vorschlag_uebernehmen_oder_anpassen()
    {
        var (p, _, f2) = Pruefung();
        p.FrageBewerten(f2.Id, 7m, false);    // 15 von 20 = 75 % → 2,3
        p.NoteFestlegen(2.3m, "wird ignoriert", Jetzt);
        (p.Endnote, p.WeichtAb, p.Abweichungsbegruendung).ShouldBe((2.3m, false, null));

        p.NoteFestlegen(2.0m, "  sichere Herleitung der Übertragungsfunktion  ", Jetzt);
        (p.Endnote, p.WeichtAb, p.Abweichungsbegruendung).ShouldBe((2.0m, true, "sichere Herleitung der Übertragungsfunktion"));
    }

    [Theory]
    [InlineData("2.5")]
    [InlineData("0.7")]
    [InlineData("4.3")]
    public void Nur_zulaessige_Noten(string note)
    {
        var (p, _, _) = Pruefung();
        Should.Throw<FachlicherFehler>(() => p.NoteFestlegen(decimal.Parse(note, System.Globalization.CultureInfo.InvariantCulture), null, Jetzt));
        p.Endnote.ShouldBeNull();
    }

    [Fact]
    public void Ohne_Fragen_keine_Note()
        => Should.Throw<FachlicherFehler>(() => new MuendlichePruefung(Guid.NewGuid(), "leer", Jetzt).NoteFestlegen(1.0m, null, Jetzt));

    [Fact]
    public void Punktaenderung_hebt_die_Festlegung_auf_Bemerkung_nicht()
    {
        var (p, f1, f2) = Pruefung();
        p.NoteFestlegen(4.0m, null, Jetzt);
        p.FrageBemerken(f1.Id, "  gut begründet ");
        f1.Bemerkung.ShouldBe("gut begründet");
        p.Endnote.ShouldBe(4.0m);

        p.FrageBewerten(f2.Id, null, true);   // unverändert zurückgestellt
        p.Endnote.ShouldBe(4.0m);
        p.FrageAendern(f1.Id, "4a", 10m);     // nur Nummer
        p.Endnote.ShouldBe(4.0m);

        p.FrageBewerten(f2.Id, 6m, false);
        p.Endnote.ShouldBeNull();
        p.Festgelegt.ShouldBeNull();

        p.NoteFestlegen(2.7m, null, Jetzt);
        p.FrageAendern(f1.Id, "4a", 12m);
        p.Endnote.ShouldBeNull();
        p.NoteFestlegen(2.7m, null, Jetzt);
        p.FrageHinzufuegen("9", 5m);
        p.Endnote.ShouldBeNull();
        p.NoteFestlegen(2.7m, null, Jetzt);
        p.FrageEntfernen(f2.Id);
        p.Endnote.ShouldBeNull();
    }

    [Fact]
    public void Bemerkung_leer_wird_entfernt_und_gekuerzt()
    {
        var (p, f1, _) = Pruefung();
        p.FrageBemerken(f1.Id, new string('x', GestellteFrage.BemerkungMaxLaenge + 10));
        f1.Bemerkung!.Length.ShouldBe(GestellteFrage.BemerkungMaxLaenge);
        p.FrageBemerken(f1.Id, "   ");
        f1.Bemerkung.ShouldBeNull();
    }

    [Fact]
    public void Angaben_duerfen_unvollstaendig_sein_aber_nie_zweimal_dieselbe_Person()
    {
        var (p, _, _) = Pruefung();
        var a = Guid.NewGuid();
        p.AngabenSetzen("Mia Muster", "123", "ET", null, a);   // während der Prüfung: Erstprüfung noch offen
        Should.Throw<FachlicherFehler>(() => p.NoteFestlegen(2.0m, null, Jetzt)).Message.ShouldContain("Erstprüf");
        Should.Throw<FachlicherFehler>(() => p.AngabenSetzen("Mia Muster", "123", "ET", a, a));
        p.AngabenSetzen("  Mia Muster ", " ", "Elektrotechnik (B.Sc., PO 2023)", a, null);
        (p.Pruefling, p.Matrikelnummer, p.Studiengang, p.ErstpruefendeId, p.ZweitpruefendeId).ShouldBe(("Mia Muster", null, "Elektrotechnik (B.Sc., PO 2023)", a, null));
    }

    [Fact]
    public void Pruefende_ohne_Konto_mit_Namen()
    {
        var (p, _, _) = Pruefung();
        var konto = Guid.NewGuid();

        // ohne Konto: Name zählt als Erstprüfung
        p.AngabenSetzen(null, null, null, null, null, "  Prof. Dr. Philip Niemeyer ", null);
        (p.ErstpruefendeId, p.ErstpruefendeName, p.HatErstpruefung).ShouldBe(((Guid?)null, "Prof. Dr. Philip Niemeyer", true));
        p.NoteFestlegen(4.0m, null, Jetzt);

        // Konto gewählt: Name entfällt
        p.AngabenSetzen(null, null, null, konto, null, "wird ignoriert", "Dr. Extern");
        (p.ErstpruefendeId, p.ErstpruefendeName, p.ZweitpruefendeName).ShouldBe(((Guid?)konto, (string?)null, "Dr. Extern"));

        // derselbe Name zweimal geht nicht, leer heißt offen
        Should.Throw<FachlicherFehler>(() => p.AngabenSetzen(null, null, null, null, null, "Dr. Extern", "dr. extern"));
        p.AngabenSetzen(null, null, null, null, null, "   ", null);
        p.HatErstpruefung.ShouldBeFalse();
    }
}
