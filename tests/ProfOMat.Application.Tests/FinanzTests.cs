using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Finanzen;
using ProfOMat.Domain.Finanzen;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Application.Tests;

public sealed class FinanzTests
{
    private const string Kst = "12103012";

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private static Task<T> Mit<T>(TestUmgebung u, Func<FinanzDienst, Task<T>> arbeit)
        => u.InBereichAsync(d => arbeit(d.GetRequiredService<FinanzDienst>()));

    private static Task Mit(TestUmgebung u, Func<FinanzDienst, Task> arbeit)
        => u.InBereichAsync(d => arbeit(d.GetRequiredService<FinanzDienst>()));

    /// <summary>Eine Buchungszeile: Buchungsdatum, Kostenart, Betrag (wie im Auszug: positiv = Ausgabe), Text.</summary>
    private sealed record Z(string Datum, string Art, decimal Betrag, string Text = "Amazon/Teile", string? Ref = "1970001", string ArtText = "Hilfsstoffe");

    /// <summary>Auszug wie aus SAP: UTF-16LE ohne BOM, tabgetrennt, Beträge „- 9.866,00“, Summenzeile mit „*“.</summary>
    private static byte[] Auszug(IEnumerable<Z> zeilen, string kst = Kst, string wertKopf = " Wert/KWähr", decimal? summe = null)
    {
        static string B(decimal d) => (d < 0 ? "- " : " ") + Math.Abs(d).ToString("#,##0.00", De);
        var liste = zeilen.ToList();
        var sb = new StringBuilder();
        sb.Append(" Kostenst.\tErfaßt am\tBelegdatum\tBuchDatum\tKostenart\tKostenartenbezeichn.\t").Append(wertKopf).Append("\tBezeichnung\tRefBelnr\r\n");
        foreach (var z in liste)
        {
            sb.Append(' ').Append(kst).Append('\t').Append(z.Datum).Append('\t').Append(z.Datum).Append('\t').Append(z.Datum).Append('\t')
                .Append(z.Art).Append('\t').Append(z.ArtText).Append('\t').Append(B(z.Betrag)).Append('\t').Append(z.Text).Append('\t').Append(z.Ref).Append("\r\n");
        }

        sb.Append("* \t\t\t\t\t\t").Append(B(summe ?? liste.Sum(z => z.Betrag))).Append("\t\t\r\n");
        return Encoding.Unicode.GetBytes(sb.ToString());   // ohne BOM wie der SAP-Export
    }

    private static readonly Z Budget2026 = new("01.01.2026", "930300", -9866m, "Co/Budget 2026 / 12103012", null, "Budget Sachmitte Fak");

    private static IEnumerable<Z> Jahr2026(int material = 3) =>
    [
        Budget2026,
        .. Enumerable.Range(1, material).Select(i => new Z($"{i:00}.03.2026", "602110", 100m, $"Amazon/Teil {i}", $"19700{i:00}")),
        new("15.04.2026", "634000", 800m, "0001234567 Hilfskraft, Erika", "908665", "Vergüt. stud.Hilfskr"),
    ];

    private static async Task<Guid> TeamAsync(TestUmgebung u, string als = "liebler")
    {
        await u.AlsAsync(als);
        return (await Mit(u, d => d.TeamAnlegenAsync("Labor Automatisierungstechnik"))).Id;
    }

    private static Task<IReadOnlyList<Importvorschau>> VorschauAsync(TestUmgebung u, Guid team, params (string Name, byte[] Inhalt)[] dateien)
        => Mit(u, d => d.ImportVorschauAsync(team, dateien));

    // ------------------------------------------------------------------ Leser

    [Fact]
    public void Auszug_wird_aus_UTF16_ohne_BOM_mit_deutschen_Betraegen_gelesen()
    {
        var a = KostenstellenauszugLeser.Lesen(Auszug(Jahr2026()), "20260622 12103012 Tabelle.xls");
        a.Fehler.ShouldBeEmpty();
        (a.Kostenstelle, a.Jahr, a.Exportdatum, a.BisDatum).ShouldBe((Kst, 2026, new DateOnly(2026, 6, 22), new DateOnly(2026, 4, 15)));
        a.Zeilen.Count.ShouldBe(5);
        a.Zeilen[0].Betrag.ShouldBe(-9866m);
        a.Zeilen[0].Belegnummer.ShouldBeNull();
        a.Zeilen[4].KostenartText.ShouldBe("Vergüt. stud.Hilfskr");
        a.Summe.ShouldBe(-9866m + 300m + 800m);
        KostenstellenauszugLeser.Exportdatum("Abschluss 2024.xls").ShouldBeNull();
    }

    [Fact]
    public void Abweichende_Kopfzeilen_und_UTF8_werden_erkannt()
    {
        var utf16 = Auszug(Jahr2026(), wertKopf: "Wert/KW");
        var utf8 = Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(utf16).Replace("Kostenartenbezeichn.", "KostenartenBez", StringComparison.Ordinal));
        var a = KostenstellenauszugLeser.Lesen(utf8, "x.xls");
        a.Gueltig.ShouldBeTrue();
        a.Zeilen[1].Text.ShouldBe("Amazon/Teil 1");
    }

    [Fact]
    public void Unvollstaendige_oder_falsche_Auszuege_werden_begruendet_abgewiesen()
    {
        KostenstellenauszugLeser.Lesen(Auszug(Jahr2026(), summe: 1m), "x").Fehler.ShouldContain(f => f.Contains("Summenzeile", StringComparison.Ordinal));
        KostenstellenauszugLeser.Lesen(Auszug(Jahr2026().Skip(1)), "x").Fehler.ShouldContain(f => f.StartsWith("Teilauszug", StringComparison.Ordinal));
        KostenstellenauszugLeser.Lesen(Auszug([.. Jahr2026(), new Z("31.12.2025", "602110", 5m)]), "x").Fehler
            .ShouldContain(f => f.Contains("mehreren Jahren", StringComparison.Ordinal));
        Should.Throw<FachlicherFehler>(() => KostenstellenauszugLeser.Lesen(Encoding.Unicode.GetBytes("Hallo\tWelt\r\n1\t2\r\n"), "brief.xls"))
            .Message.ShouldContain("kein SAP-Kostenstellenauszug");
        var kaputt = Encoding.Unicode.GetString(Auszug(Jahr2026())).Replace(" 800,00", " 8OO,00", StringComparison.Ordinal);
        Should.Throw<FachlicherFehler>(() => KostenstellenauszugLeser.Lesen(Encoding.Unicode.GetBytes(kaputt), "x.xls")).Message.ShouldContain("Zeile 6: Betrag „8OO,00“");
    }

    // ------------------------------------------------------------------ Rechte

    [Fact]
    public async Task Finanzdaten_sieht_nur_wer_zum_Laborteam_gehoert()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var team = await TeamAsync(u);

        await u.AlsAsync("mustermann");
        (await Mit(u, d => d.TeamsAsync())).ShouldBeEmpty();
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.CockpitAsync(team)))).Message.ShouldContain("Laborteam");
        await Should.ThrowAsync<FachlicherFehler>(() => VorschauAsync(u, team, ("a.xls", Auszug(Jahr2026()))));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.KostenstelleAnlegenAsync(team, Kst)));

        u.Benutzer.Aktuell = ProfOMat.Application.Benutzer.Niemand;
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.TeamsAsync()));

        await u.AlsAsync("sekretariat");
        FinanzDienst.TeamsAnlegen(u.Benutzer.Aktuell).Erlaubt.ShouldBeFalse();
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.TeamAnlegenAsync("Fremd")));
    }

    [Fact]
    public async Task Aufgenommene_sind_gleichberechtigt_das_letzte_Mitglied_bleibt()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var team = await TeamAsync(u);
        var liebler = (await u.KontoAsync("liebler")).Id;
        var niemeyer = (await u.KontoAsync("niemeyer")).Id;
        (await Mit(u, d => d.KandidatenAsync(team))).ShouldContain(k => k.KontoId == niemeyer);
        await Mit(u, d => d.AufnehmenAsync(team, niemeyer));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.AufnehmenAsync(team, niemeyer)));

        await u.AlsAsync("niemeyer");
        (await Mit(u, d => d.TeamsAsync())).Single().Id.ShouldBe(team);
        await Mit(u, d => d.KostenstelleAnlegenAsync(team, Kst));
        await Mit(u, d => d.EntfernenAsync(team, liebler));
        (await Mit(u, d => d.TeamAsync(team))).Mitglieder.Single().KontoId.ShouldBe(niemeyer);
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EntfernenAsync(team, niemeyer)))).Message.ShouldContain("letzte Mitglied");

        await u.AlsAsync("liebler");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.CockpitAsync(team)));
    }

    // ------------------------------------------------------------------ Import

    [Fact]
    public async Task Import_legt_die_Kostenstelle_an_und_ersetzt_den_Jahresstand()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var team = await TeamAsync(u);

        var v = (await VorschauAsync(u, team, ("20260513 12103012 Tabelle.xls", Auszug(Jahr2026())))).Single();
        (v.Freigabe.Erlaubt, v.NeuerTopf, v.Neu, v.Entfallen, v.RestNeu, v.RestBisher).ShouldBe((true, true, 5, 0, 9866m - 1100m, (decimal?)null));
        await Mit(u, d => d.ImportUebernehmenAsync(team, "20260513 12103012 Tabelle.xls", Auszug(Jahr2026())));
        var topf = (await Mit(u, d => d.ToepfeAsync(team))).Single();
        (topf.Art, topf.Kennung).ShouldBe((Topfart.Kostenstelle, Kst));

        // Neuer Stand: zwei Teile mehr, eine Buchung storniert und neu (anderer Text)
        var neu = Jahr2026(material: 5).Select(z => z.Text == "Amazon/Teil 1" ? z with { Text = "Amazon/Teil 1 (korrigiert)" } : z).ToList();
        v = (await VorschauAsync(u, team, ("20260622 12103012 Tabelle.xls", Auszug(neu)))).Single();
        (v.NeuerTopf, v.Neu, v.Entfallen, v.Unveraendert, v.Aelter).ShouldBe((false, 3, 1, 4, false));
        await Mit(u, d => d.ImportUebernehmenAsync(team, "20260622 12103012 Tabelle.xls", Auszug(neu)));
        (await Mit(u, d => d.BuchungenAsync(team, topf.Id, 2026))).Count.ShouldBe(7);
        var jahr = (await Mit(u, d => d.JahreAsync(team, topf.Id))).Single();
        (jahr.Dateiname, jahr.Exportdatum, jahr.Rest).ShouldBe(("20260622 12103012 Tabelle.xls", new DateOnly(2026, 6, 22), 9866m - 1300m));

        // Älterer Stand nur mit Bestätigung
        v = (await VorschauAsync(u, team, ("20260513 12103012 Tabelle.xls", Auszug(Jahr2026())))).Single();
        v.Aelter.ShouldBeTrue();
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.ImportUebernehmenAsync(team, "20260513 12103012 Tabelle.xls", Auszug(Jahr2026()))));
        await Mit(u, d => d.ImportUebernehmenAsync(team, "20260513 12103012 Tabelle.xls", Auszug(Jahr2026()), aelterBestaetigt: true));
        (await Mit(u, d => d.BuchungenAsync(team, topf.Id, 2026))).Count.ShouldBe(5);
    }

    [Fact]
    public async Task Mehrere_Dateien_je_Jahr_nur_der_neueste_Stand_und_Teilauszuege_werden_abgewiesen()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var team = await TeamAsync(u);
        var vorschau = await VorschauAsync(u, team,
            ("20260316 12103012 Tabelle.xls", Auszug(Jahr2026(material: 1).Take(2))),
            ("20260622 12103012 Tabelle.xls", Auszug(Jahr2026())),
            ("20250217 12103012 Tabelle.xls", Auszug([new Z("07.01.2025", "602110", 20m)])),
            ("andere.xls", Auszug(Jahr2026(), kst: "12109999")));
        vorschau[0].Ueberholt.ShouldBeTrue();
        vorschau[0].Freigabe.Erlaubt.ShouldBeFalse();
        vorschau[1].Freigabe.Erlaubt.ShouldBeTrue();
        vorschau[2].Freigabe.Begruendung!.ShouldContain("Teilauszug");
        (vorschau[3].Freigabe.Erlaubt, vorschau[3].NeuerTopf).ShouldBe((true, true));   // weitere Kostenstelle des Teams

        // Jahresabschluss mit demselben letzten Buchungsdatum wie der Januar-Auszug, aber Nachbuchungen: der Abschluss gewinnt
        Z[] januar = [new("01.01.2024", "930300", -6113m, "Co/Budget 2024", null, "Budget Sachmitte Fak"), new("31.12.2024", "602110", 10m)];
        Z[] abschluss = [.. januar, new("31.12.2024", "940106", 146.23m, "Co/Pauschaler Steuerbetrag")];
        vorschau = await VorschauAsync(u, team, ("20250122 12103012 Tabelle.xls", Auszug(januar)), ("Abschluss 2024.xls", Auszug(abschluss)));
        (vorschau[0].Ueberholt, vorschau[1].Ueberholt).ShouldBe((true, false));
        await Mit(u, d => d.ImportUebernehmenAsync(team, "Abschluss 2024.xls", Auszug(abschluss)));
        (await VorschauAsync(u, team, ("20250122 12103012 Tabelle.xls", Auszug(januar)))).Single().Aelter.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ Cockpit

    [Fact]
    public async Task Cockpit_rechnet_Rest_Vortrag_erwartet_und_warnt_vor_der_Uebertragsgrenze()
    {
        await using var u = await TestUmgebung.StartenAsync();   // Uhr: 20.11.2026
        var team = await TeamAsync(u);
        Z[] jahr2025 = [new("01.01.2025", "930300", -6314m, "Co/Budget 2025 / 12103012", null, "Budget Sachmitte Fak"), new("10.05.2025", "602110", 5000m)];
        await Mit(u, d => d.ImportUebernehmenAsync(team, "20260121 12103012 Tabelle.xls", Auszug(jahr2025)));
        await Mit(u, d => d.ImportUebernehmenAsync(team, "20260622 12103012 Tabelle.xls", Auszug(Jahr2026())));

        var c = await Mit(u, d => d.CockpitAsync(team));
        var z = c.Toepfe.Single().Zahlen;
        z.VortragErwartet.ShouldBe(1314m);
        (z.Verfuegbar, z.Ausgegeben, z.Frei).ShouldBe((9866m + 1314m, 1100m, 9866m + 1314m - 1100m));
        (z.Budget, z.Grenze).ShouldBe((9866m, 1183.92m));
        z.UeberGrenze.ShouldBe(z.Frei - 1183.92m);
        z.GrenzeHervorheben.ShouldBeTrue();
        z.Ampel.ShouldBe(Ampel.Warn);
        z.Hinweise.ShouldContain(h => h.Contains("Vortrag aus 2025", StringComparison.Ordinal));
        c.FreiVerwendbar.ShouldBe(z.Frei);
        c.Fristen.ShouldContain(f => f.Datum == new DateOnly(2026, 12, 31) && f.TopfId == c.Toepfe[0].Topf.Id);

        // Mit gebuchtem Vortrag kein „erwartet“ mehr; unter der Grenze keine Warnung
        await Mit(u, d => d.ImportUebernehmenAsync(team, "20261120 12103012 Tabelle.xls", Auszug(
            [.. Jahr2026(), new Z("01.01.2026", "930600", -1314m, "Co/Vortrag aus 2025", null, "Saldo aus Vorjahren"), new Z("20.10.2026", "980800", 9000m, "Co/Dell/PCs")])));
        z = (await Mit(u, d => d.CockpitAsync(team))).Toepfe.Single().Zahlen;
        (z.VortragErwartet, z.Frei, z.UeberGrenze, z.Ampel).ShouldBe(((decimal?)null, 9866m + 1314m - 10100m, (decimal?)null, Ampel.Gut));
    }

    [Fact]
    public void Grenzwarnung_wird_erst_ab_dem_dritten_Quartal_hervorgehoben_und_rechnet_Bindungen_bis_Jahresende_ein()
    {
        var topf = Topf.Kostenstelle(Guid.NewGuid(), Kst);
        Buchung B(decimal betrag, string art = "602110") => new(topf.Id, 2026, 1, null, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), art, "", betrag, "", null);
        Buchung[] buchungen = [B(-10000m, "930300"), B(2000m)];
        Bindung[] bindungen = [new(5000m, new DateOnly(2026, 12, 1), "Roboter-Zubehör"), new(1000m, new DateOnly(2027, 2, 1), "Hiwi Januar")];

        var mai = Finanzrechnung.Kostenstelle(topf, 2026, buchungen, null, bindungen, new DateOnly(2026, 5, 2));
        (mai.Grenze, mai.Prognose31Dez, mai.UeberGrenze, mai.GrenzeHervorheben).ShouldBe((1200m, 3000m, 1800m, false));
        (mai.Gebunden, mai.Frei).ShouldBe((6000m, 2000m));
        Finanzrechnung.Kostenstelle(topf, 2026, buchungen, null, bindungen, new DateOnly(2026, 7, 1)).GrenzeHervorheben.ShouldBeTrue();

        // Vergangenes Jahr: keine Prognose
        Finanzrechnung.Kostenstelle(topf, 2026, buchungen, null, [], new DateOnly(2027, 3, 1)).Grenze.ShouldBeNull();

        // Überplant
        Finanzrechnung.Kostenstelle(topf, 2026, buchungen, null, [new(9000m, new DateOnly(2026, 12, 1), "x")], new DateOnly(2026, 5, 2)).Ampel.ShouldBe(Ampel.Krit);
    }

    // ------------------------------------------------------------------ Maßnahmen

    [Fact]
    public void SQM_und_Investition_bekommen_Laufzeit_und_Fristen_aus_dem_Zeitraster()
    {
        var team = Guid.NewGuid();
        var sqm = Topf.Sqm(team, "sqm-2026-15", "Hiwis für Aufbauten", 5000m, 0m, "3 Tutoren Hiwis");
        (sqm.Kennung, sqm.Jahr, sqm.LaufzeitVon, sqm.LaufzeitBis, sqm.Rechnungsschluss, sqm.Stichtag)
            .ShouldBe(("SQM-2026-15", (int?)2026, (DateOnly?)new DateOnly(2026, 3, 1), (DateOnly?)new DateOnly(2027, 1, 31), (DateOnly?)new DateOnly(2027, 1, 31), (DateOnly?)new DateOnly(2027, 1, 31)));
        var inv = Topf.Investition(team, "AUT-07-26", "Embedded Systems", 3200m, "71211637");
        (inv.Jahr, inv.Bestellschluss, inv.BewilligtSach, inv.Innenauftrag).ShouldBe(((int?)2026, (DateOnly?)new DateOnly(2027, 8, 31), 3200m, "71211637"));
        Topf.JahrAus("ohne Jahr").ShouldBeNull();
        Should.Throw<FachlicherFehler>(() => Topf.Kostenstelle(team, "1210301"));
        Should.Throw<FachlicherFehler>(() => sqm.Aendern(sqm.Angaben with { LaufzeitBis = new DateOnly(2025, 1, 1) }));
        Should.Throw<FachlicherFehler>(() => Topf.Kostenstelle(team, Kst).Aendern(new Topfangaben("x", BewilligtSach: 5)));
    }

    [Fact]
    public void Massnahme_warnt_vor_dem_Stichtag_und_meldet_verfallene_Mittel()
    {
        var sqm = Topf.Sqm(Guid.NewGuid(), "SQM-2026-15", "Hiwis", 5000m, 0m);
        var stand = new Massnahmenstand(sqm.Id, new DateOnly(2026, 11, 1), 3586m, Standquelle.Excel, DateTimeOffset.UnixEpoch);

        var vorher = Finanzrechnung.Massnahme(sqm, null, [], new DateOnly(2026, 2, 1));
        (vorher.Frei, vorher.Ampel).ShouldBe((5000m, Ampel.Neutral));
        vorher.Hinweise.ShouldContain(h => h.Contains("01.03.2026", StringComparison.Ordinal));

        var dezember = Finanzrechnung.Massnahme(sqm, stand, [], new DateOnly(2026, 12, 15));
        (dezember.Ausgegeben, dezember.Frei, dezember.Ampel).ShouldBe((3586m, 1414m, Ampel.Warn));
        dezember.Hinweise.ShouldContain(h => h.Contains("Rechnungsabgabe in 47 Tagen", StringComparison.Ordinal));

        Finanzrechnung.Massnahme(sqm, stand, [], new DateOnly(2026, 6, 1)).Ampel.ShouldBe(Ampel.Gut);
        Finanzrechnung.Massnahme(sqm, stand, [], new DateOnly(2027, 2, 1)).Ampel.ShouldBe(Ampel.Krit);
    }

    [Fact]
    public async Task Stand_einer_Massnahme_von_Hand_erfassen()
    {
        await using var u = await TestUmgebung.StartenAsync();   // 20.11.2026
        var team = await TeamAsync(u);
        var sqm = await Mit(u, d => d.SqmAnlegenAsync(team, "SQM-2026-15", "Hiwis für Aufbauten", 5000m, 0m));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.SqmAnlegenAsync(team, "sqm-2026-15", "doppelt", 1m, 0m)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.StandErfassenAsync(team, sqm.Id, new DateOnly(2026, 12, 1), 1m)));
        await Mit(u, d => d.StandErfassenAsync(team, sqm.Id, new DateOnly(2026, 9, 22), 3586m, "laut Excel der Fakultät"));
        var c = await Mit(u, d => d.CockpitAsync(team));
        c.Toepfe.Single().Zahlen.Frei.ShouldBe(1414m);
        c.FreiZweckgebunden.ShouldBe(1414m);
        c.Fristen.ShouldContain(f => f.Datum == new DateOnly(2027, 1, 31) && f.Frei == 1414m);
        c.Fristen.ShouldContain(f => f.Text.StartsWith("SQM-Anträge für 2028", StringComparison.Ordinal));

        var kst = await Mit(u, d => d.KostenstelleAnlegenAsync(team, Kst));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.StandErfassenAsync(team, kst.Id, new DateOnly(2026, 9, 22), 1m)));
        await Mit(u, d => d.ArchivierenAsync(team, sqm.Id, true));
        (await Mit(u, d => d.CockpitAsync(team))).Toepfe.Single().Topf.Id.ShouldBe(kst.Id);
    }

    [Fact]
    public async Task Datenbank_von_Version_12_bekommt_die_Finanztabellen_ohne_Datenverlust()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var konten = await u.InBereichAsync(async d =>
        {
            var db = d.GetRequiredService<ProfOMatDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Buchungsnotizen\"; DROP TABLE \"Einzelbuchungen\"; DROP TABLE \"Prognoseveranstaltungen\"; "
                                                 + "DROP TABLE \"Budgetprognosen\"; DROP TABLE \"Finanzerinnerungen\";");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Ausgabenanteile\"; DROP TABLE \"GeplanteAusgaben\"; DROP TABLE \"Auslagenpositionen\"; "
                                                 + "DROP TABLE \"Auslagenabrechnungen\"; DROP TABLE \"Hilfskraftanteile\"; DROP TABLE \"Hilfskraefte\"; DROP TABLE \"Stundensaetze\";");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Massnahmenstaende\"; DROP TABLE \"Kostenstellenstaende\"; DROP TABLE \"FinanzBuchungen\"; "
                                                 + "DROP TABLE \"FinanzToepfe\"; DROP TABLE \"Labormitglieder\"; DROP TABLE \"Laborteams\";");

            await db.Database.ExecuteSqlRawAsync("UPDATE \"Schemainfo\" SET \"Version\" = 12;");
            return await db.Set<ProfOMat.Domain.Konten.Konto>().CountAsync();
        });

        await DatenbankInitialisierung.InitialisierenAsync(u.Dienste, TestContext.Current.CancellationToken);

        var team = await TeamAsync(u);
        await Mit(u, d => d.ImportUebernehmenAsync(team, "a.xls", Auszug(Jahr2026())));
        (await Mit(u, d => d.CockpitAsync(team))).Toepfe.Count.ShouldBe(1);
        (await u.InBereichAsync(d => d.GetRequiredService<ProfOMatDbContext>().Set<ProfOMat.Domain.Konten.Konto>().CountAsync())).ShouldBe(konten);
        (await u.InBereichAsync(d => d.GetRequiredService<ProfOMatDbContext>().Set<Schemainfo>().SingleAsync())).Version.ShouldBe(ProfOMatDbContext.Schemaversion);
    }
}
