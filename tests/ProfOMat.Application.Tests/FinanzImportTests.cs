using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Finanzen;
using ProfOMat.Domain.Finanzen;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Application.Tests;

/// <summary>Stände der Maßnahmen: SQM-Excel (FA-704) und Investitionen per KI-JSON (FA-703). Uhr: 20.11.2026; Daten erfunden.</summary>
public sealed class FinanzImportTests
{
    private static Task<T> Mit<T>(TestUmgebung u, Func<FinanzDienst, Task<T>> arbeit)
        => u.InBereichAsync(d => arbeit(d.GetRequiredService<FinanzDienst>()));

    /// <summary>SQM-Liste wie die der Fakultät: Deckblatt ohne Anträge, Blatt je Laborbereich mit Kopfzeile in Zeile 6.</summary>
    private static byte[] SqmListe()
    {
        using var mappe = new XLWorkbook();
        mappe.Worksheets.Add("Deckblatt").Cell(1, 1).Value = "Studienqualitätsmittel 2026";
        var b = mappe.Worksheets.Add("Automatisierungtechnik");
        string[] kopf = ["Antrag-Nr. ", "Antragssteller / Verantwortliche *", "Hochschul-E-Mail-Adresse *", "Fakultät", "Bezeichnung der Maßnahme *", "Begründung",
            "Laufzeit *", "Personalkosten *", "Sachkosten *", "Bei Geräten", "Gesamtsumme *", "Kategorie *", "Summe", "Verausgabt", "Restmittel"];
        for (var i = 0; i < kopf.Length; i++)
        {
            b.Cell(6, i + 1).Value = kopf[i];
        }

        void Zeile(int r, string nr, string wer, string bez, double personal, double sach, string verausgabt)
        {
            b.Cell(r, 1).Value = nr;
            b.Cell(r, 2).Value = wer;
            b.Cell(r, 5).Value = bez;
            b.Cell(r, 7).Value = "SoSe26+WiSe26/27";
            b.Cell(r, 8).Value = personal;
            b.Cell(r, 9).Value = sach;
            b.Cell(r, 11).Value = personal + sach;
            b.Cell(r, 12).Value = "4 Verbesserung der Laborausstattung";
            b.Cell(r, 14).Value = verausgabt;
        }

        Zeile(7, "SQM-2026-13", "Andere", "Demonstratoren", 8700, 0, "8700");
        Zeile(8, "SQM-2026-14", "Niemeyer", "Aufbauten und Material", 0, 9300, "6909,18");
        Zeile(9, "SQM-2026-15", "Liebler", "Hiwis für Aufbauten", 5000, 0, "3586");
        Zeile(10, "SQM-2026-17a", "Dritte", "Grafikkarten", 0, 2500, "");
        using var ziel = new MemoryStream();
        mappe.SaveAs(ziel);
        return ziel.ToArray();
    }

    private static async Task<Guid> TeamAsync(TestUmgebung u)
    {
        await u.AlsAsync("liebler");
        var team = (await Mit(u, d => d.TeamAnlegenAsync("Labor"))).Id;
        var niemeyer = (await u.KontoAsync("niemeyer")).Id;
        await Mit(u, async d =>
        {
            await d.AufnehmenAsync(team, niemeyer);
            return true;
        });
        return team;
    }

    [Fact]
    public async Task SQM_Liste_schlaegt_nur_Antraege_des_Teams_vor_und_aktualisiert_den_Stand()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var team = await TeamAsync(u);
        var sqm15 = await Mit(u, d => d.SqmAnlegenAsync(team, "SQM-2026-15", "Hiwis", 4000m, 0m));

        var v = await Mit(u, d => d.SqmVorschauAsync(team, SqmListe(), new DateOnly(2026, 9, 22)));
        v.Uebrige.ShouldBe(2);   // Anträge anderer Personen: nur gezählt
        v.Massnahmen.Select(m => (m.Antragsnummer, m.TopfId is not null)).ShouldBe([("SQM-2026-14", false), ("SQM-2026-15", true)]);
        var m15 = v.Massnahmen.Single(m => m.Antragsnummer == "SQM-2026-15");
        (m15.Bewilligt, m15.BewilligtBisher, m15.Ausgegeben).ShouldBe((5000m, (decimal?)4000m, (decimal?)3586m));
        m15.Hinweise.ShouldContain(h => h.Contains("bewilligt ändert sich", StringComparison.Ordinal));

        // Ohne Auswahl wird SQM-2026-14 nicht angelegt; SQM-2026-15 bekommt Bewilligung und Stand
        (await Mit(u, d => d.SqmUebernehmenAsync(team, SqmListe(), new DateOnly(2026, 9, 22), []))).ShouldBe(1);
        var c = await Mit(u, d => d.CockpitAsync(team));
        var z = c.Toepfe.Single().Zahlen;
        (c.Toepfe.Single().Topf.Bewilligt, z.Ausgegeben, z.Frei, z.Datenstand).ShouldBe((5000m, 3586m, 1414m, (DateOnly?)new DateOnly(2026, 9, 22)));

        // Mit Auswahl angelegt (Sachmittel, Verantwortlich aus der Liste); erneute Übernahme legt keinen doppelten Stand an
        (await Mit(u, d => d.SqmUebernehmenAsync(team, SqmListe(), new DateOnly(2026, 9, 22), ["SQM-2026-14"]))).ShouldBe(2);
        var toepfe = await Mit(u, d => d.ToepfeAsync(team));
        var t14 = toepfe.Single(t => t.Kennung == "SQM-2026-14");
        (t14.BewilligtSach, t14.Verantwortlich, t14.LaufzeitBis).ShouldBe((9300m, "Niemeyer", (DateOnly?)new DateOnly(2027, 1, 31)));
        (await Mit(u, d => d.StaendeAsync(team, sqm15.Id))).Count.ShouldBe(1);

        // Stichtag in der Zukunft wird auf heute begrenzt
        (await Mit(u, d => d.SqmVorschauAsync(team, SqmListe(), new DateOnly(2027, 1, 1)))).Stichtag.ShouldBe(new DateOnly(2026, 11, 20));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.SqmVorschauAsync(team, [1, 2, 3])));

        await u.AlsAsync("mustermann");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.SqmVorschauAsync(team, SqmListe())));
    }

    private const string Json = """
        Hier ist das JSON:
        ```json
        {
          "format": "profomat-investitionen-v1",
          "stichtag": "2026-10-27",
          "massnahmen": [
            { "antragsnummer": "AUT-02-26", "laborbereich": "Automatisierungstechnik", "budget": 2400.00, "ausgegeben": 2384.19, "rest": 15.81,
              "buchungen": [ { "datum": "2026-06-17", "kostenart": "930100", "betrag": 2384.19, "text": "Co/Zu./ Grafikkarten" } ] },
            { "antragsnummer": "AUT-07-26", "laborbereich": "Automatisierungstechnik", "budget": 3200, "ausgegeben": 0, "rest": 3200, "buchungen": [] },
            { "antragsnummer": "aut-05-26", "budget": 13000, "ausgegeben": 0, "rest": 12000 }
          ]
        }
        ```
        """;

    [Fact]
    public async Task Investitionsstand_per_KI_JSON_wird_geprueft_und_uebernommen()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var team = await TeamAsync(u);
        var aut07 = await Mit(u, d => d.InvestitionAnlegenAsync(team, "AUT-07-26", "Embedded Systems", 3000m, "71211637"));

        InvestitionenJson.Prompt("Automatisierungstechnik").ShouldContain("nur der Laborbereich „Automatisierungstechnik“");
        var v = await Mit(u, d => d.InvestitionenVorschauAsync(team, Json));
        v.Stichtag.ShouldBe(new DateOnly(2026, 10, 27));
        v.Massnahmen.Select(m => (m.Antragsnummer, m.TopfId == aut07.Id)).ShouldBe([("AUT-02-26", false), ("AUT-07-26", true), ("AUT-05-26", false)]);
        v.Massnahmen[1].Hinweise.ShouldContain(h => h.Contains("3.000,00 € → 3.200,00 €", StringComparison.Ordinal));
        v.Massnahmen[2].Hinweise.ShouldContain(h => h.Contains("≠ rest", StringComparison.Ordinal));   // Summen passen nicht: gezeigt, nicht korrigiert

        (await Mit(u, d => d.InvestitionenUebernehmenAsync(team, Json, ["AUT-02-26"]))).ShouldBe(2);
        var c = await Mit(u, d => d.CockpitAsync(team));
        c.Toepfe.Select(a => (a.Topf.Kennung, a.Topf.Bewilligt, a.Zahlen.Frei)).ShouldBe([("AUT-02-26", 2400m, 15.81m), ("AUT-07-26", 3200m, 3200m)], ignoreOrder: true);
        c.Toepfe.Single(a => a.Topf.Kennung == "AUT-02-26").Topf.Bestellschluss.ShouldBe(new DateOnly(2027, 8, 31));

        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.InvestitionenVorschauAsync(team, "{\"format\":\"anders\"}")))).Message.ShouldContain("Format");
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.InvestitionenVorschauAsync(team, "kein json")))).Message.ShouldContain("kein JSON");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.InvestitionenVorschauAsync(team,
            "{\"format\":\"profomat-investitionen-v1\",\"stichtag\":\"2026-10-27\",\"massnahmen\":[{\"antragsnummer\":\"x\",\"budget\":1,\"ausgegeben\":0,\"rest\":1}]}")));
    }
}
