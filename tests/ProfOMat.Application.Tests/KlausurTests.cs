using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Klausuren;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Klausuren;

namespace ProfOMat.Application.Tests;

public sealed class PruefungslisteTests
{
    private const string Kopf = "pruefer,exam_name,pruefungsname,pruefungsnummer,row,matriculation_number1,first_name1,first_name,hochschulmail";

    internal static byte[] Liste(params (string Matrikel, string Nachname, string Vorname)[] personen)
    {
        var sb = new StringBuilder(Kopf).Append('\n');
        var i = 1;
        foreach (var (m, n, v) in personen)
        {
            sb.Append($"Prof. Dr. Ing. Klaus Liebler,Prüfungsleistung,Software Engineering für Elektrotechnik (Vorlesung),11B0399-1-VL-EuI,{i++},{m},{n},{v},{v.ToLowerInvariant()}.{n.ToLowerInvariant()}@hs-osnabrueck.de\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Fact]
    public void Liest_das_Format_des_Hochschulsystems_first_name1_ist_der_Nachname()
    {
        var l = PruefungslisteLeser.Lesen(Liste(("1103876", "Schäfer", "Martin"), ("1044678", "Backmann", "Jonas")));
        l.Pruefungsnummer.ShouldBe("11B0399-1-VL-EuI");
        l.Pruefungsname.ShouldBe("Software Engineering für Elektrotechnik (Vorlesung)");
        l.Pruefer.ShouldBe("Prof. Dr. Ing. Klaus Liebler");
        l.Zeilen[0].ShouldBe(new Listenzeile(1, "1103876", "Schäfer", "Martin", "martin.schäfer@hs-osnabrueck.de"));
    }

    [Fact]
    public void Semikolon_Anfuehrungszeichen_und_Windows1252_werden_erkannt()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = "Matrikelnummer;Nachname;Vorname\r\n1234567;\"Müller; Lüdenscheid\";Jörg\r\n";
        var l = PruefungslisteLeser.Lesen(Encoding.GetEncoding(1252).GetBytes(text));
        l.Zeilen.Single().Nachname.ShouldBe("Müller; Lüdenscheid");
        l.Zeilen.Single().Vorname.ShouldBe("Jörg");
    }

    [Fact]
    public void Ungueltige_Matrikelnummern_werden_gemeldet_mehrere_Pruefungen_abgelehnt()
    {
        var l = PruefungslisteLeser.Lesen(Encoding.UTF8.GetBytes($"{Kopf}\nP,E,N,X,1,12ab,A,B,\nP,E,N,X,2,1234567,C,D,\n"));
        l.Zeilen.Count.ShouldBe(1);
        l.Hinweise.Single().ShouldContain("12ab");
        Should.Throw<FachlicherFehler>(() => PruefungslisteLeser.Lesen(Encoding.UTF8.GetBytes($"{Kopf}\nP,E,N,X,1,1234567,A,B,\nP,E,N,Y,2,7654321,C,D,\n")))
            .Message.ShouldContain("mehrere Prüfungen");
    }
}

public sealed class KlausurTests
{
    private static async Task<Guid> AnlegenAsync(TestUmgebung u, int anzahl)
    {
        await u.AlsAsync("mustermann");
        var personen = Enumerable.Range(0, anzahl).Select(i => ($"{1100000 + i}", $"Name{i:000}", $"Vorname{i}")).ToArray();
        var (k, ergebnis, _) = await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>()
            .AusListeAnlegenAsync(PruefungslisteTests.Liste(personen), null, "SE", u.Uhr.GetUtcNow().AddDays(20)));
        ergebnis.Neu.ShouldBe(anzahl);
        return k.Id;
    }

    [Fact]
    public async Task Import_vergibt_eindeutige_pruefbare_IDs_und_Abgleich_meldet_Abmeldungen()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var id = await AnlegenAsync(u, 50);
        var a = await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().AnsichtAsync(id));
        a.Klausur.Titel.ShouldBe("Software Engineering für Elektrotechnik (Vorlesung)");
        a.Klausur.Teilnahmen.Select(t => t.Code).Distinct().Count().ShouldBe(50);
        a.Klausur.Teilnahmen.ShouldAllBe(t => t.Code.StartsWith("SE-") && Klausur.CodeGueltig(t.Code));
        var code = a.Klausur.Teilnahmen.Single(t => t.Matrikelnummer == "1100000").Code;
        a.Klausur.NachCode(code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal))!.Code.ShouldBe(code);

        // neue Liste: Person 0 fehlt, eine neue ist dazugekommen
        var neu = Enumerable.Range(1, 49).Select(i => ($"{1100000 + i}", $"Name{i:000}", $"Vorname{i}")).Append(("1199999", "Neu", "Nora")).ToArray();
        var (ergebnis, _) = await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().ListeAbgleichenAsync(id, PruefungslisteTests.Liste(neu)));
        ergebnis.ShouldBe(new ImportErgebnis(1, 0, 1, 0));
        var b = await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().AnsichtAsync(id));
        b.Klausur.Teilnahmen.Count.ShouldBe(51);
        b.Klausur.Teilnahmen.Single(t => t.Matrikelnummer == "1100000").Code.ShouldBe(code);   // bleibt, nur abgemeldet
        b.Klausur.Angemeldete.Count().ShouldBe(50);
    }

    [Fact]
    public async Task Verteilung_braucht_genug_Plaetze_und_lastet_Raeume_gleichmaessig_aus()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var id = await AnlegenAsync(u, 57);
        var raeume = await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().RaeumeAsync());
        var klein = raeume.Single(r => r.Kuerzel == "SI 0012");    // 40
        var zweiter = raeume.Single(r => r.Kuerzel == "SI 0110");  // 32
        await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().RaeumeWaehlenAsync(id, [klein.Id]));
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().VerteilenAsync(id, false))))
            .Message.ShouldContain("57 Teilnehmende, 40 Plätze");

        await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().RaeumeWaehlenAsync(id, [klein.Id, zweiter.Id]));
        await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().VerteilenAsync(id, false));
        var a = await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().AnsichtAsync(id));
        var inKlein = a.Klausur.Angemeldete.Count(t => t.RaumId == klein.Id);
        var inZweitem = a.Klausur.Angemeldete.Count(t => t.RaumId == zweiter.Id);
        (inKlein + inZweitem).ShouldBe(57);
        inKlein.ShouldBe(32);   // 57 · 40/72 = 31,7 → 31 + Rest
        inZweitem.ShouldBe(25);
        a.Klausur.Angemeldete.Where(t => t.RaumId == klein.Id).Select(t => t.Platz).Distinct().Count().ShouldBe(inKlein);
        // alphabetisch: der erste Raum hat die vorderen Namen
        a.Klausur.Angemeldete.Single(t => t.Nachname == "Name000").RaumId.ShouldBe(klein.Id);
        a.Klausur.Angemeldete.Single(t => t.Nachname == "Name056").RaumId.ShouldBe(zweiter.Id);

        var serienbrief = Encoding.UTF8.GetString((await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().SerienbriefAsync(id))).Inhalt);
        serienbrief.ShouldStartWith("﻿KlausurID;Nachname;Vorname;Matrikelnummer;EMail;Raum;");
        serienbrief.ShouldContain(";SI 0012;Seminarraum;1;");
        (await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().SitzlistenAsync(id))).Inhalt.Length.ShouldBeGreaterThan(1000);
        (await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().AushangAsync(id))).Inhalt.Length.ShouldBeGreaterThan(1000);
    }

    [Fact]
    public async Task Punkte_Noten_und_Statistik()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var id = await AnlegenAsync(u, 6);
        await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().AufgabenSetzenAsync(id,
            [("Aufgabe 1", 40m, false), ("Aufgabe 2", 60m, false), ("Sonderpunkte", 5m, true)]));
        var a = await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().AnsichtAsync(id));
        a.Klausur.Hoechstpunktzahl.ShouldBe(100m);
        var auf = a.Klausur.AufgabenSortiert.ToList();
        var t = a.Klausur.Angemeldete.OrderBy(x => x.Nachname).ToList();
        async Task Setzen(int person, decimal a1, decimal a2, decimal sonder = 0)
        {
            await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().PunkteSetzenAsync(id, t[person].Id, auf[0].Id, a1));
            await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().PunkteSetzenAsync(id, t[person].Id, auf[1].Id, a2));
            await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().PunkteSetzenAsync(id, t[person].Id, auf[2].Id, sonder));
        }

        await Setzen(0, 40, 54.5m);         // 94,5 → 1,0
        await Setzen(1, 30, 19.5m);         // 49,5 → 4,0
        await Setzen(2, 30, 19);            // 49   → 5,0
        await Setzen(3, 30, 15, 4.5m);      // 49,5 → 4,0 (mit Sonderpunkten)
        await Setzen(4, 10, 5);             // 15   → 5,0, nicht aktiv (≤ 20)
        await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().RestNichtErschienenAsync(id));   // Person 5

        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().PunkteSetzenAsync(id, t[0].Id, auf[0].Id, 41))))
            .Message.ShouldContain("zwischen 0 und 40");

        var w = (await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().AnsichtAsync(id))).Auswertung;
        w.Anwesend.ShouldBe(5);
        w.Aktiv.ShouldBe(4);
        w.Durchfallquote.ShouldBe(0.25m);
        w.Durchschnittsnote.ShouldBe(3.5m);   // (1,0 + 4,0 + 5,0 + 4,0) / 4
        w.Maximalpunktzahl.ShouldBe(94.5m);
        w.Minimalpunktzahl.ShouldBe(15m);
        w.AnzahlGanzeNote[1].ShouldBe(1);
        w.AnzahlGanzeNote[4].ShouldBe(2);
        w.AnzahlGanzeNote[5].ShouldBe(1);
        w.Vollstaendig.ShouldBeTrue();
        w.Zeilen.Single(z => z.Teilnahme.Id == t[5].Id).Note.ShouldBeNull();

        var pdf = await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().ErgebnisPdfAsync(id));
        pdf.Inhalt.AsSpan(0, 4).ToArray().ShouldBe("%PDF"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(Path.GetTempPath(), "profomat-test-ergebnis.pdf"), pdf.Inhalt, TestContext.Current.CancellationToken);
        var xlsx = await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().ErgebnisXlsxAsync(id));
        xlsx.Inhalt.AsSpan(0, 2).ToArray().ShouldBe("PK"u8.ToArray());
        var noten = Encoding.UTF8.GetString((await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().NotenCsvAsync(id))).Inhalt);
        noten.ShouldContain($"{t[0].Matrikelnummer};{t[0].Nachname};{t[0].Vorname};1,0;;94,5");
        noten.ShouldContain("nicht erschienen");
    }

    [Fact]
    public async Task Notenschluessel_editierbar_und_umrechenbar()
    {
        Notenschluessel.NoteFuer(Notenschluessel.Standard, 49.4m).ShouldBe(5.0m);
        Notenschluessel.NoteFuer(Notenschluessel.Standard, 49.5m).ShouldBe(4.0m);
        Notenschluessel.NoteFuer(Notenschluessel.Standard, 94.5m).ShouldBe(1.0m);
        var auf60 = Notenschluessel.Umrechnen(Notenschluessel.Standard, 100m, 60m);
        auf60.Single(s => s.Note == 4.0m).Mindestpunkte.ShouldBe(29.5m);   // 29,7 → 29,5
        Notenschluessel.Beanstandung([new(0, 5.0m), new(50, 4.0m), new(60, 4.0m)])!.ShouldContain("bessere Note");

        await using var u = await TestUmgebung.StartenAsync();
        var id = await AnlegenAsync(u, 2);
        await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().NotenschluesselSetzenAsync(id, auf60));
        (await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().AnsichtAsync(id))).Klausur.Notenstufen.ShouldBe(auf60);
    }

    [Fact]
    public async Task Fremde_sehen_die_Klausur_nicht()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var id = await AnlegenAsync(u, 2);
        await u.AlsAsync("probst");
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().AnsichtAsync(id))))
            .Message.ShouldContain("verantwortet");
        (await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().ListeAsync())).ShouldBeEmpty();
        await u.AlsAsync("verwaltung");
        (await u.InBereichAsync(d => d.GetRequiredService<KlausurDienst>().ListeAsync())).Count.ShouldBe(1);
    }
}
