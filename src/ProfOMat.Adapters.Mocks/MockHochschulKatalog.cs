using ProfOMat.Application;

namespace ProfOMat.Adapters.Mocks;

/// <summary>
/// Stand-in für das Hochschulsystem (HISinOne o. Ä.): liefert Fakultäten, Studiengänge und Prüfungen. Die Daten sind
/// erfunden, aber im Aufbau realistisch (Schlüssel aus Studiengang/PO bzw. Prüfungsnummer). Siehe docs/spezifikation/architektur.md
/// für die echte Anbindung.
/// </summary>
public sealed class MockHochschulKatalog : IHochschulKatalog
{
    public string Quelle => "Hochschulsystem (Mock)";

    public Task<KatalogStand> LadenAsync(CancellationToken abbruch = default)
    {
        static PruefungQuelle P(string nr, string titel, string form = "Klausur 2 h") => new($"pnr:{nr}", nr, titel, form);

        var et = new StudiengangQuelle("stg:ET-BSC:PO2023", "ET", "Elektrotechnik", "B.Sc.", "PO 2023",
        [
            P("11001", "Mathematik 1"), P("11002", "Mathematik 2"), P("11010", "Grundlagen der Elektrotechnik 1"),
            P("11011", "Grundlagen der Elektrotechnik 2"), P("11020", "Digitaltechnik"), P("11030", "Physik"),
            P("12040", "Regelungstechnik"), P("12050", "Mikrocomputertechnik", "Klausur 90 min"), P("12060", "Elektrische Messtechnik"),
        ]);
        var ti = new StudiengangQuelle("stg:TI-BSC:PO2023", "TI", "Informatik – Technische Informatik", "B.Sc.", "PO 2023",
        [
            P("21001", "Mathematik 1"), P("21002", "Mathematik 2"), P("21010", "Programmieren 1"), P("21011", "Programmieren 2"),
            P("21020", "Algorithmen und Datenstrukturen"), P("21030", "Rechnerarchitektur"), P("22040", "Betriebssysteme"),
            P("22050", "Eingebettete Systeme", "Klausur 90 min"),
        ]);
        var mt = new StudiengangQuelle("stg:MT-BSC:PO2021", "MT", "Mechatronik", "B.Sc.", "PO 2021",
        [
            P("31001", "Mathematik 1"), P("31002", "Technische Mechanik 1"), P("31003", "Technische Mechanik 2"),
            P("31010", "Grundlagen der Elektrotechnik"), P("32020", "Regelungstechnik"), P("32030", "Aktorik und Sensorik"),
        ]);
        var esm = new StudiengangQuelle("stg:ESM-MSC:PO2022", "ESM", "Elektrotechnik und Systemtechnik", "M.Sc.", "PO 2022",
        [
            P("41010", "Höhere Regelungstechnik"), P("41020", "Leistungselektronik"), P("41030", "Digitale Signalverarbeitung"),
        ]);
        var bwl = new StudiengangQuelle("stg:BWL-BA:PO2020", "BWL", "Betriebswirtschaft und Management", "B.A.", "PO 2020",
        [
            P("51001", "Wirtschaftsmathematik"), P("51002", "Statistik"), P("51010", "Kosten- und Leistungsrechnung"),
        ]);

        // Maschinenbau: Studiengänge wie auf hs-osnabrueck.de („Maschinenbau studieren“, Studiengänge A–Z, Stand 11/2026);
        // Prüfungsordnungen und Prüfungen sind erfunden. Jede Prüfungsnummer nur einmal (eigener Nummernkreis je Studiengang).
        static StudiengangQuelle M(string kuerzel, string name, string abschluss, string po, int nummernkreis, params string[] pruefungen)
            => new($"stg:{kuerzel}:{po.Replace(" ", "", StringComparison.Ordinal)}", kuerzel, name, abschluss, po,
                pruefungen.Select((t, i) => P((nummernkreis + i).ToString(System.Globalization.CultureInfo.InvariantCulture), t)).ToList());

        string[] mbGrundlagen = ["Mathematik 1", "Mathematik 2", "Technische Mechanik 1", "Technische Mechanik 2", "Werkstofftechnik",
            "Konstruktionslehre 1", "Thermodynamik", "Strömungslehre", "Fertigungstechnik", "Regelungstechnik"];
        var maschinenbau =
            new[]
            {
                M("MB-BSC", "Maschinenbau", "B.Sc.", "PO 2023", 61001, [.. mbGrundlagen, "Maschinenelemente 1", "Maschinenelemente 2"]),
                M("MBP-BSC", "Maschinenbau im Praxisverbund", "B.Sc.", "PO 2023", 61101, [.. mbGrundlagen, "Maschinenelemente 1"]),
                M("IWMB-BENG", "Ingenieurwesen – Maschinenbau (berufsbegleitend)", "B.Eng.", "PO 2022", 61201,
                    "Mathematik 1", "Technische Mechanik 1", "Werkstofftechnik", "Konstruktionslehre 1", "Fertigungstechnik"),
                M("FZT-BSC", "Fahrzeugtechnik", "B.Sc.", "PO 2023", 61301,
                    [.. mbGrundlagen[..6], "Fahrzeugtechnik 1", "Fahrzeugdynamik", "Verbrennungsmotoren", "Elektrische Antriebe im Fahrzeug"]),
                M("AFE-BSC", "Aircraft and Flight Engineering", "B.Sc.", "PO 2023", 61401,
                    [.. mbGrundlagen[..6], "Aerodynamik", "Flugmechanik", "Flugzeugbau", "Luftfahrtantriebe"]),
                M("BBFZT-BSC", "Berufliche Bildung – Teilstudiengang Fahrzeugtechnik", "B.Sc.", "PO 2021", 61501,
                    "Mathematik 1", "Technische Mechanik 1", "Fahrzeugtechnik 1", "Kraftfahrzeugelektrik"),
                M("BBMT-BSC", "Berufliche Bildung – Teilstudiengang Metalltechnik", "B.Sc.", "PO 2021", 61601,
                    "Mathematik 1", "Technische Mechanik 1", "Werkstofftechnik", "Fertigungstechnik"),
                M("MB-MSC", "Maschinenbau", "M.Sc.", "PO 2022", 62001,
                    "Höhere Technische Mechanik", "Finite-Elemente-Methode", "Höhere Regelungstechnik", "Produktentwicklung", "Numerische Strömungsmechanik"),
                M("FZT-MSC", "Fahrzeugtechnik", "M.Sc.", "PO 2022", 62101,
                    "Fahrzeugsystemtechnik", "Alternative Antriebe", "Fahrerassistenzsysteme", "Fahrzeugakustik"),
                M("LBSFZT-MED", "Lehramt an berufsbildenden Schulen – Teilstudiengang Fahrzeugtechnik", "M.Ed.", "PO 2021", 62201,
                    "Fahrzeugtechnik 2", "Fachdidaktik Fahrzeugtechnik"),
                M("LBSMT-MED", "Lehramt an berufsbildenden Schulen – Teilstudiengang Metalltechnik", "M.Ed.", "PO 2021", 62301,
                    "Fertigungstechnik 2", "Fachdidaktik Metalltechnik"),
            };
        var lingen =
            new[]
            {
                M("AMB-BSC", "Allgemeiner Maschinenbau (Standort Lingen)", "B.Sc.", "PO 2023", 71001,
                    "Mathematik 1", "Technische Mechanik 1", "Werkstofftechnik", "Konstruktionslehre 1", "Thermodynamik", "Fertigungstechnik"),
                M("ETSMB-BENG", "ETS – Maschinenbau (dual, Standort Lingen)", "B.Eng.", "PO 2023", 71101,
                    "Mathematik 1", "Technische Mechanik 1", "Werkstofftechnik", "Konstruktionslehre 1", "Fertigungstechnik"),
            };

        return Task.FromResult(new KatalogStand(
        [
            new FakultaetQuelle("IuI", "Fakultät Ingenieurwissenschaften und Informatik", [et, ti, mt, esm, .. maschinenbau]),
            new FakultaetQuelle("WiSo", "Fakultät Wirtschafts- und Sozialwissenschaften", [bwl]),
            new FakultaetQuelle("MKT", "Fakultät Management, Kultur und Technik (Campus Lingen)", lingen),
        ]));
    }
}
