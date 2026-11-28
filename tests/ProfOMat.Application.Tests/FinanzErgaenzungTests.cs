using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Finanzen;
using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Finanzen;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Application.Tests;

/// <summary>Buchungsnotizen (FA-718), Einzelbuchungen (FA-705), Verlauf/Jahresvergleich (FA-711), Budgetprognose (FA-713), Erinnerungen (FA-712). Uhr: 20.11.2026.</summary>
public sealed class FinanzErgaenzungTests
{
    private const string Kst = "12103012";

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private static Task<T> Mit<T>(TestUmgebung u, Func<FinanzDienst, Task<T>> arbeit)
        => u.InBereichAsync(d => arbeit(d.GetRequiredService<FinanzDienst>()));

    private static Task Mit(TestUmgebung u, Func<FinanzDienst, Task> arbeit)
        => u.InBereichAsync(d => arbeit(d.GetRequiredService<FinanzDienst>()));

    private static DateOnly D(int j, int m, int t = 1) => new(j, m, t);

    private static byte[] Auszug(params (string Datum, string Art, decimal Wert, string Text)[] z)
    {
        static string B(decimal d) => (d < 0 ? "- " : " ") + Math.Abs(d).ToString("#,##0.00", De);
        var sb = new StringBuilder(" Kostenst.\tErfaßt am\tBelegdatum\tBuchDatum\tKostenart\tKostenartenbezeichn.\t Wert/KWähr\tBezeichnung\tRefBelnr\r\n");
        foreach (var (datum, art, wert, text) in z)
        {
            sb.Append($" {Kst}\t{datum}\t{datum}\t{datum}\t{art}\tx\t{B(wert)}\t{text}\t\r\n");
        }

        sb.Append($"* \t\t\t\t\t\t{B(z.Sum(x => x.Wert))}\t\t\r\n");
        return Encoding.Unicode.GetBytes(sb.ToString());
    }

    private static readonly (string, string, decimal, string)[] Jahr2026 =
    [
        ("01.01.2026", "930300", -9866m, "Co/Budget 2026"),
        ("12.03.2026", "602110", 1234.56m, "Händler/Roboterteile"),
        ("29.05.2026", "602110", -10.67m, "Händler/Gutschrift LED-Leiste"),
        ("15.04.2026", "634000", 780m, "Hilfskraft April"),
        ("31.05.2026", "685100", 25m, "Dienstwagenpauschale"),
    ];

    private static async Task<(Guid Team, Guid Kst)> TeamAsync(TestUmgebung u)
    {
        await u.AlsAsync("liebler");
        var team = (await Mit(u, d => d.TeamAnlegenAsync("Labor"))).Id;
        await Mit(u, d => d.ImportUebernehmenAsync(team, "20260622 12103012 Tabelle.xls", Auszug(Jahr2026)));
        var kst = (await Mit(u, d => d.ToepfeAsync(team))).Single().Id;
        var niemeyer = (await u.KontoAsync("niemeyer")).Id;
        await Mit(u, d => d.AufnehmenAsync(team, niemeyer));
        return (team, kst);
    }

    // ------------------------------------------------------------------ Buchungsnotizen

    [Fact]
    public async Task Notizen_und_Markierungen_ueberleben_den_Import_und_zaehlen_im_Cockpit()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (team, kst) = await TeamAsync(u);
        var buchungen = await Mit(u, d => d.BuchungenAsync(team, kst, 2026));
        var gutschrift = buchungen.Single(b => b.Betrag == -10.67m);
        var reise = buchungen.Single(b => b.Kostenart == "685100");

        await Mit(u, d => d.NotizSetzenAsync(team, kst, gutschrift.Id, "Zweite Gutschrift fehlt noch", Buchungsmarkierung.Nachverfolgen));
        await Mit(u, d => d.NotizSetzenAsync(team, kst, reise.Id, "Gehört auf die Kostenstelle von Niemeyer", Buchungsmarkierung.NichtOk | Buchungsmarkierung.Rueckfrage));
        (await Mit(u, d => d.CockpitAsync(team))).OffeneMarkierungen[kst].ShouldBe(2);

        // Niemeyer sieht die Notizen mit Namen; neuer Import desselben Jahres: Notizen hängen am Fingerabdruck
        await u.AlsAsync("niemeyer");
        await Mit(u, d => d.ImportUebernehmenAsync(team, "20260722 12103012 Tabelle.xls", Auszug([.. Jahr2026, ("30.06.2026", "602110", 50m, "Neu")])));
        var neu = await Mit(u, d => d.BuchungenAsync(team, kst, 2026));
        var notizen = await Mit(u, d => d.NotizenAsync(team, kst, 2026));
        notizen.JeBuchung.Count.ShouldBe(2);
        var n = notizen.JeBuchung[neu.Single(b => b.Betrag == -10.67m).Fingerabdruck];
        (n.Notiz.Kommentar, n.Notiz.Markierung, n.Von).ShouldBe(("Zweite Gutschrift fehlt noch", Buchungsmarkierung.Nachverfolgen, "Prof. Dr. Klaus Liebler"));

        // Geklärt zählt nicht mehr als offen; beides leer entfernt die Notiz
        await Mit(u, d => d.NotizSetzenAsync(team, kst, neu.Single(b => b.Betrag == -10.67m).Id, "Gutschrift kam am 03.07.", Buchungsmarkierung.Nachverfolgen | Buchungsmarkierung.Geklaert));
        (await Mit(u, d => d.CockpitAsync(team))).OffeneMarkierungen[kst].ShouldBe(1);
        await Mit(u, d => d.NotizSetzenAsync(team, kst, neu.Single(b => b.Betrag == -10.67m).Id, "  ", Buchungsmarkierung.Keine));
        (await Mit(u, d => d.NotizenAsync(team, kst, 2026))).JeBuchung.Count.ShouldBe(1);

        // Korrigiert die Verwaltung die Reisebuchung (anderer Text), wird die Notiz „verwaist“ (bleibt sichtbar, löschbar)
        await Mit(u, d => d.ImportUebernehmenAsync(team, "20260822 12103012 Tabelle.xls", Auszug([.. Jahr2026.Where(z => z.Item2 != "685100"),
            ("31.05.2026", "685100", 25m, "Dienstwagenpauschale umgebucht"), ("30.06.2026", "602110", 50m, "Neu"), ("15.07.2026", "602110", 5m, "Juli")])));
        notizen = await Mit(u, d => d.NotizenAsync(team, kst, 2026));
        var verwaist = notizen.Verwaist.ShouldHaveSingleItem();
        verwaist.Notiz.Beschreibung.ShouldContain("Dienstwagenpauschale");
        await Mit(u, d => d.NotizLoeschenAsync(team, kst, verwaist.Notiz.Id));
        (await Mit(u, d => d.NotizenAsync(team, kst, 2026))).Verwaist.ShouldBeEmpty();

        await u.AlsAsync("mustermann");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.NotizenAsync(team, kst, 2026)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.NotizSetzenAsync(team, kst, neu[0].Id, "x", Buchungsmarkierung.NichtOk)));
    }

    // ------------------------------------------------------------------ Einzelbuchungen

    [Fact]
    public async Task Einzelbuchungen_zaehlen_nach_dem_Stichtag_des_Stands()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (team, kst) = await TeamAsync(u);
        var sqm = await Mit(u, d => d.SqmAnlegenAsync(team, "SQM-2026-14", "Material", 0m, 9300m));
        await Mit(u, d => d.StandErfassenAsync(team, sqm.Id, D(2026, 9, 22), 6909.18m));
        await Mit(u, d => d.EinzelbuchungAnlegenAsync(team, sqm.Id, D(2026, 9, 1), 50m, "schon im Stand enthalten"));
        await Mit(u, d => d.EinzelbuchungAnlegenAsync(team, sqm.Id, D(2026, 10, 15), 400m, "Bauteile Reichelt"));
        await Mit(u, d => d.EinzelbuchungAnlegenAsync(team, sqm.Id, D(2026, 10, 20), -20m, "Gutschrift"));
        var z = (await Mit(u, d => d.CockpitAsync(team))).Toepfe.Single(a => a.Topf.Id == sqm.Id).Zahlen;
        (z.Ausgegeben, z.Frei, z.Datenstand).ShouldBe((6909.18m + 380m, 9300m - 7289.18m, (DateOnly?)D(2026, 10, 20)));
        (await Mit(u, d => d.EinzelbuchungenAsync(team, sqm.Id))).Count.ShouldBe(3);

        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EinzelbuchungAnlegenAsync(team, kst, D(2026, 10, 1), 1m, "x")));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EinzelbuchungAnlegenAsync(team, sqm.Id, D(2026, 12, 1), 1m, "Zukunft")));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EinzelbuchungAnlegenAsync(team, sqm.Id, D(2026, 10, 1), 0m, "x")));
    }

    // ------------------------------------------------------------------ Verlauf und Jahresvergleich

    [Fact]
    public async Task Verlauf_je_Monat_und_Jahresvergleich_je_Kostenartengruppe()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (team, kst) = await TeamAsync(u);
        await Mit(u, d => d.ImportUebernehmenAsync(team, "Abschluss 2025.xls", Auszug(("01.01.2025", "930300", -6314m, "Co/Budget 2025"), ("10.05.2025", "602110", 500m, "Material"),
            ("12.12.2025", "685100", 389m, "Reise"))));
        await Mit(u, d => d.AusgabeAnlegenAsync(team, "Zubehör", D(2026, 12), [new Anteil(kst, 1000m)]));

        var v = await Mit(u, d => d.VerlaufAsync(team, kst, 2026));
        v.Count.ShouldBe(12);
        (v[0].Zugaenge, v[0].RestAmEnde).ShouldBe((9866m, 9866m));
        v[2].Ausgaben.ShouldBe(1234.56m);
        v[4].Ausgaben.ShouldBe(25m - 10.67m);
        v[11].Geplant.ShouldBe(1000m);
        v[11].RestAmEnde.ShouldBe(9866m - 1234.56m - 780m - 25m + 10.67m);
        (await Mit(u, d => d.VerlaufAsync(team, kst, 2025)))[11].Geplant.ShouldBe(0m);

        var (jahre, zeilen) = await Mit(u, d => d.JahresvergleichAsync(team, kst));
        jahre.ShouldBe([2025, 2026]);
        zeilen[0].Gruppe.ShouldBe("Material");
        (zeilen[0].Werte[2025], zeilen[0].Werte[2026]).ShouldBe((500m, 1234.56m - 10.67m));
        zeilen.ShouldNotContain(z => z.Gruppe == Kostenartgruppe.Zuweisungen);
    }

    // ------------------------------------------------------------------ Budgetprognose

    [Fact]
    public async Task Budgetprognose_nach_den_Regeln_der_Fakultaet_mit_Vorlage_und_Wirkung_auf_die_Zeitachse()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (team, kst) = await TeamAsync(u);
        var leer = await Mit(u, d => d.PrognoseAsync(team, kst, 2026));
        (leer.Neu, leer.Vorlage, leer.Gebucht).ShouldBe((true, false, (decimal?)9866m));

        // 2026: 2 Prof. × 1.250 + 1 WM × 800 + Praktika + verfallene SWS (SoSe 6 → höchstens 800) + 1 SWS Abschlussarbeiten
        var p = await Mit(u, d => d.PrognoseSpeichernAsync(team, kst, 2026, 2m, 1m, 6m, 1m, 1m,
            [("SoSe 2025", "Regelungstechnik Praktikum", Veranstaltungsart.Praktikum, 80, 1m), ("WiSe 2025/26", "Embedded Systems", Veranstaltungsart.Praktikum, 60, 2m),
             ("WiSe 2025/26", "", Veranstaltungsart.Projekt, 1, 1m)],
            (12.50m, 200m, 800m, 1250m, 800m)));
        p.Veranstaltungen.Count.ShouldBe(2);   // ohne Bezeichnung: weggelassen
        (p.Lehre, p.Verfallen, p.Abschlussarbeiten, p.Personen).ShouldBe((200m * 12.50m, 800m + 200m, 200m, 3300m));
        p.Summe.ShouldBe(2500m + 1000m + 200m + 3300m);

        // Vorlage für 2027 aus 2026: Personen und Veranstaltungen (Semester weitergezählt), verfallene SWS leer
        var vorlage = await Mit(u, d => d.PrognoseAsync(team, kst, 2027));
        (vorlage.Neu, vorlage.Vorlage, vorlage.Gebucht).ShouldBe((true, true, (decimal?)null));
        vorlage.Prognose.Veranstaltungen.Select(v => v.Semester).ShouldBe(["SoSe 2026", "WiSe 2026/27"]);
        vorlage.Prognose.Verfallen.ShouldBe(0m);
        FinanzDienst.Semesterweiter("WiSe 2099/00").ShouldBe("WiSe 2100/01");

        // Gespeicherte Prognose 2027 ersetzt in der Zeitachse den Vorjahreswert im Januar
        await Mit(u, d => d.PrognoseSpeichernAsync(team, kst, 2027, 2m, 1m, 0m, 0m, 0m, [("SoSe 2026", "Praktikum", Veranstaltungsart.Praktikum, 100, 4m)], (12.50m, 200m, 800m, 1250m, 800m)));
        var achse = await Mit(u, d => d.ZeitachseAsync(team));
        achse.Reihen.Single().Punkte.Single(x => x.Monat == D(2027, 1)).Zugang.ShouldBe(5000m + 3300m);

        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.PrognoseSpeichernAsync(team, kst, 2030, 1m, 0m, 0m, 0m, 0m, [], (12.50m, 200m, 800m, 1250m, 800m))));
        var sqm = await Mit(u, d => d.SqmAnlegenAsync(team, "SQM-2026-15", "x", 1m, 0m));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.PrognoseAsync(team, sqm.Id, 2027)));
    }

    // ------------------------------------------------------------------ Erinnerungen

    [Fact]
    public async Task Fristerinnerungen_gehen_einmal_an_alle_Mitglieder_und_nur_wenn_Mittel_frei_sind()
    {
        await using var u = await TestUmgebung.StartenAsync();   // 20.11.2026
        var (team, kst) = await TeamAsync(u);
        var sqm = await Mit(u, d => d.SqmAnlegenAsync(team, "SQM-2026-15", "Hiwis", 5000m, 0m));
        await Mit(u, d => d.StandErfassenAsync(team, sqm.Id, D(2026, 9, 22), 3586m));
        var leer = await Mit(u, d => d.SqmAnlegenAsync(team, "SQM-2026-16", "verbraucht", 1000m, 0m));
        await Mit(u, d => d.StandErfassenAsync(team, leer.Id, D(2026, 9, 22), 1000m));

        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.ErinnerungenVersendenAsync()));   // nur der Hintergrunddienst
        u.Benutzer.Aktuell = ProfOMat.Application.Benutzer.Niemand;
        (await Mit(u, d => d.ErinnerungenVersendenAsync())).ShouldBe(0);   // Vorlauf 30 Tage: Jahresende erst in 41 Tagen

        await u.AlsAsync("liebler");
        await Mit(u, d => d.ErinnerungEinstellenAsync(team, 90));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.ErinnerungEinstellenAsync(team, 400)));
        u.Benutzer.Aktuell = ProfOMat.Application.Benutzer.Niemand;
        // Jahresende (Überhang über der Grenze), Investitionsanträge 15.01., Rechnungsabgabe SQM-2026-15 – nicht SQM-2026-16 (nichts frei)
        (await Mit(u, d => d.ErinnerungenVersendenAsync())).ShouldBe(3 * 2);
        var mails = await u.InBereichAsync(d => d.GetRequiredService<ProfOMatDbContext>().Set<Nachricht>().Where(n => n.Betreff.StartsWith("ProfOMat-Finanzen")).ToListAsync());
        mails.Select(m => m.An).Distinct().Count().ShouldBe(2);
        mails.ShouldContain(m => m.Betreff.Contains("Rechnungsabgabe SQM-2026-15", StringComparison.Ordinal) && m.Text.Contains("Noch frei: 1.414,00 €", StringComparison.Ordinal));
        mails.ShouldContain(m => m.Text.Contains("Über der Übertragsgrenze", StringComparison.Ordinal));
        mails.ShouldNotContain(m => m.Betreff.Contains("SQM-2026-16", StringComparison.Ordinal));
        (await Mit(u, d => d.ErinnerungenVersendenAsync())).ShouldBe(0);   // jede Frist nur einmal
    }

    [Fact]
    public async Task Datenbank_von_Version_14_bekommt_Notizen_Prognose_und_Erinnerungen_ohne_Datenverlust()
    {
        await using var u = await TestUmgebung.StartenAsync(sqlite: true);
        var (team, kst) = await TeamAsync(u);
        await u.InBereichAsync(async d =>
        {
            var db = d.GetRequiredService<ProfOMatDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Buchungsnotizen\"; DROP TABLE \"Einzelbuchungen\"; DROP TABLE \"Prognoseveranstaltungen\"; "
                                                 + "DROP TABLE \"Budgetprognosen\"; DROP TABLE \"Finanzerinnerungen\"; ALTER TABLE \"Laborteams\" DROP COLUMN \"ErinnerungVorlaufTage\";");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Schemainfo\" SET \"Version\" = 14;");
        });

        await SqliteAltbestand.InitialisierenAsync(u.Dienste, TestContext.Current.CancellationToken);

        (await Mit(u, d => d.TeamAsync(team))).Team.ErinnerungVorlaufTage.ShouldBe(Laborteam.StandardVorlaufTage);
        var b = (await Mit(u, d => d.BuchungenAsync(team, kst, 2026)))[1];
        await Mit(u, d => d.NotizSetzenAsync(team, kst, b.Id, "nach der Aufwertung", Buchungsmarkierung.NichtOk));
        (await u.InBereichAsync(d => d.GetRequiredService<ProfOMatDbContext>().Set<Schemainfo>().SingleAsync())).Version.ShouldBe(ProfOMatDbContext.Schemaversion);
    }
}
