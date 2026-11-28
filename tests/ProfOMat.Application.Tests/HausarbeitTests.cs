using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Betrieb;
using ProfOMat.Application.Hausarbeiten;
using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Hausarbeiten;
using ProfOMat.Domain.Klausuren;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Application.Tests;

public sealed class HausarbeitFachTests
{
    private const string Themenliste = """
        Themenideen
        Die Liste wird ständig erweitert.

        1. Mathe-Visualisierung und -Demonstration Software
        Schreiben Sie eine grafische Python-Anwendung,
        die Mathematik erklärt.

        2. Software für einen automatischen Fensteröffner Hardware
        Im Labor für Mechatronik …

        16. Zahnrad-Generator Software
        Aus Modul und Zähnezahl entsteht ein Zahnradpaar.
        """;

    private static Hausarbeit Mit(params (string Matrikel, string Nachname, string Vorname)[] personen)
    {
        var h = new Hausarbeit(Guid.NewGuid(), "Informatik für Maschinenbau", "11B1800-1-VL-MB", "Prof. Liebler", DateTimeOffset.UnixEpoch);
        h.Abgleichen(personen.Select((p, i) => new Listenzeile(i + 1, p.Matrikel, p.Nachname, p.Vorname, $"{p.Vorname.ToLowerInvariant()}@hs-osnabrueck.de")).ToList());
        return h;
    }

    [Fact]
    public void Themenliste_aus_dem_Webseitentext_Titel_Art_Beschreibung_und_Aktualisierung()
    {
        var h = Mit();
        h.ThemenEinlesen(Themenliste).ShouldBe(new ThemenImport(3, 0));
        var t = h.ThemenSortiert.ToList();
        t.Select(x => (x.Nr, x.Titel, x.Art)).ShouldBe([
            (1, "Mathe-Visualisierung und -Demonstration", Themenart.Software),
            (2, "Software für einen automatischen Fensteröffner", Themenart.Hardware),
            (16, "Zahnrad-Generator", Themenart.Software)]);
        t[0].Beschreibung.ShouldBe("Schreiben Sie eine grafische Python-Anwendung, die Mathematik erklärt.");

        h.ThemenEinlesen(Themenliste.Replace("Zahnradpaar.", "Zahnradpaar als SVG.", StringComparison.Ordinal)).ShouldBe(new ThemenImport(0, 1));
        Should.Throw<FachlicherFehler>(() => h.ThemenEinlesen("nur Text ohne Themen")).Message.ShouldContain("Keine Themen erkannt");
    }

    [Fact]
    public void Teams_Regeln_Person_nur_in_einem_Team_hoechstens_drei_Wuensche()
    {
        var h = Mit(("1000001", "Alpha", "Anna"), ("1000002", "Beta", "Bert"), ("1000003", "Gamma", "Gina"));
        h.ThemenEinlesen(Themenliste);
        var (a, b, c) = (h.PersonenSortiert.ElementAt(0).Id, h.PersonenSortiert.ElementAt(1).Id, h.PersonenSortiert.ElementAt(2).Id);
        var themen = h.ThemenSortiert.Select(t => t.Id).ToList();

        var team = h.TeamSpeichern(null, [a, b], b, [themen[2], themen[0]], null);
        team.Nr.ShouldBe(1);
        team.SprecherId.ShouldBe(b);
        team.Wuensche.ShouldBe([themen[2], themen[0]]);
        h.OhneTeam.Select(p => p.Id).ShouldBe([c]);

        Should.Throw<FachlicherFehler>(() => h.TeamSpeichern(null, [b, c], null, [], null)).Message.ShouldContain("schon in Team 1");
        Should.Throw<FachlicherFehler>(() => h.TeamSpeichern(null, [c], null, [themen[0], themen[0]], null)).Message.ShouldContain("nur einmal");
        Should.Throw<FachlicherFehler>(() => h.TeamSpeichern(null, [], null, [], null));
        h.KannThemaEntfernen(themen[0]).Erlaubt.ShouldBeFalse();     // gewünscht
        h.KannThemaEntfernen(themen[1]).Erlaubt.ShouldBeTrue();
    }

    [Fact]
    public void Vorschlag_nach_Wuenschen_vergibt_jedes_Thema_einmal_und_laesst_Zuteilungen_stehen()
    {
        var h = Mit(("1000001", "A", "A"), ("1000002", "B", "B"), ("1000003", "C", "C"), ("1000004", "D", "D"));
        h.ThemenEinlesen(Themenliste);
        var p = h.PersonenSortiert.Select(x => x.Id).ToList();
        var t = h.ThemenSortiert.Select(x => x.Id).ToList();
        var t1 = h.TeamSpeichern(null, [p[0]], null, [t[0], t[1]], null);
        var t2 = h.TeamSpeichern(null, [p[1]], null, [t[0]], null);           // nur ein Wunsch: kommt zuerst dran
        var t3 = h.TeamSpeichern(null, [p[2]], null, [t[0], t[1], t[2]], null);
        var t4 = h.TeamSpeichern(null, [p[3]], null, [t[1]], null);
        h.ThemaZuteilen(t4.Id, t[2]);                                           // von Hand, bleibt

        h.NachWuenschenZuteilen().ShouldBe(2);
        t2.ThemaId.ShouldBe(t[0]);
        t1.ThemaId.ShouldBe(t[1]);
        t3.ThemaId.ShouldBeNull();       // alle Wünsche vergeben
        t4.ThemaId.ShouldBe(t[2]);
    }

    [Fact]
    public void Neues_Thema_setzt_Benachrichtigung_und_Bestaetigung_zurueck_Frist_macht_ueberfaellig()
    {
        var h = Mit(("1000001", "A", "A"), ("1000002", "B", "B"));
        h.ThemenEinlesen(Themenliste);
        var t = h.ThemenSortiert.Select(x => x.Id).ToList();
        var team = h.TeamSpeichern(null, h.PersonenSortiert.Select(x => x.Id).ToList(), null, [], null);
        var m = team.Mitglieder[0];
        var jetzt = DateTimeOffset.UnixEpoch.AddDays(10);
        h.Stand(team, m, jetzt).ShouldBe(Bestaetigungsstand.OhneThema);

        h.ThemaZuteilen(team.Id, t[0]);
        h.ZuBenachrichtigen().Count.ShouldBe(2);
        h.Stand(team, m, jetzt).ShouldBe(Bestaetigungsstand.NichtBenachrichtigt);
        Should.Throw<FachlicherFehler>(() => h.Bestaetigen(m.Id, jetzt));    // noch keine Mail

        h.Benachrichtigen(m.Id, "hash", jetzt);
        h.Aendern(h.Titel, h.Pruefungsnummer, jetzt.AddDays(5));
        h.Stand(team, m, jetzt).ShouldBe(Bestaetigungsstand.Wartet);
        h.Stand(team, m, jetzt.AddDays(6)).ShouldBe(Bestaetigungsstand.Ueberfaellig);
        h.Bestaetigen(m.Id, jetzt.AddDays(1));
        h.Stand(team, m, jetzt.AddDays(6)).ShouldBe(Bestaetigungsstand.Bestaetigt);

        h.ThemaZuteilen(team.Id, t[1]).ShouldBeTrue();
        (m.Benachrichtigt, m.TokenHash, m.Bestaetigt).ShouldBe((null, null, null));
    }

    [Fact]
    public void Abgleich_markiert_Abmeldungen_von_Hand_Ergaenzte_bleiben()
    {
        var h = Mit(("1000001", "A", "A"), ("1000002", "B", "B"));
        h.PersonHinzufuegen("1000009", "Nach", "Zügler", null).VonHand.ShouldBeTrue();
        Should.Throw<FachlicherFehler>(() => h.PersonHinzufuegen("1000001", "X", "Y", null)).Message.ShouldContain("schon");

        var e = h.Abgleichen([new Listenzeile(1, "1000001", "A", "A", null), new Listenzeile(2, "1000009", "Nach", "Zügler", "nz@hs-osnabrueck.de")]);
        e.ShouldBe(new ImportErgebnis(0, 2, 1, 1));
        h.Personen.Single(p => p.Matrikelnummer == "1000002").Angemeldet.ShouldBeFalse();
        var nz = h.Personen.Single(p => p.Matrikelnummer == "1000009");
        (nz.Angemeldet, nz.VonHand).ShouldBe((true, false));
    }
}

public sealed class EmlTests
{
    [Fact]
    public void Eml_ist_ungesendeter_Entwurf_mit_kodiertem_Betreff_und_Text()
    {
        var eml = Encoding.ASCII.GetString(Eml.Erzeugen("anna.alpha@hs-osnabrueck.de", "Anna Älpha", "Hausarbeit: Ihr Thema – bitte bestätigen", "Grüße\nZeile 2"));
        eml.ShouldStartWith("X-Unsent: 1\r\n");
        eml.ShouldContain("To: =?utf-8?B?");
        eml.ShouldContain("<anna.alpha@hs-osnabrueck.de>");
        eml.ShouldNotContain("From:");
        var teile = eml.Split("\r\n\r\n", 2);
        Encoding.UTF8.GetString(Convert.FromBase64String(teile[1].Replace("\r\n", "", StringComparison.Ordinal))).ShouldBe("Grüße\r\nZeile 2");
        var betreff = teile[0].Split("\r\n").Single(z => z.StartsWith("Subject: ", StringComparison.Ordinal))["Subject: ".Length..];
        string.Concat(betreff.Split(' ').Select(w => Encoding.UTF8.GetString(Convert.FromBase64String(w[10..^2])))).ShouldBe("Hausarbeit: Ihr Thema – bitte bestätigen");
        Should.Throw<FachlicherFehler>(() => Eml.Erzeugen("a@b.de\r\nBcc: x@y.de", null, "x", "y"));
        Eml.Dateiname("Hausarbeit Informatik: Ihr Thema", "Anna Älpha").ShouldBe("Hausarbeit Informatik - Anna Älpha.eml");
    }
}

public sealed class SignaturTests
{
    /// <summary>So ähnlich speichert Outlook eine Signatur (Word-HTML, Windows-1252, VML mit Fallback-Bild, Bilder im Ordner „…-Dateien“).</summary>
    private const string WordHtml = """
        <html xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office">
        <head><meta http-equiv=Content-Type content="text/html; charset=windows-1252">
        <style><!--
        v\:* {behavior:url(#default#VML);}
        @font-face {font-family:"Cambria Math"; panose-1:2 4 5 3 5 4 6 3 2 4;}
        p.MsoNormal {margin:0cm; font-size:11.0pt; font-family:"Calibri",sans-serif;}
        @import url(https://tracker.example.invalid/x.css);
        --></style>
        <script>alert(1)</script>
        </head>
        <body lang=DE onload="boese()">
        <p class=MsoNormal><b><span style='color:#00A0E0'>Prof. Dr. Erika Mustermann</span></b><o:p></o:p></p>
        <!--[if gte vml 1]><v:shape id="Bild_1" o:spid="_x0000_i1025"><v:imagedata src="Beispiel-Dateien/image001.png"/></v:shape><![endif]--><![if !vml]><img width=100 height=40 src="Beispiel-Dateien/image001.png" v:shapes="Bild_1"><![endif]>
        <p class=MsoNormal>mail: <a href="mailto:e.mustermann@example.invalid">e.mustermann@example.invalid</a><br>
        <a href="javascript:alert(2)">böse</a> <a href="https://www.hs-osnabrueck.de/">Web</a></p>
        <img src="https://tracker.example.invalid/pixel.gif">
        <iframe src="https://example.invalid"></iframe>
        </body></html>
        """;

    [Fact]
    public void Outlook_Signatur_wird_bereinigt_und_Bilder_eingebettet()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var e = Signaturaufbereitung.Aufbereiten(WordHtml, [new HochgeladeneDatei("image001.png", png)]);
        e.Html.ShouldContain("Prof. Dr. Erika Mustermann");
        e.Html.ShouldContain("p.MsoNormal");
        e.Html.ShouldNotContain("font-face");
        e.Html.ShouldContain("src=\"cid:sig1@profomat\"");
        e.Html.ShouldContain("href=\"https://www.hs-osnabrueck.de/\"");
        e.Html.ShouldContain("mailto:");
        foreach (var verboten in new[] { "script", "alert", "onload", "iframe", "tracker", "v:shape", "o:p", "javascript", "<!--", "behavior", "@import" })
        {
            e.Html.ShouldNotContain(verboten, Case.Insensitive);
        }

        e.Bilder.Single().ShouldBe(("sig1@profomat", "image001.png", "image/png", png));
        e.Hinweise.Single().ShouldContain("pixel.gif");
        e.Text.ShouldStartWith("Prof. Dr. Erika Mustermann\n");
        e.Text.ShouldContain("mail: e.mustermann@example.invalid");
    }

    [Fact]
    public void Direkt_eingebettete_data_Bilder_werden_Anhaenge_doppelte_nur_einmal()
    {
        var png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 9, 9]);
        var html = $"<body><p>Logo <img src=\"data:image/png;base64, {png}\"> und nochmal <img src=\"data:image/png;base64,{png}\"></p>"
                   + "<img src=\"data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=\"></body>";
        var e = Signaturaufbereitung.Aufbereiten(html, []);
        var bild = e.Bilder.Single();
        (bild.ContentId, bild.Dateiname, bild.Typ).ShouldBe(("sig1@profomat", "bild1.png", "image/png"));
        bild.Inhalt.ShouldBe(Convert.FromBase64String(png));
        e.Html.Split("cid:sig1@profomat").Length.ShouldBe(3);
        e.Html.ShouldNotContain("data:");
        e.Hinweise.Single().ShouldContain("svg");
        e.Hinweise.Single().Length.ShouldBeLessThan(140);
    }

    [Fact]
    public void Mail_mit_Signatur_hat_Text_und_HTML_Fassung_Link_anklickbar_Bild_per_cid()
    {
        var s = new Mailsignatur(Guid.NewGuid());
        s.Setzen("<p>Signatur <img src=\"cid:sig1@profomat\"></p>", "Signatur", [("sig1@profomat", "logo.png", "image/png", [1, 2, 3])], DateTimeOffset.UnixEpoch);
        var eml = Encoding.ASCII.GetString(Eml.Erzeugen("a@hs-osnabrueck.de", null, "Betreff", "Hallo <Team>\nLink: https://x.example/bestaetigen/1/abc", s));
        eml.ShouldContain("Content-Type: multipart/alternative;");
        eml.ShouldContain("Content-Type: multipart/related;");
        eml.ShouldContain("Content-ID: <sig1@profomat>");
        var html = Eml.Html("Hallo <Team>\nLink: https://x.example/bestaetigen/1/abc", s.Html);
        html.ShouldContain("Hallo &lt;Team&gt;<br>");
        html.ShouldContain("<a href=\"https://x.example/bestaetigen/1/abc\">");
        html.ShouldEndWith("<p>Signatur <img src=\"cid:sig1@profomat\"></p>\n</body></html>");
    }
}

public sealed class HausarbeitTests
{
    private const string Themen = "1. Mathe Software\nBeschreibung eins\n2. Roboterarm Hardware\nBeschreibung zwei\n";

    private static async Task<Hausarbeit> AnlegenAsync(TestUmgebung u)
    {
        await u.AlsAsync("mustermann");
        var h = await u.InBereichAsync(d => d.GetRequiredService<HausarbeitDienst>().AusListeAnlegenAsync(
            PruefungslisteTests.MehrerePruefungen(), null, "11B1370-1-VL-MB", u.Uhr.GetUtcNow().AddDays(5)));
        await u.InBereichAsync(d => d.GetRequiredService<HausarbeitDienst>().ThemenEinlesenAsync(h.Id, Themen));
        return h;
    }

    private static Task<T> Mit<T>(TestUmgebung u, Func<HausarbeitDienst, Task<T>> arbeit) => u.InBereichAsync(d => arbeit(d.GetRequiredService<HausarbeitDienst>()));

    private static Task Mit(TestUmgebung u, Func<HausarbeitDienst, Task> arbeit) => u.InBereichAsync(d => arbeit(d.GetRequiredService<HausarbeitDienst>()));

    [Fact]
    public async Task Ablauf_Team_Zuteilung_Entwuerfe_in_Outlook_Bestaetigung_per_persoenlichem_Link()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var h = await Mit(u, async d => await d.LadenAsync((await AnlegenAsync(u)).Id));
        h.Titel.ShouldBe("Regelungstechnik");
        h.Personen.Count.ShouldBe(2);                                  // nur die gewählte Prüfung
        var personen = h.PersonenSortiert.Select(p => p.Id).ToList();
        var themen = h.ThemenSortiert.Select(t => t.Id).ToList();

        var team = await Mit(u, d => d.TeamSpeichernAsync(h.Id, null, personen, personen[1], [themen[1], themen[0]], "per Mail"));
        (await Mit(u, d => d.NachWuenschenZuteilenAsync(h.Id))).ShouldBe(1);
        (await Mit(u, d => d.BestaetigungsmailsErstellenAsync(h.Id))).ShouldBe(2);
        (await Mit(u, d => d.BestaetigungsmailsErstellenAsync(h.Id))).ShouldBe(0);   // nicht doppelt

        // Entwürfe gehören Mustermann, ProfOMat verschickt nichts selbst
        var entwuerfe = await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().MeineAsync(false));
        entwuerfe.Count.ShouldBe(2);
        entwuerfe.ShouldAllBe(e => e.Anlass == HausarbeitDienst.AnlassBestaetigung);
        var anna = entwuerfe.Single(e => e.AnName == "Anna Alpha");
        anna.Text.ShouldContain("2. Roboterarm (Hardware)");
        anna.Text.ShouldContain("mit Gina Gamma");
        var link = anna.Text.Split('\n').Single(z => z.Contains("/hausarbeit/bestaetigen/", StringComparison.Ordinal)).Trim();
        var teile = link.Split('/');
        var (mitgliedId, token) = (Guid.Parse(teile[^2]), teile[^1]);
        (await u.InBereichAsync(d => d.GetRequiredService<Postausgang>().LetzteAsync(KontoDienstVerwaltung(), 50))).ShouldBeEmpty();

        var (eml, name) = await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().EmlAsync(anna.Id));
        Encoding.ASCII.GetString(eml).ShouldStartWith("X-Unsent: 1");
        name.ShouldEndWith(".eml");
        (await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().OffeneAsync())).ShouldBe(1);

        // Studierende ohne Anmeldung: falscher Link zeigt nichts, richtiger Link bestätigt genau diese Person
        u.Benutzer.Aktuell = Benutzer.Niemand;
        (await Mit(u, d => d.BestaetigungAnsichtAsync(mitgliedId, token + "x"))).ShouldBeNull();
        (await Mit(u, d => d.BestaetigungAnsichtAsync(Guid.NewGuid(), token))).ShouldBeNull();
        var ansicht = await Mit(u, d => d.BestaetigungAnsichtAsync(mitgliedId, token));
        ansicht!.Name.ShouldBe("Anna Alpha");
        ansicht.Bestaetigt.ShouldBeNull();
        (await Mit(u, d => d.BestaetigenAsync(mitgliedId, token)))!.Bestaetigt.ShouldNotBeNull();

        await u.AlsAsync("mustermann");
        h = await Mit(u, d => d.LadenAsync(h.Id));
        team = h.TeamMit(team.Id);
        team.Mitglieder.Select(m => h.Stand(team, m, u.Uhr.GetUtcNow())).Order().ShouldBe([Bestaetigungsstand.Wartet, Bestaetigungsstand.Bestaetigt]);

        // anderes Thema: alter Link gilt nicht mehr, neue Mails nötig
        await Mit(u, d => d.ThemaZuteilenAsync(h.Id, team.Id, themen[0]));
        u.Benutzer.Aktuell = Benutzer.Niemand;
        (await Mit(u, d => d.BestaetigungAnsichtAsync(mitgliedId, token))).ShouldBeNull();
        await u.AlsAsync("mustermann");
        (await Mit(u, d => d.BestaetigungsmailsErstellenAsync(h.Id))).ShouldBe(2);
        (await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().MeineAsync(false))).Count.ShouldBe(2);   // alte Entwürfe ersetzt
    }

    private static ProfOMat.Application.Benutzer KontoDienstVerwaltung() => new("verwaltung", "Vera", Guid.NewGuid(), [ProfOMat.Domain.Konten.Rolle.Systemverwaltung]);

    [Fact]
    public async Task Rechte_nur_Betreuende_und_Systemverwaltung_Entwuerfe_nur_fuer_die_Eigentuemer()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var h = await AnlegenAsync(u);
        var personen = (await Mit(u, d => d.LadenAsync(h.Id))).Personen.Select(p => p.Id).ToList();
        var team = await Mit(u, d => d.TeamSpeichernAsync(h.Id, null, personen, null, [], null));
        var themaId = (await Mit(u, d => d.LadenAsync(h.Id))).ThemenSortiert.First().Id;
        await Mit(u, d => d.ThemaZuteilenAsync(h.Id, team.Id, themaId));
        await Mit(u, d => d.BestaetigungsmailsErstellenAsync(h.Id));
        var entwurf = (await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().MeineAsync(false)))[0];

        await u.AlsAsync("liebler");
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.LadenAsync(h.Id)))).Message.ShouldContain("betreut");
        (await Mit(u, d => d.ListeAsync())).ShouldBeEmpty();
        await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().EmlAsync(entwurf.Id)));
        (await u.InBereichAsync(d => d.GetRequiredService<Mailentwuerfe>().MeineAsync(true))).ShouldBeEmpty();

        await u.AlsAsync("sekretariat");
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.ListeAsync()))).Message.ShouldContain("Prüfende");

        await u.AlsAsync("verwaltung");
        (await Mit(u, d => d.LadenAsync(h.Id))).Id.ShouldBe(h.Id);
    }

    [Fact]
    public async Task Datenbank_von_Version_9_bekommt_die_neuen_Tabellen_ohne_Datenverlust()
    {
        await using var u = await TestUmgebung.StartenAsync(sqlite: true);
        var konten = await u.InBereichAsync(async d =>
        {
            var db = d.GetRequiredService<ProfOMatDbContext>();
            // Stand von Version 9 nachstellen: neue Tabellen fehlen
            foreach (var t in new[] { "Teammitglieder", "HausarbeitTeams", "HausarbeitPersonen", "HausarbeitThemen", "Hausarbeiten", "Mailentwuerfe", "MailsignaturBilder", "Mailsignaturen" })
            {
                var sql = "DROP TABLE \"" + t + "\";";
                await db.Database.ExecuteSqlRawAsync(sql);
            }

            await db.Database.ExecuteSqlRawAsync("UPDATE \"Schemainfo\" SET \"Version\" = 9;");
            return await db.Set<ProfOMat.Domain.Konten.Konto>().CountAsync();
        });

        await SqliteAltbestand.InitialisierenAsync(u.Dienste, TestContext.Current.CancellationToken);

        await AnlegenAsync(u);
        (await Mit(u, d => d.ListeAsync())).Count.ShouldBe(1);
        (await u.InBereichAsync(d => d.GetRequiredService<ProfOMatDbContext>().Set<ProfOMat.Domain.Konten.Konto>().CountAsync())).ShouldBe(konten);
        (await u.InBereichAsync(d => d.GetRequiredService<ProfOMatDbContext>().Set<Schemainfo>().SingleAsync())).Version.ShouldBe(ProfOMatDbContext.Schemaversion);
    }
}
