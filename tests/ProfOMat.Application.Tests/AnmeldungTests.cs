using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ProfOMat.Application.Betrieb;
using ProfOMat.Application.Konten;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Tests;

/// <summary>Anmeldung wie in Podium: Ersteinrichtung, Einladungs- und Passwort-Links, Sitzungsstempel.</summary>
public sealed class AnmeldungTests
{
    private const string Pw = "ein-langes-Passwort-1";

    private static Task<T> Mit<T>(TestUmgebung u, Func<KontoDienst, Task<T>> arbeit)
        => u.InBereichAsync(d => arbeit(d.GetRequiredService<KontoDienst>()));

    /// <summary>Umgebung ohne Konten (wie ein frisch eingerichteter Server ohne Demodaten).</summary>
    private static async Task<TestUmgebung> LeerAsync()
    {
        var u = await TestUmgebung.StartenAsync(demodaten: false);
        u.Benutzer.Aktuell = Benutzer.Niemand;
        return u;
    }

    [Fact]
    public async Task Ersteinrichtung_nur_einmal_und_nur_ohne_Konten()
    {
        await using var u = await LeerAsync();
        (await Mit(u, d => d.ErsteinrichtungNoetigAsync())).ShouldBeTrue();
        Should.Throw<FachlicherFehler>(() => Mit(u, d => d.ErsteinrichtenAsync(new Kontoangaben("liebler", "Prof. Dr.", "Klaus", "Liebler", "kl@hs.test"), "kurz", true)))
            .Message.ShouldContain("12 Zeichen");
        Should.Throw<FachlicherFehler>(() => Mit(u, d => d.ErsteinrichtenAsync(new Kontoangaben("L iebler", null, "K", "L", "kl@hs.test"), Pw, true)))
            .Message.ShouldContain("Kennung");

        var b = await Mit(u, d => d.ErsteinrichtenAsync(new Kontoangaben("Liebler", "Prof. Dr.", "Klaus", "Liebler", "kl@hs.test"), Pw, true));
        b.Kennung.ShouldBe("liebler");
        b.Rollen.ShouldBe([Rolle.Pruefende, Rolle.Systemverwaltung], ignoreOrder: true);
        (await Mit(u, d => d.ErsteinrichtungNoetigAsync())).ShouldBeFalse();
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.ErsteinrichtenAsync(new Kontoangaben("boese", null, "B", "Böse", "b@x.test"), Pw, false))))
            .Message.ShouldContain("bereits eingerichtet");
        (await Mit(u, d => d.PasswortAnmeldenAsync("LIEBLER", Pw)))!.Kennung.ShouldBe("liebler");
    }

    [Fact]
    public async Task Einladung_legt_Konto_mit_Rollen_an_und_gilt_nur_einmal()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("verwaltung");
        var link = await Mit(u, d => d.EinladungErzeugenAsync([Rolle.Pruefende], "Niemeyer", 7));
        link.Token.Length.ShouldBeGreaterThan(40);
        link.Einladung.TokenHash.ShouldBe(KontoDienst.TokenHash(link.Token));
        link.Einladung.TokenHash.ShouldNotContain(link.Token);   // nur der Hash liegt in der Datenbank
        (await Mit(u, d => d.OffeneEinladungenAsync())).Single().Notiz.ShouldBe("Niemeyer");

        u.Benutzer.Aktuell = Benutzer.Niemand;   // Einlösen ohne Anmeldung
        (await Mit(u, d => d.EinladungPruefenAsync("falsch"))).ShouldBeNull();
        var info = (await Mit(u, d => d.EinladungPruefenAsync(link.Token)))!;
        info.Art.ShouldBe(EinladungsArt.Konto);
        info.Rollen.ShouldBe([Rolle.Pruefende]);
        (await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EinladungEinloesenAsync(link.Token,
            new Kontoangaben("mustermann", null, "X", "Y", "x@y.test"), Pw)))).Message.ShouldContain("schon vergeben");
        var neu = await Mit(u, d => d.EinladungEinloesenAsync(link.Token, new Kontoangaben("pniemeyer", "Prof. Dr.", "Philip", "Niemeyer", "pn@hs.test"), Pw));
        neu.Kennung.ShouldBe("pniemeyer");
        neu.Rollen.ShouldBe([Rolle.Pruefende]);
        (await Mit(u, d => d.EinladungPruefenAsync(link.Token))).ShouldBeNull();
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EinladungEinloesenAsync(link.Token, new Kontoangaben("zweiter", null, "Z", "Z", "z@z.test"), Pw)));
    }

    [Fact]
    public async Task Einladung_laeuft_ab_und_kann_zurueckgezogen_werden()
    {
        await using var u = await TestUmgebung.StartenAsync();
        await u.AlsAsync("verwaltung");
        var kurz = await Mit(u, d => d.EinladungErzeugenAsync([Rolle.Pruefende], null, 1));
        var weg = await Mit(u, d => d.EinladungErzeugenAsync([Rolle.Pruefende], null, 30));
        await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().EinladungZurueckziehenAsync(weg.Einladung.Id));
        u.Uhr.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        (await Mit(u, d => d.EinladungPruefenAsync(kurz.Token))).ShouldBeNull();
        (await Mit(u, d => d.EinladungPruefenAsync(weg.Token))).ShouldBeNull();
    }

    [Fact]
    public async Task Passwort_Link_setzt_neues_Passwort_und_beendet_alte_Sitzungen()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var probst = await u.KontoAsync("probst");
        var vorher = await Mit(u, d => d.StempelAsync(probst.Id));
        await u.AlsAsync("verwaltung");
        var link = await Mit(u, d => d.PasswortLinkErzeugenAsync(probst.Id));
        u.Benutzer.Aktuell = Benutzer.Niemand;
        (await Mit(u, d => d.EinladungPruefenAsync(link.Token)))!.Kennung.ShouldBe("probst");
        await Mit(u, d => d.EinladungEinloesenAsync(link.Token, null, Pw));
        (await Mit(u, d => d.PasswortAnmeldenAsync("probst", Pw))).ShouldNotBeNull();
        (await Mit(u, d => d.StempelAsync(probst.Id))).ShouldNotBe(vorher);
    }

    [Fact]
    public async Task Sperre_und_Rollenaenderung_aendern_den_Stempel()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var probst = await u.KontoAsync("probst");
        var s1 = await Mit(u, d => d.StempelAsync(probst.Id));
        await u.AlsAsync("verwaltung");
        await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().AendernAsync(probst.Id, probst.Titel, probst.Vorname, probst.Nachname, probst.Email,
            [Rolle.Pruefende, Rolle.Studiendekanat], true));
        var s2 = await Mit(u, d => d.StempelAsync(probst.Id));
        s2.ShouldNotBe(s1);
        await u.InBereichAsync(d => d.GetRequiredService<KontoDienst>().AendernAsync(probst.Id, probst.Titel, probst.Vorname, probst.Nachname, probst.Email,
            [Rolle.Pruefende, Rolle.Studiendekanat], false));
        (await Mit(u, d => d.StempelAsync(probst.Id))).ShouldBeNull();
    }

    [Fact]
    public async Task Links_erzeugt_nur_die_Systemverwaltung()
    {
        await using var u = await TestUmgebung.StartenAsync();
        var probst = await u.KontoAsync("probst");
        foreach (var wer in new[] { "mustermann", "sekretariat" })
        {
            await u.AlsAsync(wer);
            await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EinladungErzeugenAsync([Rolle.Systemverwaltung], null, 7)));
            await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.PasswortLinkErzeugenAsync(probst.Id)));
            await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.OffeneEinladungenAsync()));
        }

        await u.AlsAsync("verwaltung");
        await Should.ThrowAsync<FachlicherFehler>(() => Mit(u, d => d.EinladungErzeugenAsync([], null, 7)));
    }
}

public sealed class MenschenpruefungTests
{
    private static (Menschenpruefung P, FakeTimeProvider Uhr) Neu()
    {
        var uhr = new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        return (new Menschenpruefung(uhr) { Maximum = 5_000 }, uhr);
    }

    [Fact]
    public void Richtige_Loesung_gilt_genau_einmal()
    {
        var (p, _) = Neu();
        var l = Menschenpruefung.Loesen(p.Neu());
        p.Pruefen(l);
        Should.Throw<FachlicherFehler>(() => p.Pruefen(l));
    }

    [Fact]
    public void Falsche_manipulierte_abgelaufene_oder_fehlende_Loesungen_scheitern()
    {
        var (p, uhr) = Neu();
        var a = p.Neu();
        var l = Menschenpruefung.Loesen(a);
        Should.Throw<FachlicherFehler>(() => p.Pruefen(null));
        Should.Throw<FachlicherFehler>(() => p.Pruefen(l with { Zahl = (l.Zahl + 1) % 5_000 }));
        Should.Throw<FachlicherFehler>(() => p.Pruefen(l with { Signatur = new string('0', 64) }));
        Should.Throw<FachlicherFehler>(() => p.Pruefen(l with { Salz = l.Salz.Replace(".", "0.", StringComparison.Ordinal) }));
        Should.Throw<FachlicherFehler>(() => p.Pruefen(l with { Koeder = "https://spam.example" }));
        Should.Throw<FachlicherFehler>(() => new Menschenpruefung(uhr).Pruefen(l));   // anderer Schlüssel (z. B. nach Neustart)
        uhr.Advance(Menschenpruefung.Gueltigkeit + TimeSpan.FromSeconds(1));
        Should.Throw<FachlicherFehler>(() => p.Pruefen(l)).Message.ShouldContain("abgelaufen");
    }
}
