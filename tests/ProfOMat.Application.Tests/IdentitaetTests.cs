using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ProfOMat.Application.Konten;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Tests;

/// <summary>Anmeldung über ASP.NET Core Identity: Sperre, Sicherheitsstempel, alte Hashes, TOTP-Pflicht, Verknüpfung, Passkeys, Rechte.</summary>
public sealed class IdentitaetTests
{
    private const string Pw = "ein-langes-Passwort-1";

    private static Task<T> Mit<T>(TestUmgebung u, Func<IServiceProvider, Task<T>> arbeit) => u.InBereichAsync(arbeit);

    private static KontoDienst Konten(IServiceProvider d) => d.GetRequiredService<KontoDienst>();

    private static SicherheitDienst Sicherheit(IServiceProvider d) => d.GetRequiredService<SicherheitDienst>();

    private static UserManager<Konto> Identitaet(IServiceProvider d) => d.GetRequiredService<UserManager<Konto>>();

    private static async Task<Konto> Laden(IServiceProvider d, Guid id) => (await Konten(d).AktivesKontoAsync(id))!;

    /// <summary>Passwort als Systemverwaltung setzen, danach wieder als die Person selbst.</summary>
    private static async Task<Konto> MitPasswortAsync(TestUmgebung u, string kennung)
    {
        var konto = await u.KontoAsync(kennung);
        await u.AlsAsync("verwaltung");
        await u.InBereichAsync(d => Konten(d).PasswortSetzenAsync(konto.Id, null, Pw));   // eigenes Konto der Verwaltung: noch ohne Passwort

        await u.AlsAsync(kennung);
        return await u.KontoAsync(kennung);
    }

    // ================================================================================================ Sperre nach Fehlversuchen

    [Fact]
    public async Task Fuenf_Fehlversuche_sperren_vorübergehend_auch_das_richtige_Passwort()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await MitPasswortAsync(u, "probst");
        for (var i = 1; i <= 4; i++)
        {
            (await Mit(u, d => Konten(d).PasswortPruefenAsync("probst", "falsch-falsch"))).Ergebnis.ShouldBe(Passwortergebnis.Falsch);
        }

        (await Mit(u, d => Konten(d).PasswortPruefenAsync("probst", "falsch-falsch"))).Ergebnis.ShouldBe(Passwortergebnis.VoruebergehendGesperrt);
        var gesperrt = await Mit(u, d => Konten(d).PasswortPruefenAsync("probst", Pw));
        gesperrt.Ergebnis.ShouldBe(Passwortergebnis.VoruebergehendGesperrt);
        gesperrt.Konto.ShouldBeNull();

        // Systemverwaltung speichert das Konto als „aktiv“: Sperre aufgehoben
        var probst = await u.KontoAsync("probst");
        probst.GesperrtBis.ShouldNotBeNull();
        await u.AlsAsync("verwaltung");
        await u.InBereichAsync(d => Konten(d).AendernAsync(probst.Id, probst.Titel, probst.Vorname, probst.Nachname, probst.Email, [Rolle.Pruefende], true));
        (await Mit(u, d => Konten(d).PasswortPruefenAsync("probst", Pw))).Erfolg.ShouldBeTrue();
    }

    [Fact]
    public async Task Erfolg_setzt_die_Fehlversuche_zurueck_und_gesperrte_Konten_melden_sich_nicht_an()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await MitPasswortAsync(u, "probst");
        await Mit(u, d => Konten(d).PasswortPruefenAsync("probst", "falsch-falsch"));
        (await u.KontoAsync("probst")).Fehlversuche.ShouldBe(1);
        (await Mit(u, d => Konten(d).PasswortPruefenAsync("probst", Pw))).Erfolg.ShouldBeTrue();
        (await u.KontoAsync("probst")).Fehlversuche.ShouldBe(0);

        var probst = await u.KontoAsync("probst");
        await u.AlsAsync("verwaltung");
        await u.InBereichAsync(d => Konten(d).AendernAsync(probst.Id, probst.Titel, probst.Vorname, probst.Nachname, probst.Email, [Rolle.Pruefende], false));
        (await Mit(u, d => Konten(d).PasswortPruefenAsync("probst", Pw))).Ergebnis.ShouldBe(Passwortergebnis.Falsch);   // ohne Hinweis auf die Sperre
        (await Mit(u, d => Konten(d).AktivesKontoAsync(probst.Id))).ShouldBeNull();
    }

    // ================================================================================================ Sicherheitsstempel

    [Fact]
    public async Task Sicherheitsstempel_aendert_sich_mit_Passwort_Rolle_Sperre_und_zweitem_Faktor()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var probst = await u.KontoAsync("probst");
        var s0 = await Mit(u, d => Konten(d).StempelAsync(probst.Id));
        s0.ShouldNotBeNullOrEmpty();
        s0.ShouldBe(probst.SicherheitsStempel);

        await MitPasswortAsync(u, "probst");
        var s1 = await Mit(u, d => Konten(d).StempelAsync(probst.Id));
        s1.ShouldNotBe(s0);

        await TotpEinrichtenAsync(u, probst.Id);
        var s2 = await Mit(u, d => Konten(d).StempelAsync(probst.Id));
        s2.ShouldNotBe(s1);

        // Änderung ohne sicherheitsrelevanten Inhalt (nur der Name): Stempel bleibt
        await u.AlsAsync("verwaltung");
        await u.InBereichAsync(d => Konten(d).AendernAsync(probst.Id, "Prof.", "Lena", "Probst", probst.Email, [Rolle.Pruefende], true));
        (await Mit(u, d => Konten(d).StempelAsync(probst.Id))).ShouldBe(s2);

        // Claims der Sitzung tragen den Stempel (Identity-Claim), Name = Anzeigename, Rollen
        var p = await Mit(u, async d => await d.GetRequiredService<IUserClaimsPrincipalFactory<Konto>>().CreateAsync(await Laden(d, probst.Id)));
        p.FindFirst(new ClaimsIdentityOptions().SecurityStampClaimType)!.Value.ShouldBe(s2);
        p.Identity!.Name.ShouldBe("Prof. Lena Probst");
        p.FindFirst(ClaimTypes.NameIdentifier)!.Value.ShouldBe(probst.Id.ToString("D"));
        p.IsInRole(nameof(Rolle.Pruefende)).ShouldBeTrue();
    }

    [Fact]
    public async Task Vorhandene_PBKDF2_Hashes_bleiben_gueltig_und_werden_erneuert()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var probst = await u.KontoAsync("probst");
        // Hash im bisherigen Format (Identity V3), mit weniger Iterationen als heute verlangt
        var alt = new PasswordHasher<Konto>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }));
        var altHash = alt.HashPassword(probst, Pw);
        await u.InBereichAsync(async d =>
        {
            var k = await Laden(d, probst.Id);
            k.PasswortSetzen(altHash);
            await d.GetRequiredService<IDaten>().SpeichernAsync();
        });

        (await Mit(u, d => Konten(d).PasswortPruefenAsync("probst", Pw))).Erfolg.ShouldBeTrue();
        var neu = (await u.KontoAsync("probst")).PasswortHash!;
        neu.ShouldNotBe(altHash);   // mit 210 000 Iterationen neu gehasht
        (await Mit(u, d => Konten(d).PasswortPruefenAsync("probst", Pw))).Erfolg.ShouldBeTrue();
    }

    // ================================================================================================ Zweiter Faktor (TOTP)

    [Theory]
    [InlineData(Rolle.Pruefende, Anmeldeart.Passwort, false, false, false)]
    [InlineData(Rolle.Pruefende, Anmeldeart.Passwort, false, true, true)]       // freiwillig eingerichtet: wird abgefragt
    [InlineData(Rolle.Systemverwaltung, Anmeldeart.Passwort, false, false, true)]
    [InlineData(Rolle.Studiendekanat, Anmeldeart.Passwort, true, false, true)]   // Passwort ist nie ein MFA-Nachweis
    [InlineData(Rolle.Studierendensekretariat, Anmeldeart.Passwort, false, false, false)]
    [InlineData(Rolle.Systemverwaltung, Anmeldeart.Saml, true, false, false)]    // REFEDS-MFA vom IdP
    [InlineData(Rolle.Systemverwaltung, Anmeldeart.Saml, false, false, true)]    // ohne Nachweis: eigener Faktor
    [InlineData(Rolle.Pruefende, Anmeldeart.Oidc, false, false, false)]
    [InlineData(Rolle.Studiendekanat, Anmeldeart.Oidc, false, false, true)]
    [InlineData(Rolle.Systemverwaltung, Anmeldeart.Passkey, true, false, false)] // Nutzerprüfung im Gerät
    [InlineData(Rolle.Systemverwaltung, Anmeldeart.Passkey, false, false, true)]
    [InlineData(Rolle.Systemverwaltung, Anmeldeart.Test, false, false, false)]
    public void Zweiter_Faktor_ist_Pflicht_fuer_Verwaltung_und_Studiendekanat(Rolle rolle, Anmeldeart art, bool mfa, bool eingerichtet, bool erwartet)
    {
        var k = new Konto("x-" + rolle, null, "X", "Y", "x@y.test");
        k.RolleGeben(rolle);
        k.ZweitenFaktorSetzen(eingerichtet);
        Anmelderegeln.BrauchtZweitenFaktor(k, art, mfa, new AnmeldeOptionen()).ShouldBe(erwartet);
    }

    [Fact]
    public void MFA_bei_SSO_laesst_sich_abschalten()
    {
        var k = new Konto("admin", null, "A", "B", "a@b.test");
        k.RolleGeben(Rolle.Systemverwaltung);
        Anmelderegeln.BrauchtZweitenFaktor(k, Anmeldeart.Saml, false, new AnmeldeOptionen { MfaBeiSsoVerlangen = false }).ShouldBeFalse();
        Anmelderegeln.BrauchtZweitenFaktor(k, Anmeldeart.Passwort, false, new AnmeldeOptionen { MfaBeiSsoVerlangen = false }).ShouldBeTrue();
    }

    [Fact]
    public async Task TOTP_einrichten_mit_QR_Inhalt_Code_und_Wiederherstellungscodes()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var admin = await MitPasswortAsync(u, "verwaltung");
        var einrichtung = await Mit(u, async d => await Sicherheit(d).AuthenticatorAsync(await Laden(d, admin.Id)));
        einrichtung.OtpauthUri.ShouldStartWith("otpauth://totp/ProfOMat:verwaltung?secret=");
        var geheim = einrichtung.Schluessel.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        (await u.KontoAsync("verwaltung")).AuthenticatorSchluessel!.ShouldNotContain(geheim);   // nur verschlüsselt gespeichert

        (await Mit(u, async d => await Sicherheit(d).ZweitenFaktorAktivierenAsync(await Laden(d, admin.Id), "000000"))).ShouldBeNull();
        var codes = await Mit(u, async d => await Sicherheit(d).ZweitenFaktorAktivierenAsync(await Laden(d, admin.Id), Totp.Code(geheim)));
        codes.ShouldNotBeNull();
        codes.Count.ShouldBe(SicherheitDienst.AnzahlWiederherstellungscodes);
        var konto = await u.KontoAsync("verwaltung");
        konto.ZweiterFaktorAktiv.ShouldBeTrue();
        konto.Wiederherstellungscodes!.ShouldNotContain(codes[0]);   // nur Hashes gespeichert

        // Code und Wiederherstellungscode prüft Identity; ein Wiederherstellungscode gilt nur einmal
        (await Mit(u, async d => await Identitaet(d).VerifyTwoFactorTokenAsync(await Laden(d, admin.Id), TokenOptions.DefaultAuthenticatorProvider, Totp.Code(geheim)))).ShouldBeTrue();
        (await Mit(u, async d => (await Identitaet(d).RedeemTwoFactorRecoveryCodeAsync(await Laden(d, admin.Id), codes[3])).Succeeded)).ShouldBeTrue();
        (await Mit(u, async d => (await Identitaet(d).RedeemTwoFactorRecoveryCodeAsync(await Laden(d, admin.Id), codes[3])).Succeeded)).ShouldBeFalse();
        (await u.KontoAsync("verwaltung")).WiederherstellungscodeHashes.Count.ShouldBe(SicherheitDienst.AnzahlWiederherstellungscodes - 1);

        // Pflicht für die Systemverwaltung: nicht abschaltbar
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, async d =>
        {
            await Sicherheit(d).ZweitenFaktorAbschaltenAsync(await Laden(d, admin.Id));
            return true;
        }))).Message.ShouldContain("Pflicht");
    }

    [Fact]
    public async Task Pruefende_koennen_den_freiwilligen_Faktor_abschalten_die_Verwaltung_setzt_ihn_zurueck()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var probst = await MitPasswortAsync(u, "probst");
        await TotpEinrichtenAsync(u, probst.Id);
        await u.AlsAsync("probst");
        await u.InBereichAsync(async d => await Sicherheit(d).ZweitenFaktorAbschaltenAsync(await Laden(d, probst.Id)));
        (await u.KontoAsync("probst")).ZweiterFaktorAktiv.ShouldBeFalse();

        await TotpEinrichtenAsync(u, probst.Id);
        foreach (var wer in new[] { "mustermann", "beispiel", "sekretariat" })
        {
            await u.AlsAsync(wer);
            await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => Konten(d).ZweitenFaktorZuruecksetzenAsync(probst.Id)));
        }

        await u.AlsAsync("verwaltung");
        var vorher = (await u.KontoAsync("probst")).SicherheitsStempel;
        await u.InBereichAsync(d => Konten(d).ZweitenFaktorZuruecksetzenAsync(probst.Id));
        var nachher = await u.KontoAsync("probst");
        nachher.ZweiterFaktorAktiv.ShouldBeFalse();
        nachher.AuthenticatorSchluessel.ShouldBeNull();
        nachher.SicherheitsStempel.ShouldNotBe(vorher);
        var admin = await u.KontoAsync("verwaltung");
        await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(d => Konten(d).ZweitenFaktorZuruecksetzenAsync(admin.Id)));   // nicht am eigenen Konto
    }

    [Fact]
    public async Task Selbstverwaltung_nur_am_eigenen_Konto()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var probst = await u.KontoAsync("probst");
        await u.AlsAsync("mustermann");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, async d => await Sicherheit(d).AuthenticatorAsync(await Laden(d, probst.Id))));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, async d => await Sicherheit(d).ZweitenFaktorAktivierenAsync(await Laden(d, probst.Id), "123456")));
        await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(async d =>
            await Sicherheit(d).AnmeldungVerknuepfenAsync(await Laden(d, probst.Id), Saml("probst@hs.test"))));

        // vor der Anmeldung (Zwischen-Cookie nach dem Passwort) darf die Einrichtung des Pflicht-Faktors laufen
        u.Benutzer.Aktuell = Benutzer.Niemand;
        (await Mit(u, async d => await Sicherheit(d).AuthenticatorAsync(await Laden(d, probst.Id)))).Schluessel.ShouldNotBeEmpty();
        await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(async d => await Sicherheit(d).ZweitenFaktorAbschaltenAsync(await Laden(d, probst.Id))));
    }

    // ================================================================================================ Verknüpfung mit Identitätsdiensten

    private static ExterneIdentitaet Saml(string kennung, bool mfa = false)
        => new(ExterneIdentitaet.Saml, kennung, "Lena", "Probst", "Lena Probst", "lp@hs.test", ["employee@hs-osnabrueck.de"], mfa);

    [Fact]
    public void SAML_Kennung_subject_id_vor_pairwise_id_vor_eppn_und_REFEDS_MFA()
    {
        var alle = new[]
        {
            new Claim(ExterneAntwort.AttrEppn, "lprobst@hs-osnabrueck.de"),
            new Claim(ExterneAntwort.AttrPairwiseId, "PAIR123@hs-osnabrueck.de"),
            new Claim(ExterneAntwort.AttrSubjectId, "Subj42@hs-osnabrueck.de"),
            new Claim(ExterneAntwort.AttrAffiliation, "employee@hs-osnabrueck.de"),
            new Claim(ExterneAntwort.AttrMail, "l.probst@hs-osnabrueck.de"),
            new Claim(ExterneAntwort.SamlAuthnKontext, ExterneAntwort.RefedsMfa),
        };
        var e = ExterneAntwort.AusSaml(alle)!;
        e.Schluessel.ShouldBe("subj42@hs-osnabrueck.de");
        e.MfaNachgewiesen.ShouldBeTrue();
        e.Zugehoerigkeiten.ShouldBe(["employee@hs-osnabrueck.de"]);
        ExterneAntwort.AusSaml(alle.Where(c => c.Type != ExterneAntwort.AttrSubjectId))!.Schluessel.ShouldBe("pair123@hs-osnabrueck.de");
        var nurEppn = ExterneAntwort.AusSaml(alle.Where(c => c.Type is not (ExterneAntwort.AttrSubjectId or ExterneAntwort.AttrPairwiseId or ExterneAntwort.SamlAuthnKontext)))!;
        nurEppn.Schluessel.ShouldBe("lprobst@hs-osnabrueck.de");
        nurEppn.MfaNachgewiesen.ShouldBeFalse();
        nurEppn.KennungsVorschlag().ShouldBe("lprobst");
        ExterneAntwort.AusSaml([new Claim(ExterneAntwort.AttrMail, "x@y")]).ShouldBeNull();

        // Passwort-Kontext ist kein zweiter Faktor; weitere Kontexte lassen sich konfigurieren
        var pw = new[] { new Claim(ExterneAntwort.AttrEppn, "a@b"), new Claim(ExterneAntwort.SamlAuthnKontext, "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport") };
        ExterneAntwort.AusSaml(pw)!.MfaNachgewiesen.ShouldBeFalse();
        ExterneAntwort.AusSaml(pw, ["urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport"])!.MfaNachgewiesen.ShouldBeTrue();
    }

    [Fact]
    public void OIDC_Kennung_sub_und_MFA_ueber_amr_oder_acr()
    {
        ExterneAntwort.AusOidc([new Claim("sub", "abc"), new Claim("amr", "pwd"), new Claim("amr", "mfa")])!.MfaNachgewiesen.ShouldBeTrue();
        ExterneAntwort.AusOidc([new Claim("sub", "abc"), new Claim("amr", "pwd")])!.MfaNachgewiesen.ShouldBeFalse();
        ExterneAntwort.AusOidc([new Claim("sub", "abc"), new Claim("acr", "gold")], ["gold"])!.MfaNachgewiesen.ShouldBeTrue();
        ExterneAntwort.AusOidc([new Claim("email", "x@y")]).ShouldBeNull();
        Anmelderegeln.ZugehoerigkeitErlaubt(["employee@hs-osnabrueck.de"], ["student@hs-osnabrueck.de"]).ShouldBeFalse();
        Anmelderegeln.ZugehoerigkeitErlaubt(["employee@hs-osnabrueck.de"], ["Employee@HS-Osnabrueck.de"]).ShouldBeTrue();
        Anmelderegeln.ZugehoerigkeitErlaubt([], ["student@x"]).ShouldBeTrue();
    }

    [Fact]
    public async Task Verknuepfen_ueber_dauerhafte_Kennung_einmalig_und_nicht_die_letzte_Anmeldung_trennen()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var probst = await u.KontoAsync("probst");
        await u.AlsAsync("probst");
        var vorher = probst.SicherheitsStempel;
        await u.InBereichAsync(async d => await Sicherheit(d).AnmeldungVerknuepfenAsync(await Laden(d, probst.Id), Saml("subj-probst@hs.test")));
        (await Mit(u, d => Identitaet(d).FindByLoginAsync(ExterneIdentitaet.Saml, "subj-probst@hs.test")))!.Id.ShouldBe(probst.Id);
        (await u.KontoAsync("probst")).SicherheitsStempel.ShouldBe(vorher);   // neuer Weg hinein beendet keine Sitzung, Trennen schon (unten)
        // noch einmal: nichts passiert
        await u.InBereichAsync(async d => await Sicherheit(d).AnmeldungVerknuepfenAsync(await Laden(d, probst.Id), Saml("subj-probst@hs.test")));
        (await u.KontoAsync("probst")).Anmeldungen.Count.ShouldBe(1);

        // dieselbe externe Kennung an einem zweiten Konto: abgelehnt
        var muster = await u.KontoAsync("mustermann");
        await u.AlsAsync("mustermann");
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(async d =>
            await Sicherheit(d).AnmeldungVerknuepfenAsync(await Laden(d, muster.Id), Saml("subj-probst@hs.test"))))).Message.ShouldContain("anderen");

        // Probst hat kein Passwort und keinen Passkey: die einzige Verknüpfung bleibt
        await u.AlsAsync("probst");
        var verknuepfung = (await u.KontoAsync("probst")).Anmeldungen.Single();
        (await Should.ThrowAsync<FachlicherFehler>(() => u.InBereichAsync(async d =>
            await Sicherheit(d).AnmeldungTrennenAsync(await Laden(d, probst.Id), verknuepfung.Id)))).Message.ShouldContain("letzte");
        await MitPasswortAsync(u, "probst");
        var mitPasswort = (await u.KontoAsync("probst")).SicherheitsStempel;
        await u.InBereichAsync(async d => await Sicherheit(d).AnmeldungTrennenAsync(await Laden(d, probst.Id), verknuepfung.Id));
        (await u.KontoAsync("probst")).SicherheitsStempel.ShouldNotBe(mitPasswort);
        (await Mit(u, d => Identitaet(d).FindByLoginAsync(ExterneIdentitaet.Saml, "subj-probst@hs.test"))).ShouldBeNull();
    }

    [Fact]
    public async Task Einladung_ueber_Identitaetsdienst_legt_Konto_ohne_Passwort_an()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("verwaltung");
        var link = await Mit(u, d => Konten(d).EinladungErzeugenAsync([Rolle.Pruefende, Rolle.Studiendekanat], "Neu", 7));
        var pwLink = await Mit(u, async d => await Konten(d).PasswortLinkErzeugenAsync((await u.KontoAsync("probst")).Id));
        u.Benutzer.Aktuell = Benutzer.Niemand;

        var ext = new ExterneIdentitaet(ExterneIdentitaet.Saml, "subj-neu@hs-osnabrueck.de", "Nora", "Neu", "Nora Neu", "n.neu@hs-osnabrueck.de", [], true);
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => Konten(d).EinladungMitIdentitaetsdienstEinloesenAsync(pwLink.Token, ext)));
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => Konten(d).EinladungMitIdentitaetsdienstEinloesenAsync(link.Token, ext with { Email = null })));

        var konto = await Mit(u, d => Konten(d).EinladungMitIdentitaetsdienstEinloesenAsync(link.Token, ext));
        konto.Kennung.ShouldBe("subj-neu");
        konto.Herkunft.ShouldBe(KontoHerkunft.Hochschule);
        konto.PasswortHash.ShouldBeNull();
        konto.Anzeigename.ShouldBe("Nora Neu");
        konto.Rollen.Select(r => r.Rolle).ShouldBe([Rolle.Pruefende, Rolle.Studiendekanat], ignoreOrder: true);
        (await Mit(u, d => Identitaet(d).FindByLoginAsync(ExterneIdentitaet.Saml, "subj-neu@hs-osnabrueck.de")))!.Id.ShouldBe(konto.Id);
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => Konten(d).EinladungMitIdentitaetsdienstEinloesenAsync(link.Token, ext)));   // nur einmal
        (await Mit(u, d => Konten(d).PasswortPruefenAsync("subj-neu", Pw))).Erfolg.ShouldBeFalse();   // kein Passwort
    }

    // ================================================================================================ Passkeys

    [Fact]
    public async Task Passkey_speichern_finden_und_entfernen()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var probst = await MitPasswortAsync(u, "probst");
        var id = RandomNumberGenerator.GetBytes(32);
        var info = new UserPasskeyInfo(id, RandomNumberGenerator.GetBytes(77), new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), 3, ["internal", "hybrid"],
            isUserVerified: true, isBackupEligible: true, isBackedUp: false, attestationObject: [1, 2, 3], clientDataJson: [4, 5]);
        await u.InBereichAsync(async d => await Sicherheit(d).PasskeyHinzufuegenAsync(await Laden(d, probst.Id), info, "Dienstlaptop"));

        (await Mit(u, d => Identitaet(d).FindByPasskeyIdAsync(id)))!.Id.ShouldBe(probst.Id);
        var gespeichert = (await Mit(u, async d => await Identitaet(d).GetPasskeysAsync(await Laden(d, probst.Id)))).Single();
        gespeichert.CredentialId.ShouldBe(id);
        gespeichert.Name.ShouldBe("Dienstlaptop");
        gespeichert.SignCount.ShouldBe(3u);
        gespeichert.Transports.ShouldBe(["internal", "hybrid"]);
        gespeichert.IsUserVerified.ShouldBeTrue();

        // Zähler fortschreiben (wie nach jeder Anmeldung)
        var weiter = new UserPasskeyInfo(id, info.PublicKey, info.CreatedAt, 9, info.Transports, true, true, true, info.AttestationObject, info.ClientDataJson);
        await u.InBereichAsync(async d => (await Identitaet(d).AddOrUpdatePasskeyAsync(await Laden(d, probst.Id), weiter)).Succeeded.ShouldBeTrue());
        var konto = await u.KontoAsync("probst");
        konto.Passkeys.Single().Zaehler.ShouldBe(9);
        konto.Passkeys.Single().Bezeichnung.ShouldBe("Dienstlaptop");

        await u.InBereichAsync(async d => await Sicherheit(d).PasskeyEntfernenAsync(await Laden(d, probst.Id), konto.Passkeys.Single().Id));
        (await Mit(u, d => Identitaet(d).FindByPasskeyIdAsync(id))).ShouldBeNull();
    }

    // ================================================================================================ Verfahren je Instanz

    [Fact]
    public void Verfahren_Vorgabe_und_Schalter()
    {
        new AnmeldeOptionen().Wirksam(false, false).ShouldBe([Anmeldeverfahren.Passwort, Anmeldeverfahren.Passkey], ignoreOrder: true);
        new AnmeldeOptionen().Wirksam(true, true).ShouldBe([Anmeldeverfahren.Saml, Anmeldeverfahren.Oidc, Anmeldeverfahren.Passwort, Anmeldeverfahren.Passkey], ignoreOrder: true);
        new AnmeldeOptionen { Verfahren = "saml, passkey" }.Wirksam(true, true).ShouldBe([Anmeldeverfahren.Saml, Anmeldeverfahren.Passkey], ignoreOrder: true);
        new AnmeldeOptionen { Verfahren = "Passwort" }.Wirksam(true, false).ShouldBe([Anmeldeverfahren.Passwort]);
        Should.Throw<InvalidOperationException>(() => new AnmeldeOptionen { Verfahren = "Saml,Passwort" }.Wirksam(false, false)).Message.ShouldContain("Saml:IdpEntityId");
        Should.Throw<InvalidOperationException>(() => new AnmeldeOptionen { Verfahren = "Oidc" }.Wirksam(true, false)).Message.ShouldContain("Oidc:Authority");
        Should.Throw<InvalidOperationException>(() => new AnmeldeOptionen { Verfahren = "Passwort,Fax" }.Wirksam(true, true)).Message.ShouldContain("Fax");
    }

    [Fact]
    public async Task Sperre_nach_Fehlversuchen_ist_einstellbar()
    {
        await using var u = await TestUmgebung.StartenAsync(new Dictionary<string, string?> { ["Anmeldung:SperreNachFehlversuchen"] = "2" });
        await MitPasswortAsync(u, "probst");
        (await Mit(u, d => Konten(d).PasswortPruefenAsync("probst", "falsch-falsch"))).Ergebnis.ShouldBe(Passwortergebnis.Falsch);
        (await Mit(u, d => Konten(d).PasswortPruefenAsync("probst", "falsch-falsch"))).Ergebnis.ShouldBe(Passwortergebnis.VoruebergehendGesperrt);
    }

    // ================================================================================================ Hilfen

    private static async Task TotpEinrichtenAsync(TestUmgebung u, Guid kontoId)
    {
        var vorher = u.Benutzer.Aktuell;
        u.Benutzer.Aktuell = KontoDienst.AlsBenutzer(await u.InBereichAsync(d => Laden(d, kontoId)));
        var e = await Mit(u, async d => await Sicherheit(d).AuthenticatorAsync(await Laden(d, kontoId)));
        var geheim = e.Schluessel.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        (await Mit(u, async d => await Sicherheit(d).ZweitenFaktorAktivierenAsync(await Laden(d, kontoId), Totp.Code(geheim)))).ShouldNotBeNull();
        u.Benutzer.Aktuell = vorher;
    }
}

/// <summary>TOTP nach RFC 6238 (SHA-1, 6 Stellen, 30 s) – wie eine Authenticator-App.</summary>
internal static class Totp
{
    public static string Code(string base32, DateTimeOffset? zeit = null)
    {
        var schluessel = Base32(base32);
        var schritt = (zeit ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var zaehler = BitConverter.GetBytes(schritt);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(zaehler);
        }

        var hash = HMACSHA1.HashData(schluessel, zaehler);
        var o = hash[^1] & 0x0F;
        var wert = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (wert % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string s)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var puffer = 0;
        var ergebnis = new List<byte>();
        foreach (var c in s.TrimEnd('='))
        {
            puffer = (puffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                ergebnis.Add((byte)(puffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. ergebnis];
    }
}
