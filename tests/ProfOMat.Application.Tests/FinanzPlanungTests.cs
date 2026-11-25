using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Finanzen;
using ProfOMat.Domain.Finanzen;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Application.Tests;

/// <summary>Planung (FA-706–709): geplante Ausgaben, Auslagen, Hilfskräfte und ihre Wirkung auf das Cockpit. Uhr: 20.11.2026.</summary>
public sealed class FinanzPlanungTests
{
    private const string Kst = "12103012";

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private static Task<T> Mit<T>(TestUmgebung u, Func<FinanzDienst, Task<T>> arbeit)
        => u.InBereichAsync(d => arbeit(d.GetRequiredService<FinanzDienst>()));

    private static Task Mit(TestUmgebung u, Func<FinanzDienst, Task> arbeit)
        => u.InBereichAsync(d => arbeit(d.GetRequiredService<FinanzDienst>()));

    private static DateOnly D(int j, int m, int t = 1) => new(j, m, t);

    /// <summary>Auszug 2026: Budget 9.866 €, Material 1.234,56 € (12.03.), Hiwi 780 € (15.04.) → Rest 7.851,44 €, Datenstand 15.04.2026.</summary>
    private static byte[] Auszug2026()
    {
        static string B(decimal d) => (d < 0 ? "- " : " ") + Math.Abs(d).ToString("#,##0.00", De);
        (string Datum, string Art, decimal Wert, string Text, string Ref)[] z =
        [
            ("01.01.2026", "930300", -9866m, "Co/Budget 2026", ""),
            ("12.03.2026", "602110", 1234.56m, "Händler/Roboterteile", "1970001"),
            ("15.04.2026", "634000", 780m, "0001234567 Hilfskraft, Erika", "908665"),
        ];
        var sb = new StringBuilder(" Kostenst.\tErfaßt am\tBelegdatum\tBuchDatum\tKostenart\tKostenartenbezeichn.\t Wert/KWähr\tBezeichnung\tRefBelnr\r\n");
        foreach (var (datum, art, wert, text, r) in z)
        {
            sb.Append($" {Kst}\t{datum}\t{datum}\t{datum}\t{art}\tx\t{B(wert)}\t{text}\t{r}\r\n");
        }

        sb.Append($"* \t\t\t\t\t\t{B(z.Sum(x => x.Wert))}\t\t\r\n");
        return Encoding.Unicode.GetBytes(sb.ToString());
    }

    private sealed record Aufbau(Guid Team, Topf Kst, Topf Sqm, Topf Inv);

    /// <summary>Team mit Kostenstelle (Auszug 2026), SQM-2026-15 (5.000 € Personal, Stand 3.586 €) und AUT-07-26 (3.200 €).</summary>
    private static async Task<Aufbau> AufbauAsync(TestUmgebung u)
    {
        await u.AlsAsync("liebler");
        var team = (await Mit(u, d => d.TeamAnlegenAsync("Labor"))).Id;
        await Mit(u, d => d.ImportUebernehmenAsync(team, "20260513 12103012 Tabelle.xls", Auszug2026()));
        var sqm = await Mit(u, d => d.SqmAnlegenAsync(team, "SQM-2026-15", "Hiwis für Aufbauten", 5000m, 0m));
        await Mit(u, d => d.StandErfassenAsync(team, sqm.Id, D(2026, 9, 22), 3586m));
        var inv = await Mit(u, d => d.InvestitionAnlegenAsync(team, "AUT-07-26", "Embedded Systems", 3200m, "71211637"));
        var kst = (await Mit(u, d => d.ToepfeAsync(team))).Single(t => t.Art == Topfart.Kostenstelle);
        return new(team, kst, sqm, inv);
    }

    private static async Task<Topfkennzahlen> ZahlenAsync(TestUmgebung u, Guid team, Guid topf)
        => (await Mit(u, d => d.CockpitAsync(team))).Toepfe.Single(a => a.Topf.Id == topf).Zahlen;

    // ------------------------------------------------------------------ geplante Ausgaben

    [Fact]
    public async Task Geplante_Ausgaben_binden_Mittel_und_zaehlen_fuer_die_Grenze_nur_bis_Jahresende()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var a = await AufbauAsync(u);
        var vorher = await ZahlenAsync(u, a.Team, a.Kst.Id);
        (vorher.Frei, vorher.Prognose31Dez).ShouldBe((7851.44m, 7851.44m));

        var dez = await Mit(u, d => d.AusgabeAnlegenAsync(a.Team, "ABB-Zubehör", D(2026, 12, 10), [new Anteil(a.Kst.Id, 4000m)]));
        await Mit(u, d => d.AusgabeAnlegenAsync(a.Team, "Messgeräte", D(2027, 2), [new Anteil(a.Kst.Id, 1000m), new Anteil(a.Inv.Id, 3000m)]));
        var z = await ZahlenAsync(u, a.Team, a.Kst.Id);
        (z.Gebunden, z.Frei, z.Prognose31Dez).ShouldBe((5000m, 2851.44m, 3851.44m));
        z.UeberGrenze.ShouldBe(3851.44m - 1183.92m);
        (await ZahlenAsync(u, a.Team, a.Inv.Id)).Frei.ShouldBe(200m);

        await Mit(u, d => d.AusgabeStandAsync(a.Team, dez.Id, Ausgabenstand.Bestellt));
        (await ZahlenAsync(u, a.Team, a.Kst.Id)).Gebunden.ShouldBe(5000m);
        await Mit(u, d => d.AusgabeStandAsync(a.Team, dez.Id, Ausgabenstand.Erledigt));
        (await ZahlenAsync(u, a.Team, a.Kst.Id)).Gebunden.ShouldBe(1000m);
    }

    [Fact]
    public async Task Ausgaben_werden_geprueft_und_passende_Buchungen_vorgeschlagen()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var a = await AufbauAsync(u);
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AusgabeAnlegenAsync(a.Team, "x", D(2026, 3), [])));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AusgabeAnlegenAsync(a.Team, "x", D(2026, 3), [new Anteil(Guid.NewGuid(), 5m)])));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AusgabeAnlegenAsync(a.Team, "x", D(2026, 3), [new Anteil(a.Kst.Id, -5m)])));

        var teile = await Mit(u, d => d.AusgabeAnlegenAsync(a.Team, "Roboterteile", D(2026, 3), [new Anteil(a.Kst.Id, 1230m)]));
        var vorschlag = (await Mit(u, d => d.BuchungsvorschlaegeAsync(a.Team, teile.Id))).ShouldHaveSingleItem();
        vorschlag.Betrag.ShouldBe(1234.56m);
        await Mit(u, d => d.AusgabeStandAsync(a.Team, teile.Id, Ausgabenstand.Erledigt, vorschlag.Fingerabdruck));

        // Schon verknüpfte Buchungen werden einer anderen Ausgabe nicht mehr vorgeschlagen
        var zweite = await Mit(u, d => d.AusgabeAnlegenAsync(a.Team, "Roboterteile 2", D(2026, 3), [new Anteil(a.Kst.Id, 1234.56m)]));
        (await Mit(u, d => d.BuchungsvorschlaegeAsync(a.Team, zweite.Id))).ShouldBeEmpty();

        await u.AlsAsync("mustermann");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AusgabenAsync(a.Team)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AusgabeLoeschenAsync(a.Team, zweite.Id)));
    }

    // ------------------------------------------------------------------ Auslagen

    [Fact]
    public async Task Auslagenabrechnung_bindet_bis_zur_Erstattung_prueft_Fristen_und_liefert_die_Aufstellung()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var a = await AufbauAsync(u);
        var ab = await Mit(u, d => d.AuslageAnlegenAsync(a.Team, "Sammelabrechnung November"));
        await Mit(u, d => d.PositionHinzufuegenAsync(a.Team, ab.Id, D(2026, 11, 3), "Händler A", "Schrittmotoren", 89.90m, a.Kst.Id));
        await Mit(u, d => d.PositionHinzufuegenAsync(a.Team, ab.Id, D(2026, 11, 4), "Händler B", "Sensoren", 45.10m, a.Sqm.Id));
        await Mit(u, d => d.PositionHinzufuegenAsync(a.Team, ab.Id, D(2026, 2, 20), "Händler B", "Kabel (vor SQM-Laufzeit)", 12m, a.Sqm.Id));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.PositionHinzufuegenAsync(a.Team, ab.Id, D(2026, 12, 1), "x", "Zukunft", 1m, a.Kst.Id)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.PositionHinzufuegenAsync(a.Team, ab.Id, D(2026, 11, 1), "x", "y", 0m, a.Kst.Id)));

        var ansicht = await Mit(u, d => d.AuslageAsync(a.Team, ab.Id));
        ansicht.Person.ShouldBe("Prof. Dr. Klaus Liebler");
        ansicht.Abrechnung.Summe.ShouldBe(147m);
        ansicht.Hinweise.ShouldHaveSingleItem().Text.ShouldContain("außerhalb der Laufzeit von SQM-2026-15");
        (await ZahlenAsync(u, a.Team, a.Kst.Id)).Gebunden.ShouldBe(89.90m);
        (await ZahlenAsync(u, a.Team, a.Sqm.Id)).Frei.ShouldBe(1414m - 57.10m);

        await Mit(u, d => d.AuslageStandAsync(a.Team, ab.Id, Auslagenstand.Eingereicht));
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.PositionEntfernenAsync(a.Team, ab.Id, ansicht.Abrechnung.Positionen[0].Id)))).Message.ShouldContain("eingereicht");
        (await Mit(u, d => d.AuslageAsync(a.Team, ab.Id))).Abrechnung.Eingereicht.ShouldBe(D(2026, 11, 20));
        (await ZahlenAsync(u, a.Team, a.Kst.Id)).Gebunden.ShouldBe(89.90m);

        var xlsx = await Mit(u, d => d.AuslagenXlsxAsync(a.Team, ab.Id));
        (xlsx.Name, xlsx.Inhalt[0], xlsx.Inhalt[1]).ShouldBe(("Sammelabrechnung-November.xlsx", (byte)'P', (byte)'K'));
        using (var mappe = new ClosedXML.Excel.XLWorkbook(new MemoryStream(xlsx.Inhalt)))
        {
            mappe.Worksheets.Select(w => w.Name).ShouldBe(["Übersicht", Kst, "SQM-2026-15"]);
            Math.Round(mappe.Worksheet("SQM-2026-15").Cell(6, 5).GetValue<decimal>(), 2).ShouldBe(57.10m);   // Excel speichert double
        }

        await Mit(u, d => d.AuslageStandAsync(a.Team, ab.Id, Auslagenstand.Erstattet));
        (await ZahlenAsync(u, a.Team, a.Kst.Id)).Gebunden.ShouldBe(0m);
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AuslageLoeschenAsync(a.Team, ab.Id)));

        // Investition: Kauf nach dem Bestellschluss
        var spaet = new Auslagenabrechnung(a.Team, "x", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        spaet.Hinzufuegen(D(2027, 9, 1), "h", "Oszilloskop", 900m, a.Inv.Id);
        spaet.Pruefen(new Dictionary<Guid, Topf> { [a.Inv.Id] = a.Inv }, D(2027, 9, 2)).ShouldHaveSingleItem().Text.ShouldContain("Bestellschluss");
    }

    // ------------------------------------------------------------------ Hilfskräfte

    [Fact]
    public async Task Hilfskraft_bindet_die_Monate_nach_dem_Ist_Stand()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var a = await AufbauAsync(u);
        // SHK 40 h × 19,50 € = 780 €/Monat; Kostenstelle bis 15.04.2026 gebucht → Mai bis Dezember gebunden
        var h = await Mit(u, d => d.HilfskraftAnlegenAsync(a.Team, "Erika Hilfskraft", Hilfskraftart.StudentischeHilfskraft, 40m, D(2026, 1), D(2026, 12), [(a.Kst.Id, 100m)]));
        h.Monate.ShouldBe(12);
        var z = await ZahlenAsync(u, a.Team, a.Kst.Id);
        z.Gebunden.ShouldBe(8 * 780m);

        // Hälfte aus SQM (Stand bis 22.09. → Oktober bis Dezember gebunden)
        await Mit(u, d => d.HilfskraftAendernAsync(a.Team, h.Id, "Erika Hilfskraft", Hilfskraftart.StudentischeHilfskraft, 40m, D(2026, 1), D(2026, 12), [(a.Kst.Id, 50m), (a.Sqm.Id, 50m)]));
        (await ZahlenAsync(u, a.Team, a.Kst.Id)).Gebunden.ShouldBe(8 * 390m);
        (await ZahlenAsync(u, a.Team, a.Sqm.Id)).Gebunden.ShouldBe(3 * 390m);

        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.HilfskraftAnlegenAsync(a.Team, "x", Hilfskraftart.TutorMaster, 10m, D(2026, 12), D(2027, 1), [(a.Inv.Id, 100m)]))))
            .Message.ShouldContain("Investitionsmaßnahme");
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.HilfskraftAnlegenAsync(a.Team, "x", Hilfskraftart.TutorMaster, 10m, D(2026, 12), D(2027, 1), [(a.Kst.Id, 60m)]))))
            .Message.ShouldContain("100 %");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.HilfskraftAnlegenAsync(a.Team, "x", Hilfskraftart.TutorMaster, 10m, D(2027, 2), D(2027, 1), [(a.Kst.Id, 100m)])));

        // Eigener Stundensatz 2026 gilt auch für 2027 (letzter bekannter), nicht für andere Arten
        await Mit(u, d => d.StundensatzSetzenAsync(a.Team, 2026, Hilfskraftart.StudentischeHilfskraft, 20m));
        (await ZahlenAsync(u, a.Team, a.Kst.Id)).Gebunden.ShouldBe(8 * 400m);
        var saetze = await Mit(u, d => d.StundensaetzeAsync(a.Team, 2027));
        saetze.Single(s => s.Art == Hilfskraftart.StudentischeHilfskraft).ShouldBe(new Stundensatzansicht(Hilfskraftart.StudentischeHilfskraft, 2027, 20m, false));
        saetze.Single(s => s.Art == Hilfskraftart.WissHilfskraftMaster).EuroProStunde.ShouldBe(26.98m);
    }

    [Fact]
    public async Task Einstellungsmoeglichkeiten_und_Rechner_nutzen_zuerst_auslaufende_Mittel()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var a = await AufbauAsync(u);

        var m = await Mit(u, d => d.EinstellungsmoeglichkeitenAsync(a.Team, D(2026, 12)));
        m.Select(x => x.Topf.Id).ShouldBe([a.Kst.Id, a.Sqm.Id], ignoreOrder: true);   // Investition zahlt keine Hilfskräfte
        var sqm = m.Single(x => x.Topf.Id == a.Sqm.Id);
        (sqm.Frei, sqm.Monate, sqm.Ende).ShouldBe((1414m, 2, D(2027, 1)));
        sqm.StundenProMonat[Hilfskraftart.StudentischeHilfskraft].ShouldBe(36m);   // 1.414 / (2 × 19,50) = 36,26
        m.Single(x => x.Topf.Id == a.Kst.Id).Monate.ShouldBe(1);   // Kostenstelle: bis Jahresende

        // 20 h SHK = 390 €/Monat ab Dezember bis Juni: SQM trägt Dezember und Januar (Laufzeitende), danach die Kostenstelle
        var vorschlag = await Mit(u, d => d.HilfskraftRechnenAsync(a.Team, Hilfskraftart.StudentischeHilfskraft, 20m, D(2026, 12), D(2027, 6)));
        vorschlag.Select(x => (x.Topf.Kennung, x.Von, x.Bis, x.Kosten)).ShouldBe([("SQM-2026-15", D(2026, 12), D(2027, 1), 780m), (Kst, D(2027, 2), D(2027, 6), 1950m)]);

        var neu = await Mit(u, d => d.VorschlagUebernehmenAsync(a.Team, "N. N. Tutor Embedded", Hilfskraftart.StudentischeHilfskraft, 20m,
            [.. vorschlag.Select(x => (x.Topf.Id, x.Von, x.Bis))]));
        neu.Count.ShouldBe(2);
        (await ZahlenAsync(u, a.Team, a.Sqm.Id)).Frei.ShouldBe(1414m - 780m);
        (await Mit(u, d => d.HilfskraefteAsync(a.Team))).Count.ShouldBe(2);
    }

    [Fact]
    public void Rechner_stoppt_wenn_ein_Monat_nicht_mehr_bezahlt_werden_kann()
    {
        var sqm = Topf.Sqm(Guid.NewGuid(), "SQM-2026-1", "x", 1000m, 0m);
        static decimal Satz(Hilfskraftart a, int j) => 20m;
        var v = Bindungsrechnung.Vorschlagen([(sqm, 1000m)], Hilfskraftart.StudentischeHilfskraft, 20m, D(2026, 3), null, Satz);
        v.ShouldHaveSingleItem().Monate.ShouldBe(2);   // 400 € je Monat: März und April, Mai reicht nicht mehr
        Bindungsrechnung.Vorschlagen([(sqm, 1000m)], Hilfskraftart.StudentischeHilfskraft, 20m, D(2026, 2), null, Satz).ShouldBeEmpty();   // vor Mittelfreigabe
        Bindungsrechnung.Moeglichkeit(sqm, 1000m, D(2027, 3), null, Satz).ShouldNotBeNull().Monate.ShouldBe(0);   // nach Laufzeitende
    }

    // ------------------------------------------------------------------ Zeitachse und Aufteilen

    [Fact]
    public async Task Zeitachse_zeigt_Abfluesse_erwartete_Zugaenge_Verfall_und_Uebertragsgrenze()
    {
        await using var u = await TestUmgebung.StartenAsync();   // 20.11.2026
        var a = await AufbauAsync(u);
        await Mit(u, d => d.AusgabeAnlegenAsync(a.Team, "ABB-Zubehör", D(2026, 12), [new Anteil(a.Kst.Id, 4000m)]));
        var z = await Mit(u, d => d.ZeitachseAsync(a.Team));

        z.Monate.First().ShouldBe(D(2026, 11));
        z.Monate.Last().ShouldBe(D(2027, 12));
        var kst = z.Reihen.Single(r => r.Topf.Id == a.Kst.Id).Punkte;
        (kst[0].Frei, kst[0].Erwartet).ShouldBe((7851.44m, false));
        (kst[1].Frei, kst[1].Abfluss, kst[1].Grenze, kst[1].EinzugMoeglich).ShouldBe((3851.44m, 4000m, 1183.92m, 3851.44m - 1183.92m));
        (kst[2].Frei, kst[2].Zugang, kst[2].Erwartet).ShouldBe((3851.44m + 9866m, 9866m, true));   // Budget 2027 erwartet (Wert 2026)

        var sqm = z.Reihen.Single(r => r.Topf.Id == a.Sqm.Id).Punkte;
        (sqm[2].Monat, sqm[2].Verfaellt).ShouldBe((D(2027, 1), 1414m));
        (sqm[3].Frei, sqm[3].Aktiv).ShouldBe((0m, false));
        z.Reihen.Single(r => r.Topf.Id == a.Inv.Id).Punkte.Single(p => p.Verfaellt is not null).Monat.ShouldBe(D(2027, 8));
        z.Summe[3].ShouldBe(3851.44m + 9866m + 3200m);

        // Was wäre, wenn: angenommene Ausgabe im Januar
        var annahme = await Mit(u, d => d.ZeitachseAsync(a.Team, [new Anteil(a.Sqm.Id, 1000m)], D(2027, 1)));
        annahme.Reihen.Single(r => r.Topf.Id == a.Sqm.Id).Punkte[2].Verfaellt.ShouldBe(414m);
    }

    [Fact]
    public async Task Aufteilen_setzt_zuerst_verfallende_Mittel_und_den_Ueberhang_ein()
    {
        await using var u = await TestUmgebung.StartenAsync();   // 20.11.2026
        var a = await AufbauAsync(u);
        var sach = await Mit(u, d => d.SqmAnlegenAsync(a.Team, "SQM-2026-20", "Aufbauten", 0m, 1000m, "4 Verbesserung der Laborausstattung"));

        // Dezember, Gerät: SQM-Sachmittel verfallen im Januar → zuerst; dann Überhang der Kostenstelle; Investition erst danach
        var v = await Mit(u, d => d.AufteilenAsync(a.Team, 2500m, Ausgabenzweck.Geraet, D(2026, 12)));
        v.Anteile.Select(x => (x.Topf.Kennung, x.Betrag)).ShouldBe([("SQM-2026-20", 1000m), (Kst, 1500m)]);
        v.Anteile[0].Grund.ShouldContain("verfällt sonst");
        v.Anteile[1].Grund.ShouldContain("Übertragsgrenze");
        v.Hinweise.ShouldContain(h => h.Contains("Bagatellgrenze", StringComparison.Ordinal));
        (v.Fehlbetrag, v.FruehesterMonat).ShouldBe((0m, (DateOnly?)D(2026, 12)));
        v.Anteile.ShouldNotContain(x => x.Topf.Id == a.Sqm.Id);   // nur Personalmittel

        // Sachmittel dürfen nicht in die Investition
        (await Mit(u, d => d.AufteilenAsync(a.Team, 100m, Ausgabenzweck.Sachmittel, D(2027, 3)))).Anteile.ShouldAllBe(x => x.Topf.Art == Topfart.Kostenstelle);

        // Zu viel: fehlt jetzt, reicht ab Januar mit dem erwarteten Budget 2027
        var gross = await Mit(u, d => d.AufteilenAsync(a.Team, 15000m, Ausgabenzweck.Sachmittel, D(2026, 12)));
        (gross.Fehlbetrag, gross.FruehesterMonat).ShouldBe((15000m - 7851.44m - 1000m, (DateOnly?)D(2027, 1)));
        gross.Hinweise[0].ShouldContain("ab 01.2027");

        // Investition passt erst, wenn der Gerätezweck es erlaubt, und nur bis zum Bestellschluss
        Aufteilungsrechnung.Passt(a.Inv, Ausgabenzweck.Geraet, D(2027, 8)).ShouldBeTrue();
        Aufteilungsrechnung.Passt(a.Inv, Ausgabenzweck.Geraet, D(2027, 9)).ShouldBeFalse();
        Aufteilungsrechnung.Passt(sach, Ausgabenzweck.Sachmittel, D(2026, 2)).ShouldBeFalse();
    }

    [Fact]
    public async Task Datenbank_von_Version_13_bekommt_die_Planungstabellen_ohne_Datenverlust()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var a = await AufbauAsync(u);
        await u.InBereichAsync(async d =>
        {
            var db = d.GetRequiredService<ProfOMatDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Buchungsnotizen\"; DROP TABLE \"Einzelbuchungen\"; DROP TABLE \"Prognoseveranstaltungen\"; "
                                                 + "DROP TABLE \"Budgetprognosen\"; DROP TABLE \"Finanzerinnerungen\";");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Ausgabenanteile\"; DROP TABLE \"GeplanteAusgaben\"; DROP TABLE \"Auslagenpositionen\"; "
                                                 + "DROP TABLE \"Auslagenabrechnungen\"; DROP TABLE \"Hilfskraftanteile\"; DROP TABLE \"Hilfskraefte\"; DROP TABLE \"Stundensaetze\";");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Schemainfo\" SET \"Version\" = 13;");
        });

        await DatenbankInitialisierung.InitialisierenAsync(u.Dienste, TestContext.Current.CancellationToken);

        (await ZahlenAsync(u, a.Team, a.Kst.Id)).Frei.ShouldBe(7851.44m);
        await Mit(u, d => d.AusgabeAnlegenAsync(a.Team, "nach der Aufwertung", D(2026, 12), [new Anteil(a.Kst.Id, 1m)]));
        (await u.InBereichAsync(d => d.GetRequiredService<ProfOMatDbContext>().Set<Schemainfo>().SingleAsync())).Version.ShouldBe(ProfOMatDbContext.Schemaversion);
    }
}
