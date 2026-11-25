using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Betrieb;
using ProfOMat.Application.Hausarbeiten;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Hausarbeiten;
using ProfOMat.Domain.Klausuren;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Application.Tests;

/// <summary>Erörterungsgespräche (FA-608–FA-612). Uhr der Testumgebung: 20.11.2026.</summary>
public sealed class HausarbeitGespraechTests
{
    private const string Themen = """
        1. Zahnrad-Generator Software
        Aus Modul und Zähnezahl entsteht ein Zahnradpaar.

        2. Roboterarm Hardware
        Ein kleiner Roboterarm.
        """;

    private static readonly DateOnly Mo = new(2027, 3, 1);
    private static readonly DateOnly Di = new(2027, 3, 2);

    private static DateTime Um(DateOnly tag, int h, int m = 0) => tag.ToDateTime(new TimeOnly(h, m));

    private static Hausarbeit MitTeams(int anzahl)
    {
        var h = new Hausarbeit(Guid.NewGuid(), "Informatik für Maschinenbau", null, null, DateTimeOffset.UnixEpoch);
        h.Abgleichen([.. Enumerable.Range(1, anzahl).Select(i => new Listenzeile(i, $"10000{i:00}", $"Nachname{i}", $"Vorname{i}", $"p{i}@hs-osnabrueck.de"))]);
        h.ThemenEinlesen(Themen);
        var thema = h.ThemenSortiert.First().Id;
        foreach (var p in h.PersonenSortiert.ToList())
        {
            var t = h.TeamSpeichern(null, [p.Id], p.Id, [], null);
            h.ThemaZuteilen(t.Id, thema);
        }

        return h;
    }

    // ------------------------------------------------------------------ Fachmodell

    [Fact]
    public void Planung_im_15_Minuten_Raster_mit_Gespraechstagen_und_Zeitfenster()
    {
        var h = MitTeams(3);
        var teams = h.TeamsSortiert.ToList();
        Should.Throw<FachlicherFehler>(() => h.GespraecheEinstellen(20, [Mo], new TimeOnly(9, 0), new TimeOnly(12, 0), null)).Message.ShouldContain("15 Minuten");
        Should.Throw<FachlicherFehler>(() => h.GespraecheEinstellen(30, [Mo], new TimeOnly(9, 10), new TimeOnly(12, 0), null));
        Should.Throw<FachlicherFehler>(() => h.GespraecheEinstellen(60, [Mo], new TimeOnly(9, 0), new TimeOnly(9, 45), null));
        h.GespraecheEinstellen(45, [Di, Mo, Mo], new TimeOnly(9, 0), new TimeOnly(12, 0), "SI 0110");
        h.Gespraechstage.ShouldBe([Mo, Di]);

        h.TerminSetzbar(teams[0].Id, Um(Mo, 9)).Erlaubt.ShouldBeTrue();
        h.TerminSetzbar(teams[0].Id, Um(Mo, 11, 15)).Erlaubt.ShouldBeTrue();                                 // endet genau 12:00
        h.TerminSetzbar(teams[0].Id, Um(Mo, 11, 30)).Begruendung!.ShouldContain("zwischen 09:00 und 12:00");
        h.TerminSetzbar(teams[0].Id, Um(Mo, 9, 10)).Begruendung!.ShouldContain("Raster");
        h.TerminSetzbar(teams[0].Id, Um(new DateOnly(2027, 3, 3), 9)).Begruendung!.ShouldContain("kein Gesprächstag");

        h.TerminSetzen(teams[0].Id, Um(Mo, 9));
        h.TerminSetzen(teams[1].Id, Um(Mo, 9, 30));                                                         // überschneidet sich (9:00–9:45)
        h.Ueberschneidungen().ShouldHaveSingleItem().ShouldBe((teams[0], teams[1]));
        h.FreieTermine().First().ShouldBe(Um(Mo, 10, 15));
        h.OhneTerminVerteilen().ShouldBe(1);
        teams[2].Termin.ShouldBe(Um(Mo, 10, 15));
        h.TerminSetzen(teams[1].Id, null);
        h.Ueberschneidungen().ShouldBeEmpty();
        h.OhneTermin.ShouldBe([teams[1]]);
    }

    [Fact]
    public void Platzhalter_der_Terminmail_werden_geprueft_und_ersetzt()
    {
        var h = MitTeams(1);
        h.GespraecheEinstellen(30, [Mo], new TimeOnly(9, 0), new TimeOnly(12, 0), "SI 0110");
        var team = h.TeamsSortiert.Single();
        h.TerminSetzen(team.Id, Um(Mo, 9, 15));
        Should.Throw<FachlicherFehler>(() => h.TerminvorlageSetzen("Termin {Datum}", "Hallo {Vorname}, {Raum}")).Message.ShouldContain("{Raum}");
        h.TerminvorlageSetzen("Gespräch am {Wochentag}, {Datum}", "Hallo {Vorname} {Nachname}: {Uhrzeit}–{Ende} ({Dauer} min) in {Ort}, Thema {Thema}, {Absender}");
        var (betreff, text) = HausarbeitDienst.Terminmail(h, team, team.Mitglieder[0], "Prof. Dr. Liebler");
        betreff.ShouldBe("Gespräch am Montag, 01.03.2027");
        text.ShouldBe("Hallo Vorname1 Nachname1: 09:15–09:45 (30 min) in SI 0110, Thema 1. Zahnrad-Generator, Prof. Dr. Liebler");

        // Zurück auf den Standard: gespeichert wird nichts
        h.TerminvorlageSetzen(Hausarbeit.StandardTerminBetreff, Hausarbeit.StandardTerminVorlage);
        (h.TerminBetreff, h.TerminVorlage).ShouldBe((null, null));
        Terminplatzhalter.Unbekannte(Hausarbeit.StandardTerminVorlage).ShouldBeEmpty();
    }

    [Fact]
    public void Bestaetigungsmail_aus_editierbarer_Vorlage_mit_Hinweis_auf_Einschraenkungen()
    {
        var h = MitTeams(2);
        var teams = h.TeamsSortiert.ToList();
        var zweite = teams[1].Mitglieder[0].PersonId;
        h.TeamEntfernen(teams[1].Id);
        h.TeamSpeichern(teams[0].Id, [teams[0].Mitglieder[0].PersonId, zweite], teams[0].Mitglieder[0].PersonId, [], null);
        var team = h.TeamsSortiert.Single();
        var m = team.Mitglieder[0];

        var (betreff, text) = h.Bestaetigungsmail(team, m, "https://x.test/hausarbeit/bestaetigen/1/abc", "05.12.2026", "Prof. Dr. Liebler");
        betreff.ShouldBe("Hausarbeit Informatik für Maschinenbau: Ihr Thema – bitte bis zum 05.12.2026 bestätigen");
        text.ShouldContain("habe ich Ihrem Team (mit Vorname2 Nachname2) das folgende Thema zugeteilt:");
        text.ShouldContain("1. Zahnrad-Generator (Software)\n\nAus Modul und Zähnezahl entsteht ein Zahnradpaar.");
        text.ShouldContain("bis zum 05.12.2026 über Ihren persönlichen Link:\nhttps://x.test/hausarbeit/bestaetigen/1/abc");
        text.ShouldContain("terminlichen Einschränkungen für das Erörterungsgespräch");
        text.ShouldNotContain("\n\n\n");
        var ohneFrist = h.Bestaetigungsmail(team, m, "l", null, "x");
        ohneFrist.Betreff.ShouldBe("Hausarbeit Informatik für Maschinenbau: Ihr Thema – bitte bestätigen");
        ohneFrist.Text.ShouldContain("Annahme des Themas über Ihren persönlichen Link:");   // leerer Platzhalter ohne doppeltes Leerzeichen
        Hausarbeit.StandardBestaetigungVorlage.ShouldContain("des Themas {BisFrist} über");
        Bestaetigungsplatzhalter.Ersetzen("Bitte {BisFrist}. Danke", new Dictionary<string, string> { ["BisFrist"] = "" }).ShouldBe("Bitte. Danke");

        Should.Throw<FachlicherFehler>(() => h.BestaetigungsvorlageSetzen("x", "Hallo {Name}, ohne Link")).Message.ShouldContain("{Link}");
        Should.Throw<FachlicherFehler>(() => h.BestaetigungsvorlageSetzen("x {Termin}", "{Link}")).Message.ShouldContain("{Termin}");
        h.BestaetigungsvorlageSetzen("Thema für {Vorname}", "Hallo {Vorname}: {Thema}{Beschreibung}\n{Link} {BisFrist}");
        h.Bestaetigungsmail(team, m, "L", null, "A").ShouldBe(("Thema für Vorname1", "Hallo Vorname1: 1. Zahnrad-Generator (Software)Aus Modul und Zähnezahl entsteht ein Zahnradpaar.\nL"));
        h.BestaetigungsvorlageSetzen(Hausarbeit.StandardBestaetigungBetreff, Hausarbeit.StandardBestaetigungVorlage);
        (h.BestaetigungBetreff, h.BestaetigungVorlage).ShouldBe((null, null));
        Bestaetigungsplatzhalter.Unbekannte(Hausarbeit.StandardBestaetigungVorlage).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ Ablauf mit Dienst

    private static Task<T> Mit<T>(TestUmgebung u, Func<HausarbeitDienst, Task<T>> arbeit) => u.InBereichAsync(d => arbeit(d.GetRequiredService<HausarbeitDienst>()));

    private static Task Mit(TestUmgebung u, Func<HausarbeitDienst, Task> arbeit) => u.InBereichAsync(d => arbeit(d.GetRequiredService<HausarbeitDienst>()));

    private static (Guid Mitglied, string Token) Link(string text)
    {
        var teile = text.Split('\n').Single(z => z.Contains("/hausarbeit/bestaetigen/", StringComparison.Ordinal)).Trim().Split('/');
        return (Guid.Parse(teile[^2]), teile[^1]);
    }

    [Fact]
    public async Task Einschraenkungen_Kalender_Terminmails_ZIP_und_Outlook_Kalender()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("mustermann");
        var h = await Mit(u, d => d.AusListeAnlegenAsync(PruefungslisteTests.MehrerePruefungen(), null, "11B1370-1-VL-MB", null));
        await Mit(u, d => d.ThemenEinlesenAsync(h.Id, Themen));
        h = await Mit(u, d => d.LadenAsync(h.Id));
        var personen = h.PersonenSortiert.Select(p => p.Id).ToList();
        var team = await Mit(u, d => d.TeamSpeichernAsync(h.Id, null, personen, personen[0], [], null));
        await Mit(u, d => d.ThemaZuteilenAsync(h.Id, team.Id, h.ThemenSortiert.First().Id));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.BestaetigungsvorlageSetzenAsync(h.Id, "x", "ohne Link")));
        await Mit(u, d => d.BestaetigungsvorlageSetzenAsync(h.Id, "Ihr Thema {Thema}", "Hallo {Vorname},\nbitte bestätigen und Einschränkungen nennen:\n{Link}\n{Absender}"));
        await Mit(u, d => d.BestaetigungsmailsErstellenAsync(h.Id));
        var eigene = (await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().MeineAsync(false))).Single(e => e.AnName == "Anna Alpha");
        eigene.Betreff.ShouldBe("Ihr Thema 1. Zahnrad-Generator (Software)");
        eigene.Text.ShouldStartWith("Hallo Anna,\nbitte bestätigen und Einschränkungen nennen:\nhttps://profomat.test/hausarbeit/bestaetigen/");
        var links = (await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().MeineAsync(false))).ToDictionary(e => e.AnName!, e => Link(e.Text));

        // Studierende: Anna meldet Einschränkungen bei der Bestätigung, Gina bestätigt ohne (wie die bisherigen) und ergänzt später
        u.Benutzer.Aktuell = Benutzer.Niemand;
        var (anna, annaToken) = links["Anna Alpha"];
        (await Mit(u, d => d.BestaetigenAsync(anna, annaToken, "  Dienstags nicht, montags erst ab 11 Uhr  ")))!.Einschraenkungen.ShouldBe("Dienstags nicht, montags erst ab 11 Uhr");
        (await Mit(u, d => d.BestaetigenAsync(anna, annaToken, "anders")))!.Einschraenkungen.ShouldBe("Dienstags nicht, montags erst ab 11 Uhr");   // zweites Bestätigen ändert nichts
        var (gina, ginaToken) = links["Gina Gamma"];
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EinschraenkungenAendernAsync(gina, ginaToken, "erst bestätigen")));
        (await Mit(u, d => d.BestaetigenAsync(gina, ginaToken)))!.Einschraenkungen.ShouldBeNull();
        (await Mit(u, d => d.EinschraenkungenAendernAsync(gina, ginaToken + "x", "falscher Link"))).ShouldBeNull();
        (await Mit(u, d => d.EinschraenkungenAendernAsync(gina, ginaToken, "1. März ganztägig Klausur")))!.Einschraenkungen.ShouldBe("1. März ganztägig Klausur");

        // Planung: Einschränkungen sichtbar, Termin setzen, fremde Prüfende dürfen nichts
        await u.AlsAsync("mustermann");
        await Mit(u, d => d.GespraecheEinstellenAsync(h.Id, 30, [Mo, Di], new TimeOnly(9, 0), new TimeOnly(12, 0), "SI 0110"));
        h = await Mit(u, d => d.LadenAsync(h.Id));
        h.TeamMit(team.Id).Mitglieder.Select(m => m.Einschraenkungen).ShouldBe(["Dienstags nicht, montags erst ab 11 Uhr", "1. März ganztägig Klausur"], ignoreOrder: true);
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.TerminSetzenAsync(h.Id, team.Id, Um(Mo, 12))));
        await Mit(u, d => d.TerminSetzenAsync(h.Id, team.Id, Um(Mo, 11)));
        await u.AlsAsync("probst");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.TerminSetzenAsync(h.Id, team.Id, Um(Mo, 9))));

        // Terminmails: je Person ein eigener Entwurf (der Bestätigungsentwurf bleibt), nicht doppelt, nach Verschieben neu
        await u.AlsAsync("mustermann");
        await Mit(u, d => d.TerminvorlageSetzenAsync(h.Id, "Gespräch {Datum} {Uhrzeit}", "Hallo {Vorname}, {Wochentag} {Datum} {Uhrzeit}–{Ende} in {Ort}. {Absender}"));
        (await Mit(u, d => d.TerminmailsErstellenAsync(h.Id))).ShouldBe(2);
        (await Mit(u, d => d.TerminmailsErstellenAsync(h.Id))).ShouldBe(0);
        var termin = (await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().MeineAsync(false))).Where(e => e.Anlass == HausarbeitDienst.AnlassTermin).ToList();
        termin.Count.ShouldBe(2);
        termin.Single(e => e.AnName == "Anna Alpha").Text.ShouldBe("Hallo Anna, Montag 01.03.2027 11:00–11:30 in SI 0110. Prof. Dr. Erika Mustermann");
        termin[0].Betreff.ShouldBe("Gespräch 01.03.2027 11:00");
        u.Benutzer.Aktuell = Benutzer.Niemand;
        (await Mit(u, d => d.BestaetigungAnsichtAsync(anna, annaToken)))!.Termin.ShouldBe(Um(Mo, 11));

        await u.AlsAsync("mustermann");
        await Mit(u, d => d.TerminSetzenAsync(h.Id, team.Id, Um(Di, 9, 30)));
        u.Benutzer.Aktuell = Benutzer.Niemand;
        (await Mit(u, d => d.BestaetigungAnsichtAsync(anna, annaToken)))!.Termin.ShouldBeNull();   // neuer Termin noch nicht mitgeteilt
        await u.AlsAsync("mustermann");
        (await Mit(u, d => d.TerminmailsErstellenAsync(h.Id))).ShouldBe(2);
        var offen = await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().MeineAsync(false));
        (offen.Count(e => e.Anlass == HausarbeitDienst.AnlassTermin), offen.Count(e => e.Anlass == HausarbeitDienst.AnlassBestaetigung)).ShouldBe((2, 2));

        // Alle .eml als ZIP (nur Terminmails bzw. alle offenen)
        var (zip, zipName, anzahl) = await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().ZipAsync(HausarbeitDienst.AnlassTermin));
        (zipName, anzahl).ShouldBe(("mails.zip", 2));
        using (var archiv = new ZipArchive(new MemoryStream(zip)))
        {
            archiv.Entries.Count.ShouldBe(2);
            archiv.Entries.ShouldAllBe(e => e.Name.EndsWith(".eml", StringComparison.Ordinal));
            using var r = new StreamReader(archiv.Entries[0].Open());
            (await r.ReadToEndAsync(TestContext.Current.CancellationToken)).ShouldStartWith("X-Unsent: 1");
        }

        (await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().ZipAsync())).Anzahl.ShouldBe(4);
        (await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().OffeneAsync())).ShouldBe(0);   // alle als geöffnet gemerkt
        await u.AlsAsync("probst");
        await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().ZipAsync()));

        // Kalender für Outlook: Datei (angemeldet) und Abo-Link (ohne Anmeldung, geheimer Token)
        await u.AlsAsync("mustermann");
        var (ics, icsName) = await Mit(u, d => d.KalenderAsync(h.Id));
        icsName.ShouldEndWith("-gespraeche.ics");
        var text = Encoding.UTF8.GetString(ics);
        text.ShouldStartWith("BEGIN:VCALENDAR\r\n");
        text.ShouldContain("DTSTART:20270302T083000Z");   // 09:30 MEZ
        text.ShouldContain("DTEND:20270302T090000Z");
        text.ShouldContain("LOCATION:SI 0110");
        text.ShouldContain($"UID:{team.Id:N}@profomat");
        text.Replace("\r\n ", "", StringComparison.Ordinal).ShouldContain("Einschränkungen Anna Alpha: Dienstags nicht\\, montags erst ab 11 Uhr");
        text.Split("\r\n").ShouldAllBe(z => Encoding.UTF8.GetByteCount(z) <= 75);

        var abo = await Mit(u, d => d.KalenderAboErzeugenAsync(h.Id));
        abo.ShouldStartWith($"https://profomat.test/hausarbeit/kalender/{h.Id:D}/");
        var token = abo.Split('/')[^1].Replace(".ics", "", StringComparison.Ordinal);
        u.Benutzer.Aktuell = Benutzer.Niemand;
        (await Mit(u, d => d.KalenderAboAsync(h.Id, token))).ShouldNotBeNull();
        (await Mit(u, d => d.KalenderAboAsync(h.Id, token + "x"))).ShouldBeNull();
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.KalenderAsync(h.Id)));
        await u.AlsAsync("mustermann");
        await Mit(u, d => d.KalenderAboErzeugenAsync(h.Id));
        (await Mit(u, d => d.KalenderAboAsync(h.Id, token))).ShouldBeNull();                             // alter Link gilt nicht mehr
        await Mit(u, d => d.KalenderAboBeendenAsync(h.Id));
        (await Mit(u, d => d.LadenAsync(h.Id))).KalenderTokenHash.ShouldBeNull();
    }

    [Fact]
    public async Task Datenbank_von_Version_15_bekommt_die_Gespraechsplanung_ohne_Datenverlust()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("mustermann");
        var h = await Mit(u, d => d.AusListeAnlegenAsync(PruefungslisteTests.MehrerePruefungen(), "Vorher", "11B1370-1-VL-MB", null));
        await u.InBereichAsync(async d =>
        {
            var db = d.GetRequiredService<ProfOMatDbContext>();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Teammitglieder\" DROP COLUMN \"Einschraenkungen\"; ALTER TABLE \"Teammitglieder\" DROP COLUMN \"TerminMitgeteilt\"; "
                                                 + "ALTER TABLE \"HausarbeitTeams\" DROP COLUMN \"Termin\"; ALTER TABLE \"Hausarbeiten\" DROP COLUMN \"GespraechMinuten\"; "
                                                 + "ALTER TABLE \"Hausarbeiten\" DROP COLUMN \"GespraechTageText\"; ALTER TABLE \"Hausarbeiten\" DROP COLUMN \"TagBeginnMinuten\"; "
                                                 + "ALTER TABLE \"Hausarbeiten\" DROP COLUMN \"TagEndeMinuten\"; ALTER TABLE \"Hausarbeiten\" DROP COLUMN \"GespraechOrt\"; "
                                                 + "ALTER TABLE \"Hausarbeiten\" DROP COLUMN \"TerminBetreff\"; ALTER TABLE \"Hausarbeiten\" DROP COLUMN \"TerminVorlage\"; "
                                                 + "ALTER TABLE \"Hausarbeiten\" DROP COLUMN \"KalenderTokenHash\"; ALTER TABLE \"Hausarbeiten\" DROP COLUMN \"BestaetigungBetreff\"; "
                                                 + "ALTER TABLE \"Hausarbeiten\" DROP COLUMN \"BestaetigungVorlage\";");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Schemainfo\" SET \"Version\" = 15;");
        });

        await DatenbankInitialisierung.InitialisierenAsync(u.Dienste, TestContext.Current.CancellationToken);

        var geladen = await Mit(u, d => d.LadenAsync(h.Id));
        (geladen.Titel, geladen.GespraechMinuten, geladen.TagBeginnMinuten, geladen.TagEndeMinuten).ShouldBe(("Vorher", 30, 480, 1080));
        await Mit(u, d => d.GespraecheEinstellenAsync(h.Id, 45, [Mo], new TimeOnly(9, 0), new TimeOnly(12, 0), null));
        (await u.InBereichAsync(d => d.GetRequiredService<ProfOMatDbContext>().Set<Schemainfo>().SingleAsync())).Version.ShouldBe(ProfOMatDbContext.Schemaversion);
    }
}
