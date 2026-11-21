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

        return Task.FromResult(new KatalogStand(
        [
            new FakultaetQuelle("IuI", "Fakultät Ingenieurwissenschaften und Informatik", [et, ti, mt, esm]),
            new FakultaetQuelle("WiSo", "Fakultät Wirtschafts- und Sozialwissenschaften", [bwl]),
        ]));
    }
}
