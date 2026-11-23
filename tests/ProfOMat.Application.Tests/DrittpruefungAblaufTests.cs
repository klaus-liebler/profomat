using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Betrieb;
using ProfOMat.Application.Drittpruefung;
using ProfOMat.Application.Katalog;
using ProfOMat.Application.Konten;
using ProfOMat.Application.Signaturen;
using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Drittpruefung;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Katalog;

namespace ProfOMat.Application.Tests;

public sealed class DrittpruefungAblaufTests
{
    private static async Task<Antragsangaben> AngabenAsync(TestUmgebung u, string erstpruefer = "mustermann", string pruefung = "Regelungstechnik")
    {
        var erst = await u.KontoAsync(erstpruefer);
        return await u.InBereichAsync(async d =>
        {
            var k = d.GetRequiredService<KatalogDienst>();
            var et = (await k.StudiengaengeAsync(false)).Single(s => s.Kuerzel == "ET");
            var p = (await k.PruefungenAsync(et.Id, false)).Single(x => x.Titel == pruefung);
            return new Antragsangaben("Mia", "Musterstudentin", "1234567", "mia.musterstudentin@hs-osnabrueck.de", et.Id, p.Id, erst.Id,
                "Nicht montags.\nAm liebsten vormittags.", "Gern Prof. Probst");
        });
    }

    /// <summary>Reicht ein und liefert Antrag-Id und Token (aus der Bestätigungsmail gelesen).</summary>
    private static async Task<(Guid Id, string Token)> EinreichenAsync(TestUmgebung u, Unterschrift? unterschrift = null, string erstpruefer = "mustermann")
    {
        u.Benutzer.Aktuell = Benutzer.Niemand;
        var angaben = await AngabenAsync(u, erstpruefer);
        var entwurf = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().EntwurfAsync(angaben));
        using var studiSchluessel = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>()
            .EinreichenAsync(entwurf, unterschrift ?? Unterschriften.MitSchluessel(studiSchluessel, entwurf.Text), TestUmgebung.Mensch(d)));
        var mail = await LetzteMailAnAsync(u, "mia.musterstudentin@hs-osnabrueck.de");
        var link = mail.Text.Split('\n').Select(z => z.Trim()).First(z => z.Contains("/drittpruefung/status/", StringComparison.Ordinal));
        var teile = link.Split('/');
        return (Guid.Parse(teile[^2]), teile[^1]);
    }

    private static Task<Nachricht> LetzteMailAnAsync(TestUmgebung u, string an) => u.InBereichAsync(async d =>
    {
        var daten = d.GetRequiredService<IDaten>();
        return (await daten.ListeAsync(daten.Menge<Nachricht>().Where(n => n.An == an).OrderByDescending(n => n.Id)))[0];
    });

    private static Task<List<Nachricht>> MailsAsync(TestUmgebung u) => u.InBereichAsync(async d =>
    {
        var daten = d.GetRequiredService<IDaten>();
        return await daten.ListeAsync(daten.Menge<Nachricht>().OrderBy(n => n.Id));
    });

    private static async Task FestlegenAsync(TestUmgebung u, Guid id, string zweit = "probst")
    {
        var zweitKonto = await u.KontoAsync(zweit);
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().FestlegungSpeichernAsync(id,
            new Festlegung(u.Uhr.GetUtcNow().AddDays(14), "SI 0012", zweitKonto.Id, "", null, null)));
    }

    private static async Task SchluesselHinterlegenAsync(TestUmgebung u, ECDsa schluessel)
    {
        var zeit = Signaturinhalt.Kappen(u.Uhr.GetUtcNow());
        var nachweis = KontoDienst.Besitznachweis(u.Benutzer.Aktuell.KontoIdPflicht, zeit);
        await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().SchluesselHinterlegenAsync(zeit, Unterschriften.MitSchluessel(schluessel, nachweis), "Laptop"));
    }

    [Fact]
    public async Task Gesamter_Ablauf_mit_Schluessel_und_Unterschriftsfeld_endet_mit_PDF_beim_Sekretariat()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinreichenAsync(u);

        // Vor der Bestätigung sieht die Erstprüferin nichts und bekommt keine Mail
        (await MailsAsync(u)).ShouldNotContain(n => n.An == "mustermann@demo.invalid");
        var bestaetigt = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().BestaetigenAsync(id, token));
        bestaetigt!.Antrag.Status.ShouldBe(AntragStatus.BeimErstpruefer);
        var anPruefer = await LetzteMailAnAsync(u, "mustermann@demo.invalid");
        anPruefer.Text.ShouldContain($"https://profomat.test/antraege/{id:D}");

        // Erstprüferin: Schlüssel hinterlegen, festlegen, unterschreiben
        await u.AlsAsync("mustermann");
        using var profSchluessel = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SchluesselHinterlegenAsync(u, profSchluessel);
        await FestlegenAsync(u, id);
        var vorlage = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().VorlageAsync(id, Signaturschritt.Erstpruefung));
        vorlage.Text.ShouldContain("Ort: SI 0012");
        vorlage.Text.ShouldContain("Zweitprüfende/r: Prof. Dr. Lena Probst");
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>()
            .ErstpruefungUnterschreibenAsync(id, vorlage.Zeitpunkt, vorlage.Text, Unterschriften.MitSchluessel(profSchluessel, vorlage.Text)));
        (await LetzteMailAnAsync(u, "beispiel@demo.invalid")).Betreff.ShouldStartWith("Bitte bestellen");

        // Studiendekan unterschreibt im Feld
        await u.AlsAsync("beispiel");
        var v2 = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().VorlageAsync(id, Signaturschritt.Studiendekanat));
        v2.VorgaengerHash.ShouldBe(vorlage.Text is var t ? Signaturinhalt.Hash(t) : null);
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().GenehmigenAsync(id, v2.Zeitpunkt, v2.Text, Unterschriften.Gezeichnet()));

        var ansicht = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().AnsichtAsync(id));
        ansicht.Antrag.Status.ShouldBe(AntragStatus.Abgeschlossen);
        ansicht.KetteInOrdnung.ShouldBeTrue(string.Join(" | ", ansicht.Signaturen.SelectMany(s => s.Hinweise)));
        ansicht.Signaturen.Count.ShouldBe(3);

        var pdf = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().PdfAsync(id));
        pdf.ShouldNotBeNull();
        pdf.Inhalt.AsSpan(0, 4).ToArray().ShouldBe("%PDF"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(Path.GetTempPath(), "profomat-test-antrag.pdf"), pdf.Inhalt, TestContext.Current.CancellationToken);

        var anSekretariat = await LetzteMailAnAsync(u, "sekretariat@hs.test");
        anSekretariat.AnhangDokumentId.ShouldBe(pdf.Id);
        anSekretariat.Text.ShouldContain(ansicht.Pruefwert!);
        (await LetzteMailAnAsync(u, "probst@demo.invalid")).AnhangDokumentId.ShouldBe(pdf.Id);

        // Studierende sehen über ihren Link den Abschluss und das PDF
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().PdfFuerStudierendeAsync(id, token))).ShouldNotBeNull();
    }

    [Fact]
    public async Task Postausgang_stellt_zu_und_haengt_das_PDF_an()
    {
        await using var u = await TestUmgebung.StartenAsync();
        _ = await EinreichenAsync(u, Unterschriften.Gezeichnet());
        var zustellung = new MerkendeZustellung();
        var anzahl = await u.InBereichAsync(d => d.GetRequiredService<Postausgang>().ZustellenAsync(zustellung));
        anzahl.ShouldBe(1);
        zustellung.Gesendet.Single().An.ShouldBe("mia.musterstudentin@hs-osnabrueck.de");
        (await MailsAsync(u)).ShouldAllBe(n => n.Versandt != null);
    }

    [Fact]
    public async Task Oeffentliches_Formular_nur_mit_Sicherheitsaufgabe_und_begrenzt_je_Mailadresse()
    {
        await using var u = await TestUmgebung.StartenAsync();
        u.Benutzer.Aktuell = Benutzer.Niemand;
        var angaben = await AngabenAsync(u);
        using var schluessel = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var entwurf = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().EntwurfAsync(angaben));
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>()
            .EinreichenAsync(entwurf, Unterschriften.MitSchluessel(schluessel, entwurf.Text), null)))).Message.ShouldContain("Sicherheitsprüfung");

        for (var i = 0; i < DrittpruefungDienst.MaxUnbestaetigtJeMailUndTag; i++)
        {
            await EinreichenAsync(u);
        }

        var vierter = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().EntwurfAsync(angaben));
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>()
            .EinreichenAsync(vierter, Unterschriften.MitSchluessel(schluessel, vierter.Text), TestUmgebung.Mensch(d))))).Message.ShouldContain("unbestätigte");
    }

    [Fact]
    public async Task Geaenderter_Text_wird_abgelehnt()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var angaben = await AngabenAsync(u);
        var entwurf = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().EntwurfAsync(angaben));
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var gefaelscht = entwurf with { Text = entwurf.Text.Replace("1234567", "7654321", StringComparison.Ordinal) };
        var fehler = await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d =>
            d.GetRequiredService<DrittpruefungDienst>().EinreichenAsync(gefaelscht, Unterschriften.MitSchluessel(k, gefaelscht.Text), TestUmgebung.Mensch(d))));
        fehler.Message.ShouldContain("geändert");

        // Signatur über anderen Text als den angezeigten
        var falsch = Unterschriften.MitSchluessel(k, entwurf.Text + "x");
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d =>
            d.GetRequiredService<DrittpruefungDienst>().EinreichenAsync(entwurf, falsch, TestUmgebung.Mensch(d))))).Message.ShouldContain("passt nicht");
    }

    [Fact]
    public async Task RSA_Schluessel_wird_akzeptiert_kurze_nicht()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var angaben = await AngabenAsync(u);
        var entwurf = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().EntwurfAsync(angaben));
        using var kurz = RSA.Create(1024);
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d =>
            d.GetRequiredService<DrittpruefungDienst>().EinreichenAsync(entwurf, Unterschriften.MitRsa(kurz, entwurf.Text), TestUmgebung.Mensch(d))))).Message.ShouldContain("2048");
        using var gut = RSA.Create(2048);
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().EinreichenAsync(entwurf, Unterschriften.MitRsa(gut, entwurf.Text), TestUmgebung.Mensch(d))))
            .ShouldBe(entwurf.Nummer);
    }

    [Fact]
    public async Task Abgelaufene_Vorlage_wird_abgelehnt()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var angaben = await AngabenAsync(u);
        var entwurf = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().EntwurfAsync(angaben));
        u.Uhr.Advance(TimeSpan.FromMinutes(31));
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d =>
            d.GetRequiredService<DrittpruefungDienst>().EinreichenAsync(entwurf, Unterschriften.Gezeichnet(), TestUmgebung.Mensch(d))))).Message.ShouldContain("abgelaufen");
    }

    [Fact]
    public async Task Nicht_hinterlegter_Schluessel_der_Pruefenden_wird_abgelehnt()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinreichenAsync(u);
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().BestaetigenAsync(id, token));
        await u.AlsAsync("mustermann");
        await FestlegenAsync(u, id);
        var v = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().VorlageAsync(id, Signaturschritt.Erstpruefung));
        using var fremd = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>()
            .ErstpruefungUnterschreibenAsync(id, v.Zeitpunkt, v.Text, Unterschriften.MitSchluessel(fremd, v.Text))))).Message.ShouldContain("nicht hinterlegt");
    }

    [Fact]
    public async Task Rechte_Fremde_Pruefende_sehen_nichts_und_Dekan_bestellt_sich_nicht_selbst()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinreichenAsync(u, Unterschriften.Gezeichnet(), erstpruefer: "beispiel");
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().BestaetigenAsync(id, token));

        await u.AlsAsync("tester");
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().AnsichtAsync(id))))
            .Message.ShouldContain("sehen nur");
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().ListeAsync(AntragFilter.Alle))).ShouldBeEmpty();

        // Der Studiendekan ist selbst Erstprüfer → seine Unterschrift als Studiendekan ist gesperrt, eine andere Person aus dem Studiendekanat darf
        await u.AlsAsync("beispiel");
        await FestlegenAsync(u, id);
        var v = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().VorlageAsync(id, Signaturschritt.Erstpruefung));
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().ErstpruefungUnterschreibenAsync(id, v.Zeitpunkt, v.Text, Unterschriften.Gezeichnet()));
        var aktionen = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().AktionenAsync(id));
        aktionen.Genehmigen.Erlaubt.ShouldBeFalse();
        aktionen.Genehmigen.Begruendung!.ShouldContain("bestellt sich nicht selbst");
        (await LetzteMailAnAsync(u, "rechner@demo.invalid")).Betreff.ShouldStartWith("Bitte bestellen");

        await u.AlsAsync("rechner");
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().ListeAsync(AntragFilter.MeineAufgaben))).Single().Id.ShouldBe(id);
        var v2 = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().VorlageAsync(id, Signaturschritt.Studiendekanat));
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().GenehmigenAsync(id, v2.Zeitpunkt, v2.Text, Unterschriften.Gezeichnet()));
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().AnsichtAsync(id))).Antrag.Status.ShouldBe(AntragStatus.Abgeschlossen);
    }

    [Fact]
    public async Task Rueckgabe_ueberholt_die_Unterschrift_und_die_Kette_bleibt_gueltig()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinreichenAsync(u, Unterschriften.Gezeichnet());
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().BestaetigenAsync(id, token));
        await u.AlsAsync("mustermann");
        await FestlegenAsync(u, id);
        var v = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().VorlageAsync(id, Signaturschritt.Erstpruefung));
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().ErstpruefungUnterschreibenAsync(id, v.Zeitpunkt, v.Text, Unterschriften.Gezeichnet()));

        await u.AlsAsync("beispiel");
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().ZurueckgebenAsync(id, "Raum ist belegt, bitte SI 0110."));

        await u.AlsAsync("mustermann");
        var zweit = await u.KontoAsync("probst");
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().FestlegungSpeichernAsync(id,
            new Festlegung(u.Uhr.GetUtcNow().AddDays(15), "SI 0110", zweit.Id, "", null, null)));
        u.Uhr.Advance(TimeSpan.FromMinutes(5));
        var v2 = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().VorlageAsync(id, Signaturschritt.Erstpruefung));
        v2.Text.ShouldContain("Ort: SI 0110");
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().AnsichtAsync(id))).Antrag.Rueckfrage.ShouldBe("Raum ist belegt, bitte SI 0110.");
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().ErstpruefungUnterschreibenAsync(id, v2.Zeitpunkt, v2.Text, Unterschriften.Gezeichnet()));

        await u.AlsAsync("beispiel");
        var v3 = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().VorlageAsync(id, Signaturschritt.Studiendekanat));
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().GenehmigenAsync(id, v3.Zeitpunkt, v3.Text, Unterschriften.Gezeichnet()));
        var ansicht = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().AnsichtAsync(id));
        ansicht.Signaturen.Count.ShouldBe(4);
        ansicht.Signaturen.Count(s => s.Signatur.Ueberholt is not null).ShouldBe(1);
        ansicht.KetteInOrdnung.ShouldBeTrue(string.Join(" | ", ansicht.Signaturen.SelectMany(s => s.Hinweise)));
    }

    [Fact]
    public async Task Veraenderung_in_der_Datenbank_faellt_auf()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinreichenAsync(u);
        await u.InBereichAsync(async d =>
        {
            var db = d.GetRequiredService<Infrastructure.Datenbank.ProfOMatDbContext>();
            var s = db.Set<Signatur>().Single(x => x.AntragId == id);
            db.Entry(s).Property(x => x.Unterzeichner).CurrentValue = "Jemand anderes";
            await db.SaveChangesAsync();
        });
        var ansicht = await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().StudierendenAnsichtAsync(id, token));
        ansicht!.KetteInOrdnung.ShouldBeFalse();
        ansicht.Signaturen.Single().Hinweise.ShouldContain(h => h.Contains("Systemsiegel ist ungültig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Falsches_Token_liefert_nichts_und_Zurueckziehen_benachrichtigt()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinreichenAsync(u, Unterschriften.Gezeichnet());
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().StudierendenAnsichtAsync(id, token + "x"))).ShouldBeNull();
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().BestaetigenAsync(id, "falsch"))).ShouldBeNull();
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().BestaetigenAsync(id, token));
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().ZurueckziehenAsync(id, token));
        (await LetzteMailAnAsync(u, "mustermann@demo.invalid")).Betreff.ShouldStartWith("Zurückgezogen");
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().StudierendenAnsichtAsync(id, token)))!.Antrag.Status
            .ShouldBe(AntragStatus.Zurueckgezogen);
    }

    [Fact]
    public async Task Unbestaetigte_Antraege_verfallen()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinreichenAsync(u, Unterschriften.Gezeichnet());
        u.Uhr.Advance(TimeSpan.FromDays(8));
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().VerfallenLassenAsync())).ShouldBe(1);
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().BestaetigenAsync(id, token)))!.Antrag.Status.ShouldBe(AntragStatus.Verfallen);
    }

    [Fact]
    public async Task Ablehnung_geht_mit_Begruendung_an_die_Studierenden()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinreichenAsync(u, Unterschriften.Gezeichnet());
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().BestaetigenAsync(id, token));
        await u.AlsAsync("probst");
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().AblehnenAsync(id, "x"))))
            .Message.ShouldContain("sehen nur");
        await u.AlsAsync("mustermann");
        await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().AblehnenAsync(id, "Ich prüfe dieses Modul nicht."));
        var mail = await LetzteMailAnAsync(u, "mia.musterstudentin@hs-osnabrueck.de");
        mail.Text.ShouldContain("Ich prüfe dieses Modul nicht.");
    }

    [Fact]
    public async Task Maildomain_der_Studierenden_wird_geprueft()
    {
        await using var u = await TestUmgebung.StartenAsync(new Dictionary<string, string?> { ["ProfOMat:StudierendenMailDomains:0"] = "hs-osnabrueck.de" });
        var angaben = await AngabenAsync(u);
        (await u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>().EntwurfAsync(angaben with { Email = "mia@stud.hs-osnabrueck.de" })))
            .ShouldNotBeNull();
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<DrittpruefungDienst>()
            .EntwurfAsync(angaben with { Email = "mia@gmail.com" })))).Message.ShouldContain("Hochschul-Mailadresse");
    }

    private sealed class MerkendeZustellung : IMailZustellung
    {
        public List<(string An, string Betreff)> Gesendet { get; } = [];

        public Task<bool> ZustellenAsync(string an, string betreff, string text, (string Name, string Typ, byte[] Inhalt)? anhang, CancellationToken abbruch = default)
        {
            Gesendet.Add((an, betreff));
            return Task.FromResult(true);
        }
    }
}

public sealed class KatalogTests
{
    [Fact]
    public async Task Abgleich_markiert_Verschwundenes_als_alt_und_Auswahlen_zeigen_es_nur_auf_Wunsch()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.InBereichAsync(async d =>
        {
            var k = d.GetRequiredService<KatalogDienst>();
            var aktuell = await k.StudiengaengeAsync(auchAlte: false);
            var mitAlten = await k.StudiengaengeAsync(auchAlte: true);
            aktuell.ShouldNotContain(s => s.Pruefungsordnung == "PO 2017");
            var alt = mitAlten.Single(s => s.Pruefungsordnung == "PO 2017");
            alt.IstAlt.ShouldBeTrue();
            alt.AltGrund!.ShouldContain("nicht mehr in");
            (await k.PruefungenAsync(alt.Id, auchAlte: false)).ShouldBeEmpty();
            (await k.PruefungenAsync(alt.Id, auchAlte: true)).Count.ShouldBe(2);
        });
    }

    [Fact]
    public async Task Lokale_Ergaenzungen_bleiben_und_von_Hand_Archiviertes_kommt_nicht_zurueck()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("verwaltung");
        await u.InBereichAsync(async d =>
        {
            var k = d.GetRequiredService<KatalogDienst>();
            var et = (await k.StudiengaengeAsync(false)).Single(s => s.Kuerzel == "ET");
            var lokal = await k.LokalePruefungAnlegenAsync(et.Id, "", "Projektarbeit Sonderfall", "mündlich");
            var mathe = (await k.PruefungenAsync(et.Id, false)).Single(p => p.Titel == "Mathematik 1");
            await k.AltSetzenAsync(mathe.Id, alt: true, "PO-Wechsel");
            var ergebnis = await k.AbgleichenAsync();
            ergebnis.Alt.ShouldBe(0);
            var nachher = await k.PruefungenAsync(et.Id, false);
            nachher.ShouldContain(p => p.Id == lokal.Id);
            nachher.ShouldNotContain(p => p.Id == mathe.Id);
        });
    }

    [Fact]
    public async Task Nur_Verwaltung_und_Studiendekanat_pflegen_den_Katalog()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("mustermann");
        await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<KatalogDienst>().AbgleichenAsync()));
    }
}

public sealed class KontoTests
{
    [Fact]
    public async Task Passwortanmeldung_und_Mindestlaenge()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("verwaltung");
        await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<KontoDienst>()
            .AnlegenAsync("neu", "Prof. Dr.", "Nora", "Neu", "neu@hs.test", [Domain.Konten.Rolle.Pruefende], "kurz")));
        await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>()
            .AnlegenAsync("Neu", "Prof. Dr.", "Nora", "Neu", "neu@hs.test", [Domain.Konten.Rolle.Pruefende], "ein-langes-passwort"));
        (await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().PasswortAnmeldenAsync("neu", "falsch"))).ShouldBeNull();
        var b = await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().PasswortAnmeldenAsync(" NEU ", "ein-langes-passwort"));
        b!.Name.ShouldBe("Prof. Dr. Nora Neu");
        b.Hat(Domain.Konten.Rolle.Pruefende).ShouldBeTrue();
    }

    [Fact]
    public async Task Schluessel_hinterlegen_braucht_Besitznachweis()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("mustermann");
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zeit = Signaturinhalt.Kappen(u.Uhr.GetUtcNow());
        var fremderText = KontoDienst.Besitznachweis(Guid.NewGuid(), zeit);
        await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<KontoDienst>()
            .SchluesselHinterlegenAsync(zeit, Unterschriften.MitSchluessel(k, fremderText), "x")));
    }
}
