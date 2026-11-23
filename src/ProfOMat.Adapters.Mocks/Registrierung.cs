using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application;
using ProfOMat.Application.Katalog;
using ProfOMat.Domain.Katalog;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Adapters.Mocks;

public static class Registrierung
{
    public static IServiceCollection AddProfOMatMocks(this IServiceCollection dienste)
    {
        dienste.AddSingleton<IHochschulKatalog, MockHochschulKatalog>();
        dienste.AddScoped<Demodaten>();
        return dienste;
    }
}

/// <summary>
/// Demodaten für die Entwicklung: erfundene Konten (Testanmeldung ohne Passwort), Katalog aus dem Mock-Hochschulsystem
/// und eine Prüfungsordnung, die es im Hochschulsystem nicht mehr gibt (zeigt „alt“).
/// </summary>
public sealed class Demodaten(IDaten daten, KatalogDienst katalog)
{
    public async Task<bool> AnlegenAsync(CancellationToken abbruch = default)
    {
        if (await daten.AnzahlAsync(daten.Menge<Konto>(), abbruch) > 0)
        {
            return false;
        }

        Konto K(string kennung, string? titel, string vorname, string nachname, params Rolle[] rollen)
        {
            var k = new Konto(kennung, titel, vorname, nachname, $"{kennung}@demo.invalid");
            foreach (var r in rollen)
            {
                k.RolleGeben(r);
            }

            daten.Hinzufuegen(k);
            return k;
        }

        K("verwaltung", null, "Vera", "Verwaltung", Rolle.Systemverwaltung);
        K("mustermann", "Prof. Dr.", "Erika", "Mustermann", Rolle.Pruefende);
        K("liebler", "Prof. Dr.", "Klaus", "Liebler", Rolle.Pruefende);
        K("niemeyer", "Prof. Dr.", "Philip", "Niemeyer", Rolle.Pruefende);
        var dekan = K("beispiel", "Prof. Dr.-Ing.", "Max", "Beispiel", Rolle.Pruefende, Rolle.Studiendekanat);
        K("probst", "Prof. Dr.", "Lena", "Probst", Rolle.Pruefende);
        K("tester", "Prof. Dr.-Ing.", "Jonas", "Tester", Rolle.Pruefende);
        var wisoDekanin = K("rechner", "Prof. Dr.", "Paula", "Rechner", Rolle.Pruefende, Rolle.Studiendekanat);
        K("sekretariat", null, "Sabine", "Sekretariat", Rolle.Studierendensekretariat);
        await daten.SpeichernAsync(abbruch);

        await katalog.AbgleichenAsync(alsAutomatik: true, abbruch);

        var fakultaeten = await daten.ListeAsync(daten.Menge<Fakultaet>(), abbruch);
        foreach (var f in fakultaeten)
        {
            f.Aendern(f.Name, f.Kuerzel == "IuI" ? dekan.Id : wisoDekanin.Id, null);
        }

        // Ältere Prüfungsordnung, die das Hochschulsystem nicht mehr liefert → nach dem nächsten Abgleich „alt“
        var iui = fakultaeten.First(f => f.Kuerzel == "IuI");
        var et2017 = new Studiengang(iui.Id, "stg:ET-BSC:PO2017", "ET", "Elektrotechnik", "B.Sc.", "PO 2017", Herkunft.Hochschulsystem);
        daten.Hinzufuegen(et2017);
        daten.Hinzufuegen(new Pruefung(et2017.Id, "pnr:1101", "1101", "Mathematik 1", "Klausur 2 h", Herkunft.Hochschulsystem));
        daten.Hinzufuegen(new Pruefung(et2017.Id, "pnr:1110", "1110", "Grundlagen der Elektrotechnik 1", "Klausur 2 h", Herkunft.Hochschulsystem));
        await daten.SpeichernAsync(abbruch);
        await katalog.AbgleichenAsync(alsAutomatik: true, abbruch);

        // Klausurräume (erfundene Platzzahlen)
        daten.Hinzufuegen(new ProfOMat.Domain.Klausuren.Raum("AA 0101", "Hörsaal, Campus Westerberg", 96));
        var si0012 = new ProfOMat.Domain.Klausuren.Raum("SI 0012", "Seminarraum", 40);
        si0012.GrundrissSetzen(Si0012Grundriss());
        daten.Hinzufuegen(si0012);
        daten.Hinzufuegen(new ProfOMat.Domain.Klausuren.Raum("SI 0110", "Seminarraum", 32));
        daten.Hinzufuegen(new ProfOMat.Domain.Klausuren.Raum("CN 0010", "Hörsaal, Caprivistraße", 60));
        await daten.SpeichernAsync(abbruch);
        return true;
    }

    /// <summary>Beispielgrundriss: Tafel und Projektion vorn, Fenster links, Tür hinten rechts, 5 × 9 Tische mit Abstand, 5 davon entfernt → 40.</summary>
    public static ProfOMat.Domain.Klausuren.Raumplan Si0012Grundriss()
    {
        var elemente = new List<(ProfOMat.Domain.Klausuren.Raumelementart, int, int, int, int)>
        {
            (ProfOMat.Domain.Klausuren.Raumelementart.Tafel, 6, 0, 8, 1),
            (ProfOMat.Domain.Klausuren.Raumelementart.Projektion, 14, 0, 4, 1),
            (ProfOMat.Domain.Klausuren.Raumelementart.Pult, 9, 2, 3, 1),
            (ProfOMat.Domain.Klausuren.Raumelementart.Fenster, 0, 3, 1, 3),
            (ProfOMat.Domain.Klausuren.Raumelementart.Fenster, 0, 8, 1, 3),
            (ProfOMat.Domain.Klausuren.Raumelementart.Tuer, 17, 13, 2, 1),
        };
        foreach (var (x, y) in ProfOMat.Domain.Klausuren.Raum.Tischmatrix(2, 4, 5, 9, 1, 1).Where(p => !(p.Y == 12 && p.X >= 10)))
        {
            elemente.Add((ProfOMat.Domain.Klausuren.Raumelementart.Tisch, x, y, 1, 1));
        }

        return new ProfOMat.Domain.Klausuren.Raumplan(20, 14, elemente, ProfOMat.Domain.Klausuren.Startecke.ObenLinks,
            ProfOMat.Domain.Klausuren.Laufrichtung.Reihen, true);
    }
}
