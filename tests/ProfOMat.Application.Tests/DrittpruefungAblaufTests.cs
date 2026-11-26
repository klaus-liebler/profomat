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
    private const string Wuensche = "Nicht montags.\nAm liebsten vormittags.";

    private static Task<T> D<T>(TestUmgebung u, Func<DrittpruefungDienst, Task<T>> arbeit) => u.InBereichAsync(d => arbeit(d.GetRequiredService<DrittpruefungDienst>()));

    private static Task D(TestUmgebung u, Func<DrittpruefungDienst, Task> arbeit) => u.InBereichAsync(d => arbeit(d.GetRequiredService<DrittpruefungDienst>()));

    private static Task<Einladungsangaben> AngabenAsync(TestUmgebung u, string pruefung = "Regelungstechnik") => u.InBereichAsync(async d =>
    {
        var k = d.GetRequiredService<KatalogDienst>();
        var et = (await k.StudiengaengeAsync(false)).Single(s => s.Kuerzel == "ET");
        var p = (await k.PruefungenAsync(et.Id, false)).Single(x => x.Titel == pruefung);
        return new Einladungsangaben("Mia", "Musterstudentin", et.Id, p.Id, 14);
    });

    /// <summary>Erstprüfende erzeugen den Link; liefert Antrag-Id und Token (aus dem Link).</summary>
    private static async Task<(Guid Id, string Token)> EinladenAsync(TestUmgebung u, string erst = "mustermann", string? zweit = "probst", bool mitTermin = false)
    {
        await u.AlsAsync(erst);
        var angaben = await AngabenAsync(u);
        Guid? zweitId = zweit is null ? null : (await u.KontoAsync(zweit)).Id;
        var vorbelegung = new Festlegung(mitTermin ? u.Uhr.GetUtcNow().AddDays(14) : null, mitTermin ? "SI 0012" : null, zweitId, null, null, null);
        var link = await D(u, x => x.EinladenAsync(angaben, vorbelegung));
        link.Link.ShouldStartWith($"https://profomat.test/drittpruefung/{link.AntragId:D}/");
        link.GueltigBis.ShouldBe(u.Uhr.GetUtcNow().AddDays(14), TimeSpan.FromSeconds(1));
        var teile = link.Link.Split('/');
        return (Guid.Parse(teile[^2]), teile[^1]);
    }

    private static async Task StudierendAsync(TestUmgebung u, Guid id, string token, Func<string, Unterschrift>? unterschrift = null, string matrikel = "1234567")
    {
        u.Benutzer.Aktuell = Benutzer.Niemand;
        var v = await D(u, x => x.StudierendenVorlageAsync(id, token, matrikel, Wuensche));
        await D(u, x => x.StudierendUnterschreibenAsync(id, token, matrikel, Wuensche, v.Zeitpunkt, v.Text, (unterschrift ?? Gezeichnet)(v.Text)));
    }

    private static Unterschrift Gezeichnet(string _) => Unterschriften.Gezeichnet();

    private static async Task<Signiervorlage> UnterschreibenAsync(TestUmgebung u, string als, Guid id, Signaturschritt schritt, Func<string, Unterschrift>? unterschrift = null)
    {
        await u.AlsAsync(als);
        u.Uhr.Advance(TimeSpan.FromMinutes(1));
        var v = await D(u, x => x.VorlageAsync(id, schritt));
        var sig = (unterschrift ?? Gezeichnet)(v.Text);
        await D(u, x => schritt switch
        {
            Signaturschritt.Erstpruefung => x.ErstpruefungUnterschreibenAsync(id, v.Zeitpunkt, v.Text, sig),
            Signaturschritt.Zweitpruefung => x.ZweitpruefungUnterschreibenAsync(id, v.Zeitpunkt, v.Text, sig),
            _ => x.GenehmigenAsync(id, v.Zeitpunkt, v.Text, sig),
        });
        return v;
    }

    private static async Task TerminFestlegenAsync(TestUmgebung u, Guid id, string ort = "SI 0012", string zweit = "probst")
    {
        await u.AlsAsync("mustermann");
        var zweitKonto = await u.KontoAsync(zweit);
        await D(u, x => x.FestlegungSpeichernAsync(id, new Festlegung(u.Uhr.GetUtcNow().AddDays(14), ort, zweitKonto.Id, null, null, "Bitte Ausweis mitbringen.")));
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

    private static Task<int> PdfFassungenAsync(TestUmgebung u, Guid id) => u.InBereichAsync(d =>
    {
        var daten = d.GetRequiredService<IDaten>();
        return daten.AnzahlAsync(daten.Menge<Dokument>().Where(x => x.AntragId == id));
    });

    private static async Task SchluesselHinterlegenAsync(TestUmgebung u, ECDsa schluessel)
    {
        var zeit = Signaturinhalt.Kappen(u.Uhr.GetUtcNow());
        var nachweis = KontoDienst.Besitznachweis(u.Benutzer.Aktuell.KontoIdPflicht, zeit);
        await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().SchluesselHinterlegenAsync(zeit, Unterschriften.MitSchluessel(schluessel, nachweis), "Laptop"));
    }

    private static Task StudiendekanatEntfernenAsync(TestUmgebung u) => u.InBereichAsync(async d =>
    {
        var daten = d.GetRequiredService<IDaten>();
        foreach (var f in await daten.ListeAsync(daten.Menge<Fakultaet>()))
        {
            f.Aendern(f.Name, null, f.SekretariatEmail);
        }

        await daten.SpeichernAsync();
    });

    private static async Task PdfSpeichernAsync(TestUmgebung u, Guid id, string name)
    {
        var pdf = await D(u, x => x.PdfAsync(id));
        pdf.ShouldNotBeNull();
        pdf.Inhalt.AsSpan(0, 4).ToArray().ShouldBe("%PDF"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(Path.GetTempPath(), name), pdf.Inhalt, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Gesamter_Ablauf_Einladung_bis_Studiendekanat_mit_neuer_PDF_Fassung_nach_jeder_Unterschrift()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinladenAsync(u);

        // Studierende sehen die vorausgefüllten Angaben, noch ohne PDF
        u.Benutzer.Aktuell = Benutzer.Niemand;
        var vorher = await D(u, x => x.StudierendenAnsichtAsync(id, token));
        vorher!.Antrag.Status.ShouldBe(AntragStatus.Eingeladen);
        vorher.Antrag.Name.ShouldBe("Mia Musterstudentin");
        vorher.Antrag.ZweitpruefendeName.ShouldBe("Prof. Dr. Lena Probst");
        (await D(u, x => x.PdfFuerStudierendeAsync(id, token))).ShouldBeNull();

        // Studierende unterschreiben mit eigenem (nicht hinterlegtem) Schlüssel
        using var studiSchluessel = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var vorlage = await D(u, x => x.StudierendenVorlageAsync(id, token, "1234567", Wuensche));
        vorlage.Text.ShouldContain("Format: profomat-antrag-v2");
        vorlage.Text.ShouldContain("Matrikelnummer: 1234567");
        vorlage.Text.ShouldContain("Zweitprüfende/r: Prof. Dr. Lena Probst [");
        vorlage.Text.ShouldContain("Termin: -");
        await D(u, x => x.StudierendUnterschreibenAsync(id, token, "1234567", Wuensche, vorlage.Zeitpunkt, vorlage.Text,
            Unterschriften.MitSchluessel(studiSchluessel, vorlage.Text)));
        (await D(u, x => x.PdfFuerStudierendeAsync(id, token))).ShouldNotBeNull();
        var anErst = await LetzteMailAnAsync(u, "mustermann@demo.invalid");
        anErst.Text.ShouldContain($"https://profomat.test/antraege/{id:D}");
        anErst.AnhangDokumentId.ShouldNotBeNull();
        (await PdfFassungenAsync(u, id)).ShouldBe(1);

        // Erstprüferin: ohne Termin gesperrt; Termin ergänzen, mit hinterlegtem Schlüssel unterschreiben
        await u.AlsAsync("mustermann");
        (await D(u, x => x.ListeAsync(AntragFilter.MeineAufgaben))).Single().Id.ShouldBe(id);
        (await D(u, x => x.AktionenAsync(id))).ErstpruefungUnterschreiben.Begruendung!.ShouldContain("Datum");
        using var profSchluessel = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SchluesselHinterlegenAsync(u, profSchluessel);
        await TerminFestlegenAsync(u, id);
        var v1 = await UnterschreibenAsync(u, "mustermann", id, Signaturschritt.Erstpruefung, t => Unterschriften.MitSchluessel(profSchluessel, t));
        v1.Text.ShouldContain("Ort: SI 0012");
        v1.Text.ShouldContain("Terminliche Einschränkungen und Wünsche: Nicht montags.\n    Am liebsten vormittags.");
        (await LetzteMailAnAsync(u, "probst@demo.invalid")).Betreff.ShouldStartWith("Bitte unterschreiben");

        // Zweitprüferin unterschreibt im Feld → zuständiger Studiendekan im ProfOMat
        await u.AlsAsync("probst");
        (await D(u, x => x.ListeAsync(AntragFilter.MeineAufgaben))).Single().Id.ShouldBe(id);
        await UnterschreibenAsync(u, "probst", id, Signaturschritt.Zweitpruefung);
        (await D(u, x => x.AnsichtAsync(id))).Antrag.Status.ShouldBe(AntragStatus.BeimStudiendekanat);
        (await LetzteMailAnAsync(u, "beispiel@demo.invalid")).Betreff.ShouldStartWith("Bitte bestellen");

        // Studiendekan bestellt
        var v3 = await UnterschreibenAsync(u, "beispiel", id, Signaturschritt.Studiendekanat);
        v3.Text.ShouldContain("Ich bestelle die oben genannten Prüfenden");
        var ansicht = await D(u, x => x.AnsichtAsync(id));
        ansicht.Antrag.Status.ShouldBe(AntragStatus.Abgeschlossen);
        ansicht.KetteInOrdnung.ShouldBeTrue(string.Join(" | ", ansicht.Signaturen.SelectMany(s => s.Hinweise)));
        ansicht.Signaturen.Select(s => s.Signatur.Schritt).ShouldBe(
            [Signaturschritt.Antragstellung, Signaturschritt.Erstpruefung, Signaturschritt.Zweitpruefung, Signaturschritt.Studiendekanat]);
        (await PdfFassungenAsync(u, id)).ShouldBe(4);
        await PdfSpeichernAsync(u, id, "profomat-test-drittpruefung.pdf");
        (await LetzteMailAnAsync(u, "mustermann@demo.invalid")).Betreff.ShouldStartWith("Bestellt");
        (await LetzteMailAnAsync(u, "probst@demo.invalid")).Betreff.ShouldStartWith("Bestellt");

        // Keine automatische Mail an das Sekretariat: das Studiendekanat schickt das PDF selbst
        (await MailsAsync(u)).ShouldNotContain(n => n.An == "sekretariat@hs.test");
        ansicht.SekretariatEmail.ShouldBe("sekretariat@hs.test");
    }

    [Fact]
    public async Task Studiendekanat_ausserhalb_Zweitpruefende_laden_herunter_und_melden_die_Weiterleitung()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await StudiendekanatEntfernenAsync(u);
        var (id, token) = await EinladenAsync(u, mitTermin: true);
        await StudierendAsync(u, id, token);
        await UnterschreibenAsync(u, "mustermann", id, Signaturschritt.Erstpruefung);
        await UnterschreibenAsync(u, "probst", id, Signaturschritt.Zweitpruefung);

        var ansicht = await D(u, x => x.AnsichtAsync(id));
        ansicht.Antrag.Status.ShouldBe(AntragStatus.ExternWeiterleiten);
        (await D(u, x => x.ListeAsync(AntragFilter.MeineAufgaben))).Single().Id.ShouldBe(id);
        await PdfSpeichernAsync(u, id, "profomat-test-drittpruefung-extern.pdf");
        (await MailsAsync(u)).ShouldNotContain(n => n.Betreff.StartsWith("Bitte bestellen", StringComparison.Ordinal));

        await u.AlsAsync("beispiel");
        (await D(u, x => x.AktionenAsync(id))).Genehmigen.Erlaubt.ShouldBeFalse();
        (await D(u, x => x.AktionenAsync(id))).WeitergeleitetMelden.Erlaubt.ShouldBeFalse();

        await u.AlsAsync("probst");
        await D(u, x => x.WeitergeleitetAsync(id));
        (await D(u, x => x.AnsichtAsync(id))).Antrag.Status.ShouldBe(AntragStatus.Weitergeleitet);
        (await D(u, x => x.ListeAsync(AntragFilter.Offen))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dekan_als_Erstpruefer_bestellt_sich_nicht_selbst_die_Vertretung_bekommt_den_Antrag()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinladenAsync(u, erst: "beispiel", mitTermin: true);
        await StudierendAsync(u, id, token);
        await UnterschreibenAsync(u, "beispiel", id, Signaturschritt.Erstpruefung);
        await UnterschreibenAsync(u, "probst", id, Signaturschritt.Zweitpruefung);

        await u.AlsAsync("beispiel");
        var aktionen = await D(u, x => x.AktionenAsync(id));
        aktionen.Genehmigen.Begruendung!.ShouldContain("bestellt sich nicht selbst");
        (await LetzteMailAnAsync(u, "rechner@demo.invalid")).Betreff.ShouldStartWith("Bitte bestellen");

        await u.AlsAsync("rechner");
        (await D(u, x => x.ListeAsync(AntragFilter.MeineAufgaben))).Single().Id.ShouldBe(id);
        await UnterschreibenAsync(u, "rechner", id, Signaturschritt.Studiendekanat);
        (await D(u, x => x.AnsichtAsync(id))).Antrag.Status.ShouldBe(AntragStatus.Abgeschlossen);
    }

    [Fact]
    public async Task Rechte_Fremde_sehen_nichts_nur_Pruefende_laden_ein_nur_die_Zweitpruefende_unterschreibt()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("sekretariat");
        var angaben = await AngabenAsync(u);
        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.EinladenAsync(angaben, null)))).Message.ShouldContain("Prüfende");

        var (id, token) = await EinladenAsync(u, mitTermin: true);
        await u.AlsAsync("tester");
        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.AnsichtAsync(id)))).Message.ShouldContain("sehen nur");
        (await D(u, x => x.ListeAsync(AntragFilter.Alle))).ShouldBeEmpty();

        // Vor der Unterschrift der Studierenden unterschreibt niemand sonst
        await u.AlsAsync("mustermann");
        (await D(u, x => x.AktionenAsync(id))).ErstpruefungUnterschreiben.Begruendung!.ShouldContain("Erst unterschreibt die/der Studierende");

        await StudierendAsync(u, id, token);
        await UnterschreibenAsync(u, "mustermann", id, Signaturschritt.Erstpruefung);
        await u.AlsAsync("mustermann");
        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.VorlageAsync(id, Signaturschritt.Zweitpruefung)))).Message.ShouldContain("Lena Probst");
        (await D(u, x => x.AktionenAsync(id))).Festlegen.Erlaubt.ShouldBeFalse();

        // Zweitprüfende brauchen die Rolle Prüfende
        var (id2, _) = await EinladenAsync(u);
        var sekretariat = await u.KontoAsync("sekretariat");
        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.FestlegungSpeichernAsync(id2,
            new Festlegung(null, null, sekretariat.Id, null, null, null))))).Message.ShouldContain("nicht zur Verfügung");
    }

    [Fact]
    public async Task Geaenderter_Text_falsche_Signatur_und_abgelaufene_Vorlage_werden_abgelehnt()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinladenAsync(u);
        u.Benutzer.Aktuell = Benutzer.Niemand;
        var v = await D(u, x => x.StudierendenVorlageAsync(id, token, "1234567", null));
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // andere Matrikelnummer als angezeigt
        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.StudierendUnterschreibenAsync(id, token, "7654321", null, v.Zeitpunkt, v.Text,
            Unterschriften.MitSchluessel(k, v.Text))))).Message.ShouldContain("geändert");

        // Signatur über anderen Text
        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.StudierendUnterschreibenAsync(id, token, "1234567", null, v.Zeitpunkt, v.Text,
            Unterschriften.MitSchluessel(k, v.Text + "x"))))).Message.ShouldContain("passt nicht");

        // kurzer RSA-Schlüssel
        using var kurz = RSA.Create(1024);
        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.StudierendUnterschreibenAsync(id, token, "1234567", null, v.Zeitpunkt, v.Text,
            Unterschriften.MitRsa(kurz, v.Text))))).Message.ShouldContain("2048");

        // Erstprüferin ändert den Ort, während die Studentin unterschreibt
        await u.AlsAsync("mustermann");
        await D(u, x => x.FestlegungSpeichernAsync(id, new Festlegung(null, "SI 0110", null, null, null, null)));
        u.Benutzer.Aktuell = Benutzer.Niemand;
        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.StudierendUnterschreibenAsync(id, token, "1234567", null, v.Zeitpunkt, v.Text,
            Unterschriften.Gezeichnet())))).Message.ShouldContain("geändert");

        var v2 = await D(u, x => x.StudierendenVorlageAsync(id, token, "1234567", null));
        u.Uhr.Advance(TimeSpan.FromMinutes(31));
        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.StudierendUnterschreibenAsync(id, token, "1234567", null, v2.Zeitpunkt, v2.Text,
            Unterschriften.Gezeichnet())))).Message.ShouldContain("abgelaufen");

        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.StudierendenVorlageAsync(id, token, "12a4", null)))).Message.ShouldContain("Matrikelnummer");
    }

    [Fact]
    public async Task Nicht_hinterlegter_Schluessel_der_Pruefenden_wird_abgelehnt()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinladenAsync(u, mitTermin: true);
        await StudierendAsync(u, id, token);
        using var fremd = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (await Should.ThrowAsync<FachlicherFehler>(() => UnterschreibenAsync(u, "mustermann", id, Signaturschritt.Erstpruefung,
            t => Unterschriften.MitSchluessel(fremd, t)))).Message.ShouldContain("nicht hinterlegt");
    }

    [Fact]
    public async Task Rueckgabe_ueberholt_Pruefende_und_die_Kette_bleibt_gueltig()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinladenAsync(u, mitTermin: true);
        await StudierendAsync(u, id, token);
        await UnterschreibenAsync(u, "mustermann", id, Signaturschritt.Erstpruefung);

        await u.AlsAsync("probst");
        await D(u, x => x.ZurueckgebenAsync(id, "Da bin ich auf einer Tagung."));
        (await LetzteMailAnAsync(u, "mustermann@demo.invalid")).Text.ShouldContain("Da bin ich auf einer Tagung.");

        await TerminFestlegenAsync(u, id, ort: "SI 0110");
        (await D(u, x => x.AnsichtAsync(id))).Antrag.Rueckfrage!.ShouldContain("Tagung");
        var v = await UnterschreibenAsync(u, "mustermann", id, Signaturschritt.Erstpruefung);
        v.Text.ShouldContain("Ort: SI 0110");
        await UnterschreibenAsync(u, "probst", id, Signaturschritt.Zweitpruefung);

        await u.AlsAsync("beispiel");
        await D(u, x => x.ZurueckgebenAsync(id, "Bitte anderen Raum."));
        await UnterschreibenAsync(u, "mustermann", id, Signaturschritt.Erstpruefung);
        await UnterschreibenAsync(u, "probst", id, Signaturschritt.Zweitpruefung);
        await UnterschreibenAsync(u, "beispiel", id, Signaturschritt.Studiendekanat);

        var ansicht = await D(u, x => x.AnsichtAsync(id));
        ansicht.Antrag.Status.ShouldBe(AntragStatus.Abgeschlossen);
        ansicht.Signaturen.Count.ShouldBe(7);
        ansicht.Signaturen.Count(s => s.Signatur.Ueberholt is not null).ShouldBe(3);
        ansicht.KetteInOrdnung.ShouldBeTrue(string.Join(" | ", ansicht.Signaturen.SelectMany(s => s.Hinweise)));
    }

    [Fact]
    public async Task Veraenderung_in_der_Datenbank_faellt_auf()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinladenAsync(u);
        await StudierendAsync(u, id, token);
        await u.InBereichAsync(async d =>
        {
            var db = d.GetRequiredService<Infrastructure.Datenbank.ProfOMatDbContext>();
            var s = db.Set<Signatur>().Single(x => x.AntragId == id);
            db.Entry(s).Property(x => x.Unterzeichner).CurrentValue = "Jemand anderes";
            await db.SaveChangesAsync();
        });
        var ansicht = await D(u, x => x.StudierendenAnsichtAsync(id, token));
        ansicht!.KetteInOrdnung.ShouldBeFalse();
        ansicht.Signaturen.Single().Hinweise.ShouldContain(h => h.Contains("Systemsiegel ist ungültig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Link_verfaellt_und_ein_neuer_Link_ersetzt_den_alten()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinladenAsync(u);
        (await D(u, x => x.StudierendenAnsichtAsync(id, token + "x"))).ShouldBeNull();
        u.Uhr.Advance(TimeSpan.FromDays(15));
        u.Benutzer.Aktuell = Benutzer.Niemand;
        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.StudierendenVorlageAsync(id, token, "1234567", null)))).Message.ShouldContain("abgelaufen");
        (await D(u, x => x.VerfallenLassenAsync())).ShouldBe(1);
        (await D(u, x => x.StudierendenAnsichtAsync(id, token)))!.Antrag.Status.ShouldBe(AntragStatus.Verfallen);

        await u.AlsAsync("probst");
        (await D(u, x => x.AktionenAsync(id))).LinkErneuern.Erlaubt.ShouldBeFalse();
        await u.AlsAsync("mustermann");
        var neu = await D(u, x => x.LinkErneuernAsync(id, 3));
        var neuesToken = neu.Link.Split('/')[^1];
        neuesToken.ShouldNotBe(token);
        (await D(u, x => x.StudierendenAnsichtAsync(id, token))).ShouldBeNull();
        await StudierendAsync(u, id, neuesToken);
        (await D(u, x => x.StudierendenAnsichtAsync(id, neuesToken)))!.Antrag.Status.ShouldBe(AntragStatus.BeimErstpruefer);
    }

    [Fact]
    public async Task Zuruecknehmen_Zurueckziehen_und_Ablehnen()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, _) = await EinladenAsync(u);
        await D(u, x => x.EinladungZuruecknehmenAsync(id));
        (await D(u, x => x.AnsichtAsync(id))).Antrag.Status.ShouldBe(AntragStatus.Zurueckgezogen);

        // Studierende ziehen nach der Unterschrift zurück → Erstprüfende erfahren es
        var (id2, token2) = await EinladenAsync(u);
        await StudierendAsync(u, id2, token2);
        await D(u, x => x.ZurueckziehenAsync(id2, token2));
        (await LetzteMailAnAsync(u, "mustermann@demo.invalid")).Betreff.ShouldStartWith("Zurückgezogen");

        // Ablehnen: nur die Erstprüferin; die Studentin sieht die Begründung über ihren Link
        var (id3, token3) = await EinladenAsync(u);
        await StudierendAsync(u, id3, token3);
        await u.AlsAsync("probst");
        (await Should.ThrowAsync<FachlicherFehler>(() => D(u, x => x.AblehnenAsync(id3, "x")))).Message.ShouldContain("Erstprüfer");
        await u.AlsAsync("mustermann");
        await D(u, x => x.AblehnenAsync(id3, "Ich prüfe dieses Modul nicht."));
        (await D(u, x => x.StudierendenAnsichtAsync(id3, token3)))!.Antrag.Ablehnungsgrund.ShouldBe("Ich prüfe dieses Modul nicht.");
    }

    [Fact]
    public async Task Postausgang_stellt_zu_und_haengt_das_PDF_an()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var (id, token) = await EinladenAsync(u);
        await StudierendAsync(u, id, token);
        var zustellung = new MerkendeZustellung();
        var anzahl = await u.InBereichAsync(d => d.GetRequiredService<Postausgang>().ZustellenAsync(zustellung));
        anzahl.ShouldBe(1);
        zustellung.Gesendet.Single().An.ShouldBe("mustermann@demo.invalid");
        zustellung.Gesendet.Single().MitAnhang.ShouldBeTrue();
        (await MailsAsync(u)).ShouldAllBe(n => n.Versandt != null);
    }

    private sealed class MerkendeZustellung : IMailZustellung
    {
        public List<(string An, string Betreff, bool MitAnhang)> Gesendet { get; } = [];

        public Task<bool> ZustellenAsync(string an, string betreff, string text, (string Name, string Typ, byte[] Inhalt)? anhang, CancellationToken abbruch = default)
        {
            Gesendet.Add((an, betreff, anhang is not null));
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
