using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProfOMat.Application.Betrieb;
using ProfOMat.Application.Konten;
using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;
using ProfOMat.Domain.Muendlich;
using ProfOMat.Infrastructure.Betrieb;
using ProfOMat.Infrastructure.Datenbank;
using ProfOMat.Infrastructure.Sicherheit;

namespace ProfOMat.Application.Tests;

/// <summary>Stufe 2 des Zielbilds: Feldverschlüsselung, Sicherheitsprotokoll, Revision, Netzbeschränkung, Betrieb.</summary>
public sealed class FeldverschluesselungTests
{
    private static Feldschluesselring Ring(params int[] versionen)
        => new(versionen.Select(v => (v, SHA256.HashData(BitConverter.GetBytes(v)))));

    [Fact]
    public void Verschluesselt_mit_Version_und_entschluesselt_wieder()
    {
        var r = Ring(1);
        var g = Feldverschluesselung.Verschluesseln("1234567", deterministisch: false, r);
        g.ShouldStartWith("enc:v1:");
        g.ShouldNotContain("1234567");
        Feldverschluesselung.Entschluesseln(g, r).ShouldBe("1234567");
        Feldverschluesselung.Verschluesseln("1234567", false, r).ShouldNotBe(g);   // zufällige Nonce
        Feldverschluesselung.Verschluesseln("1234567", true, r).ShouldBe(Feldverschluesselung.Verschluesseln("1234567", true, r));   // eindeutige Indizes
        Feldverschluesselung.Verschluesseln("1234568", true, r).ShouldNotBe(Feldverschluesselung.Verschluesseln("1234567", true, r));
        Feldverschluesselung.Entschluesseln("klartext-alt", r).ShouldBe("klartext-alt");   // Bestand vor der Aufwertung
    }

    [Fact]
    public void Manipulation_und_fremder_Schluessel_fallen_auf()
    {
        var r = Ring(1);
        var g = Feldverschluesselung.Verschluesseln("Bemerkung", false, r);
        var roh = Convert.FromBase64String(g["enc:v1:".Length..]);
        roh[^1] ^= 1;
        Should.Throw<CryptographicException>(() => Feldverschluesselung.Entschluesseln("enc:v1:" + Convert.ToBase64String(roh), r));
        Should.Throw<CryptographicException>(() => Feldverschluesselung.Entschluesseln(g, new Feldschluesselring([(1, RandomNumberGenerator.GetBytes(32))])));
    }

    [Fact]
    public void Schluesselwechsel_liest_alte_Versionen_und_schreibt_mit_der_neuen()
    {
        var alt = Ring(1);
        var neu = Ring(1, 2);
        var g1 = Feldverschluesselung.Verschluesseln("x", false, alt);
        Feldverschluesselung.Entschluesseln(g1, neu).ShouldBe("x");
        Feldverschluesselung.Verschluesseln("x", false, neu).ShouldStartWith("enc:v2:");
        Feldverschluesselung.Version(g1).ShouldBe(1);
        Should.Throw<CryptographicException>(() => Feldverschluesselung.Entschluesseln(Feldverschluesselung.Verschluesseln("x", false, neu), alt));
        Feldschluesselring.AusText("# Kommentar\n" + Feldschluesselring.NeueZeile(1) + "\n" + Feldschluesselring.NeueZeile(3) + "\n").AktuelleVersion.ShouldBe(3);
    }

    [Fact]
    public async Task Datenbank_sieht_nur_Geheimtext_Fachcode_Klartext_und_Bestand_wird_verschluesselt()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("mustermann");
        var konto = await u.KontoAsync("mustermann");
        var id = await u.InBereichAsync(async d =>
        {
            var p = new MuendlichePruefung(konto.Id, "Mathe 1", u.Uhr.GetUtcNow());
            p.AngabenSetzen("Erika Prüfling", "7654321", "ET", konto.Id, null);
            var f = p.FrageHinzufuegen("1a", 10);
            p.FrageBewerten(f.Id, 6, false);
            p.FrageBemerken(f.Id, "unsicher bei Integralen");
            p.NoteFestlegen(2.7m, "mündlich überzeugend", u.Uhr.GetUtcNow());
            var daten = d.GetRequiredService<IDaten>();
            daten.Hinzufuegen(p);
            await daten.SpeichernAsync();
            return p.Id;
        });

        // roh in der Datenbank: nur Geheimtext
        var roh = await RohAsync(u, $"SELECT \"Matrikelnummer\" || '|' || \"Endnote\" || '|' || \"Abweichungsbegruendung\" FROM \"MuendlichePruefungen\" WHERE \"Id\" = '{id}'");
        roh.ShouldNotContain("7654321");
        roh.ShouldNotContain("2.7");
        roh.ShouldNotContain("überzeugend");
        roh.Split('|').ShouldAllBe(t => t.StartsWith("enc:v"));
        (await RohAsync(u, "SELECT \"Bemerkung\" FROM \"GestellteFragen\"")).ShouldStartWith("enc:v");

        // Fachcode: Klartext
        var gelesen = await u.InBereichAsync(d => d.GetRequiredService<IDaten>().FindenAsync<MuendlichePruefung>(id));
        gelesen!.Matrikelnummer.ShouldBe("7654321");
        gelesen.Endnote.ShouldBe(2.7m);
        gelesen.Abweichungsbegruendung.ShouldBe("mündlich überzeugend");
        gelesen.Fragen.Single().Bemerkung.ShouldBe("unsicher bei Integralen");

        // Bestand im Klartext (wie vor Version 20) wird beim Start verschlüsselt
        await RohAsync(u, $"UPDATE \"MuendlichePruefungen\" SET \"Matrikelnummer\" = '1111111', \"Endnote\" = '1.3' WHERE \"Id\" = '{id}'", schreiben: true);
        (await u.InBereichAsync(d => d.GetRequiredService<IDaten>().FindenAsync<MuendlichePruefung>(id)))!.Matrikelnummer.ShouldBe("1111111");
        var anzahl = await u.InBereichAsync(d => DatenbankInitialisierung.BestandVerschluesselnAsync(d.GetRequiredService<ProfOMatDbContext>()));
        anzahl.ShouldBe(2);
        (await RohAsync(u, $"SELECT \"Matrikelnummer\" FROM \"MuendlichePruefungen\" WHERE \"Id\" = '{id}'")).ShouldStartWith("enc:v");
        var danach = await u.InBereichAsync(d => d.GetRequiredService<IDaten>().FindenAsync<MuendlichePruefung>(id));
        danach!.Matrikelnummer.ShouldBe("1111111");
        danach.Endnote.ShouldBe(1.3m);
        (await u.InBereichAsync(d => DatenbankInitialisierung.BestandVerschluesselnAsync(d.GetRequiredService<ProfOMatDbContext>()))).ShouldBe(0);   // idempotent
    }

    internal static async Task<string> RohAsync(TestUmgebung u, string sql, bool schreiben = false) => await u.InBereichAsync(async d =>
    {
        var verbindung = d.GetRequiredService<ProfOMatDbContext>().Database.GetDbConnection();
        await verbindung.OpenAsync();
        await using var befehl = verbindung.CreateCommand();
        befehl.CommandText = sql;
        if (schreiben)
        {
            await befehl.ExecuteNonQueryAsync();
            return "";
        }

        return (await befehl.ExecuteScalarAsync())?.ToString() ?? "";
    });
}

public sealed class SicherheitsprotokollTests
{
    [Fact]
    public async Task Kette_ist_intakt_nur_anfuegen_und_Manipulation_faellt_auf()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var p = u.Dienste.GetRequiredService<ProfOMat.Application.ISicherheitsprotokoll>();
        await p.EintragenAsync("Test", null, "eins", TestContext.Current.CancellationToken);
        await p.EintragenAsync("Test", Guid.NewGuid(), "zwei", TestContext.Current.CancellationToken);
        await p.EintragenAsync("Test", null, "drei", TestContext.Current.CancellationToken);
        var k = await p.PruefenAsync(TestContext.Current.CancellationToken);
        k.Intakt.ShouldBeTrue();
        k.Anzahl.ShouldBeGreaterThanOrEqualTo(3);
        var letzte = await p.LetzteAsync(3, TestContext.Current.CancellationToken);
        letzte.Select(e => e.Text).ShouldBe(["drei", "zwei", "eins"]);
        letzte[0].VorgaengerHash.ShouldBe(letzte[1].Hash);

        // Die Datenbank verbietet Änderungen und Löschungen
        await Should.ThrowAsync<Npgsql.PostgresException>(() => FeldverschluesselungTests.RohAsync(u, "UPDATE \"Sicherheitsprotokoll\" SET \"Text\" = 'x'", schreiben: true));
        await Should.ThrowAsync<Npgsql.PostgresException>(() => FeldverschluesselungTests.RohAsync(u, "DELETE FROM \"Sicherheitsprotokoll\"", schreiben: true));
        await Should.ThrowAsync<Npgsql.PostgresException>(() => FeldverschluesselungTests.RohAsync(u, "TRUNCATE \"Sicherheitsprotokoll\"", schreiben: true));

        // Wer die Trigger entfernt und ändert, fällt bei der Prüfung auf
        await FeldverschluesselungTests.RohAsync(u, "DROP TRIGGER \"Sicherheitsprotokoll_nur_anfuegen\" ON \"Sicherheitsprotokoll\"", schreiben: true);
        await FeldverschluesselungTests.RohAsync(u, "UPDATE \"Sicherheitsprotokoll\" SET \"Text\" = 'zwei (geändert)' WHERE \"Text\" = 'zwei'", schreiben: true);
        var nachher = await p.PruefenAsync(TestContext.Current.CancellationToken);
        nachher.Intakt.ShouldBeFalse();
        nachher.Grund.ShouldBe("Inhalt verändert");
    }

    [Fact]
    public async Task Anmeldungen_Fehlversuche_und_Rechteaenderungen_landen_im_Sicherheitsprotokoll()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var probst = await u.KontoAsync("probst");
        await u.AlsAsync("verwaltung");
        await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().PasswortSetzenAsync(probst.Id, null, "ein-langes-Passwort-1"));
        await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().PasswortPruefenAsync("probst", "falsch-falsch"));
        await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().PasswortPruefenAsync("niemand-da", "falsch-falsch"));
        await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().AendernAsync(probst.Id, probst.Titel, probst.Vorname, probst.Nachname, probst.Email,
            [Rolle.Pruefende, Rolle.Studiendekanat], true));
        var e = await u.Dienste.GetRequiredService<ProfOMat.Application.ISicherheitsprotokoll>().LetzteAsync(50, TestContext.Current.CancellationToken);
        e.ShouldContain(x => x.Art == "Konto.Passwort" && x.Text.Contains(probst.Id.ToString("D")));
        e.ShouldContain(x => x.Art == "Anmeldung.Fehlversuch" && x.KontoId == probst.Id && x.Text == "Passwort falsch");
        e.ShouldContain(x => x.Art == "Anmeldung.Fehlversuch" && x.KontoId == null && x.Text == "unbekannte Kennung");
        e.ShouldContain(x => x.Art == "Rechte" && x.Text.Contains("Studiendekanat"));
        e.ShouldAllBe(x => !x.Text.Contains("probst") && !x.Text.Contains("Probst"));   // keine Namen, nur Konto-Ids
    }

    [Fact]
    public async Task Revision_liest_das_Protokoll_andere_nicht_und_hat_TOTP_Pflicht()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("verwaltung");
        var link = await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().EinladungErzeugenAsync([Rolle.Revision], "Prüferin", 7));
        u.Benutzer.Aktuell = Benutzer.Niemand;
        var pruefer = await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().EinladungEinloesenAsync(link.Token,
            new Kontoangaben("pruefer", null, "Petra", "Prüfer", "pp@extern.test"), "ein-langes-Passwort-1"));
        pruefer.Rollen.ShouldBe([Rolle.Revision]);
        Anmelderegeln.ZweiterFaktorPflicht([Rolle.Revision]).ShouldBeTrue();

        u.Benutzer.Aktuell = pruefer;
        (await u.InBereichAsync(d => d.GetRequiredService<RevisionDienst>().ProtokollAsync())).ShouldNotBeEmpty();
        (await u.InBereichAsync(d => d.GetRequiredService<RevisionDienst>().PruefenAsync())).Intakt.ShouldBeTrue();
        await u.InBereichAsync(d => d.GetRequiredService<RevisionDienst>().SicherstellenAsync("Sicherheitsbericht"));
        // keine Verwaltung, keine Inhalte
        await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().OffeneEinladungenAsync()));
        Rechte.AlleAntraegeSehen(pruefer).ShouldBeFalse();
        Rechte.KatalogPflegen(pruefer).Erlaubt.ShouldBeFalse();

        foreach (var wer in new[] { "mustermann", "beispiel", "sekretariat" })
        {
            await u.AlsAsync(wer);
            await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<RevisionDienst>().ProtokollAsync()));
            await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => d.GetRequiredService<RevisionDienst>().SicherstellenAsync("Sicherheitsbericht")));
        }

        var e = await u.Dienste.GetRequiredService<ProfOMat.Application.ISicherheitsprotokoll>().LetzteAsync(20, TestContext.Current.CancellationToken);
        e.ShouldContain(x => x.Art == "Export" && x.Text == "Sicherheitsbericht" && x.KontoId == pruefer.KontoId);
        RevisionDienst.Matrix.ShouldContain(z => z.Rollen.Contains(Rolle.Revision));
    }
}

public sealed class BetriebsregelnTests
{
    private static readonly IReadOnlyList<IPNetwork> Hochschule = Betriebsregeln.Netze(["131.173.0.0/16", "10.8.0.0/16"]);

    [Theory]
    [InlineData("/antraege", "203.0.113.5", false)]
    [InlineData("/anmelden", "203.0.113.5", false)]
    [InlineData("/konten", "131.173.20.1", true)]
    [InlineData("/drittpruefung/0b8f6c0e-0000-7000-8000-000000000001/abcdefghijklmnopqrstuvwx", "203.0.113.5", true)]
    [InlineData("/drittpruefung/0b8f6c0e-0000-7000-8000-000000000001/abcdefghijklmnopqrstuvwx/pdf", "203.0.113.5", true)]
    [InlineData("/hausarbeit/bestaetigen/0b8f6c0e-0000-7000-8000-000000000001/token", "203.0.113.5", true)]
    [InlineData("/_blazor", "203.0.113.5", true)]
    [InlineData("/app.css", "203.0.113.5", true)]
    [InlineData("/antraege/0b8f6c0e-0000-7000-8000-000000000001/pdf", "203.0.113.5", false)]
    [InlineData("/gesund/bereit", "203.0.113.5", true)]
    [InlineData("/revision", "10.8.1.1", true)]
    [InlineData("/revision", "::ffff:10.8.1.1", true)]
    public void Verwaltung_nur_aus_den_Netzen_Studierendenlinks_von_ueberall(string pfad, string ip, bool erlaubt)
        => Betriebsregeln.Erlaubt(pfad, IPAddress.Parse(ip), Hochschule).ShouldBe(erlaubt);

    [Fact]
    public void Ohne_Netze_ist_alles_erlaubt()
        => Betriebsregeln.Erlaubt("/konten", IPAddress.Parse("203.0.113.5"), []).ShouldBeTrue();

    [Theory]
    [InlineData("/drittpruefung/0b8f6c0e-0000-7000-8000-000000000001/Ab_cDeF-ghijklmnopqrstuvwxyz012", "/drittpruefung/0b8f6c0e-0000-7000-8000-000000000001/…")]
    [InlineData("/hausarbeit/kalender/0b8f6c0e-0000-7000-8000-000000000001/abcdefghijklmnopqrstuvwxyz.ics", "/hausarbeit/kalender/0b8f6c0e-0000-7000-8000-000000000001/….ics")]
    [InlineData("/antraege/0b8f6c0e-0000-7000-8000-000000000001/pdf", "/antraege/0b8f6c0e-0000-7000-8000-000000000001/pdf")]
    [InlineData("", "/")]
    public void Pfadmaske_entfernt_Tokens_aus_Logs(string pfad, string erwartet) => Betriebsregeln.PfadMaskieren(pfad).ShouldBe(erwartet);

    [Fact]
    public void SecurityTxt_nach_RFC_9116_nur_mit_Kontakt()
    {
        Betriebsregeln.SecurityTxt(null, null, null, DateTimeOffset.UnixEpoch).ShouldBeNull();
        var t = Betriebsregeln.SecurityTxt("mailto:sicherheit@hs.test, https://hs.test/meldung", "2027-06-30T00:00:00Z", null, DateTimeOffset.UnixEpoch)!;
        t.ShouldContain("Contact: mailto:sicherheit@hs.test\n");
        t.ShouldContain("Contact: https://hs.test/meldung\n");
        t.ShouldContain("Expires: 2027-06-30T00:00:00Z\n");
    }

    [Fact]
    public void Zertifikatsablauf_wird_beobachtet()
    {
        var ordner = Directory.CreateTempSubdirectory("profomat-zert-").FullName;
        try
        {
            string Zert(string name, int tage)
            {
                using var rsa = RSA.Create(2048);
                var anfrage = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var z = anfrage.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(tage));
                var pfad = Path.Combine(ordner, name);
                File.WriteAllText(pfad, z.ExportCertificatePem());
                return pfad;
            }

            var jetzt = DateTimeOffset.UtcNow;
            Betriebsregeln.ZertifikatPruefung([Zert("gut.pem", 200)], 30, jetzt).Status.ShouldBe(HealthStatus.Healthy);
            Betriebsregeln.ZertifikatPruefung([Zert("bald.pem", 10)], 30, jetzt).Status.ShouldBe(HealthStatus.Degraded);
            Betriebsregeln.ZertifikatPruefung([Zert("gut2.pem", 200), Path.Combine(ordner, "fehlt.pem")], 30, jetzt).Status.ShouldBe(HealthStatus.Degraded);
            Betriebsregeln.ZertifikatPruefung([Zert("gut3.pem", 200)], 30, jetzt.AddDays(400)).Status.ShouldBe(HealthStatus.Unhealthy);
            Betriebsregeln.PlatzPruefung(ordner, 1).Status.ShouldBe(HealthStatus.Healthy);
            Betriebsregeln.PlatzPruefung(ordner, long.MaxValue / (1024 * 1024)).Status.ShouldBe(HealthStatus.Unhealthy);
        }
        finally
        {
            Directory.Delete(ordner, true);
        }
    }

    [Fact]
    public void Sicherheitsereignis_Hash_haengt_an_allen_Feldern()
    {
        var e = new Sicherheitsereignis(1, DateTimeOffset.UnixEpoch, "Test", null, "x", "");
        e.Stimmt.ShouldBeTrue();
        Sicherheitsereignis.Berechnen(1, DateTimeOffset.UnixEpoch, "Test", null, "y", "").ShouldNotBe(e.Hash);
        Sicherheitsereignis.Berechnen(2, DateTimeOffset.UnixEpoch, "Test", null, "x", "").ShouldNotBe(e.Hash);
    }
}
