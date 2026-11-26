using System.Globalization;
using Microsoft.AspNetCore.Identity;
using ProfOMat.Application.Betrieb;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Konten;

/// <summary>Geheimnis für die Authenticator-App: zum Abtippen gruppiert und als otpauth-URI für den QR-Code.</summary>
public sealed record AuthenticatorEinrichtung(string Schluessel, string OtpauthUri);

/// <summary>
/// Selbstverwaltung der eigenen Anmeldung über ASP.NET Core Identity: zweiter Faktor (TOTP) mit Wiederherstellungscodes, Passkeys und
/// verknüpfte Identitätsdienste. Alles nur am eigenen Konto: Ist jemand angemeldet, muss es dessen Konto sein. Ohne Sitzung (Einrichtung
/// des Pflicht-Faktors direkt nach Passwort oder SSO) übergibt die Anmeldeseite das Konto aus dem kurzlebigen Zwischen-Cookie von Identity.
/// </summary>
public sealed class SicherheitDienst(UserManager<Konto> identitaet, IAktuellerBenutzer aktuell, Protokoll protokoll, IDaten daten, ISicherheitsprotokoll sicherheit)
{
    public const int AnzahlWiederherstellungscodes = 10;
    public const string Aussteller = "ProfOMat";

    public async Task<AuthenticatorEinrichtung> AuthenticatorAsync(Konto konto, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(konto);
        await EigenesKontoOderVorAnmeldungAsync(konto, abbruch);
        var schluessel = await identitaet.GetAuthenticatorKeyAsync(konto);
        if (string.IsNullOrEmpty(schluessel))
        {
            await identitaet.ResetAuthenticatorKeyAsync(konto);
            schluessel = await identitaet.GetAuthenticatorKeyAsync(konto) ?? throw new InvalidOperationException("Kein Authenticator-Schlüssel.");
        }

        var uri = string.Create(CultureInfo.InvariantCulture,
            $"otpauth://totp/{Uri.EscapeDataString(Aussteller)}:{Uri.EscapeDataString(konto.Kennung)}?secret={schluessel}&issuer={Uri.EscapeDataString(Aussteller)}&digits=6");
        return new AuthenticatorEinrichtung(Gruppiert(schluessel), uri);
    }

    /// <summary>Code aus der App prüfen und den zweiten Faktor einschalten; liefert die Wiederherstellungscodes (nur jetzt sichtbar) oder <c>null</c> bei falschem Code.</summary>
    public async Task<IReadOnlyList<string>?> ZweitenFaktorAktivierenAsync(Konto konto, string? code, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(konto);
        await EigenesKontoOderVorAnmeldungAsync(konto, abbruch);
        var sauber = Code(code);
        if (sauber.Length == 0 || !await identitaet.VerifyTwoFactorTokenAsync(konto, identitaet.Options.Tokens.AuthenticatorTokenProvider, sauber))
        {
            return null;
        }

        await identitaet.SetTwoFactorEnabledAsync(konto, true);
        var codes = (await identitaet.GenerateNewTwoFactorRecoveryCodesAsync(konto, AnzahlWiederherstellungscodes))?.ToList() ?? [];
        protokoll.Eintragen(KontoDienst.AlsBenutzer(konto), "Konto.ZweiterFaktor", "Zweiter Faktor (Authenticator-App) eingerichtet", konto.Id);
        await sicherheit.EintragenAsync("Konto.Sicherheit", konto.Id, "Zweiter Faktor (Authenticator-App) eingerichtet", abbruch);
        await daten.SpeichernAsync(abbruch);
        return codes;
    }

    public async Task<IReadOnlyList<string>> WiederherstellungscodesNeuAsync(Konto konto, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(konto);
        await EigenesKontoAsync(konto, abbruch);
        if (!konto.ZweiterFaktorAktiv)
        {
            throw new FachlicherFehler("Zuerst den zweiten Faktor einrichten.");
        }

        var codes = (await identitaet.GenerateNewTwoFactorRecoveryCodesAsync(konto, AnzahlWiederherstellungscodes))?.ToList() ?? [];
        protokoll.Eintragen(KontoDienst.AlsBenutzer(konto), "Konto.ZweiterFaktor", "Neue Wiederherstellungscodes erzeugt", konto.Id);
        await sicherheit.EintragenAsync("Konto.Sicherheit", konto.Id, "Neue Wiederherstellungscodes erzeugt", abbruch);
        await daten.SpeichernAsync(abbruch);
        return codes;
    }

    /// <summary>Zweiten Faktor ausschalten – nicht für Rollen, bei denen er Pflicht ist.</summary>
    public async Task ZweitenFaktorAbschaltenAsync(Konto konto, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(konto);
        await EigenesKontoAsync(konto, abbruch);
        if (Anmelderegeln.ZweiterFaktorPflicht(konto))
        {
            throw new FachlicherFehler("Für Systemverwaltung und Studiendekanat ist der zweite Faktor Pflicht.");
        }

        konto.ZweitenFaktorZuruecksetzen();
        protokoll.Eintragen(KontoDienst.AlsBenutzer(konto), "Konto.ZweiterFaktor", "Zweiter Faktor ausgeschaltet", konto.Id);
        await sicherheit.EintragenAsync("Konto.Sicherheit", konto.Id, "Zweiter Faktor ausgeschaltet", abbruch);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Passkey hinzufügen, nachdem Identity die Attestierung geprüft hat.</summary>
    public async Task PasskeyHinzufuegenAsync(Konto konto, UserPasskeyInfo passkey, string? bezeichnung, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(konto);
        ArgumentNullException.ThrowIfNull(passkey);
        await EigenesKontoAsync(konto, abbruch);
        passkey.Name = string.IsNullOrWhiteSpace(bezeichnung) ? "Passkey" : bezeichnung.Trim();
        var ergebnis = await identitaet.AddOrUpdatePasskeyAsync(konto, passkey);
        if (!ergebnis.Succeeded)
        {
            throw new FachlicherFehler("Der Passkey ließ sich nicht speichern.");
        }

        protokoll.Eintragen(KontoDienst.AlsBenutzer(konto), "Konto.Passkey", "Passkey hinzugefügt", konto.Id);
        await sicherheit.EintragenAsync("Konto.Sicherheit", konto.Id, "Passkey hinzugefügt", abbruch);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task PasskeyEntfernenAsync(Konto konto, Guid passkeyId, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(konto);
        await EigenesKontoAsync(konto, abbruch);
        var p = konto.Passkeys.FirstOrDefault(x => x.Id == passkeyId) ?? throw new FachlicherFehler("Passkey nicht gefunden.");
        if (!HatWeitereAnmeldung(konto, ohnePasskey: p.Id))
        {
            throw new FachlicherFehler("Das ist die letzte Möglichkeit, sich anzumelden – zuerst ein Passwort setzen oder ein Konto verknüpfen.");
        }

        await identitaet.RemovePasskeyAsync(konto, p.CredentialId);
        protokoll.Eintragen(KontoDienst.AlsBenutzer(konto), "Konto.Passkey", "Passkey entfernt", konto.Id);
        await sicherheit.EintragenAsync("Konto.Sicherheit", konto.Id, "Passkey entfernt", abbruch);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Konto bei einem Identitätsdienst mit dem eigenen ProfOMat-Konto verknüpfen.</summary>
    public async Task AnmeldungVerknuepfenAsync(Konto konto, ExterneIdentitaet ext, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(konto);
        ArgumentNullException.ThrowIfNull(ext);
        await EigenesKontoAsync(konto, abbruch);
        if (await identitaet.FindByLoginAsync(ext.Anbieter, ext.Schluessel) is { } vorhanden)
        {
            if (vorhanden.Id == konto.Id)
            {
                return;
            }

            throw new FachlicherFehler("Dieses Konto des Identitätsdienstes ist schon mit einem anderen ProfOMat-Konto verknüpft.");
        }

        var ergebnis = await identitaet.AddLoginAsync(konto, new UserLoginInfo(ext.Anbieter, ext.Schluessel, Anzeige(ext.Anbieter)));
        if (!ergebnis.Succeeded)
        {
            throw new FachlicherFehler("Die Verknüpfung ließ sich nicht speichern.");
        }

        protokoll.Eintragen(KontoDienst.AlsBenutzer(konto), "Konto.Verknuepft", $"Mit {ext.Anbieter} verknüpft", konto.Id);
        await sicherheit.EintragenAsync("Konto.Sicherheit", konto.Id, $"Mit {ext.Anbieter} verknüpft", abbruch);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task AnmeldungTrennenAsync(Konto konto, Guid anmeldungId, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(konto);
        await EigenesKontoAsync(konto, abbruch);
        var a = konto.Anmeldungen.FirstOrDefault(x => x.Id == anmeldungId) ?? throw new FachlicherFehler("Verknüpfung nicht gefunden.");
        if (!HatWeitereAnmeldung(konto, ohneAnmeldung: a.Id))
        {
            throw new FachlicherFehler("Das ist die letzte Möglichkeit, sich anzumelden – zuerst ein Passwort setzen oder einen Passkey hinzufügen.");
        }

        await identitaet.RemoveLoginAsync(konto, a.Anbieter, a.Schluessel);
        protokoll.Eintragen(KontoDienst.AlsBenutzer(konto), "Konto.Verknuepft", $"Verknüpfung mit {a.Anbieter} getrennt", konto.Id);
        await sicherheit.EintragenAsync("Konto.Sicherheit", konto.Id, $"Verknüpfung mit {a.Anbieter} getrennt", abbruch);
        await daten.SpeichernAsync(abbruch);
    }

    public static string Anzeige(string anbieter) => anbieter switch
    {
        ExterneIdentitaet.Saml => "Hochschulkonto (SAML)",
        ExterneIdentitaet.Oidc => "Firmenkonto (OIDC)",
        _ => anbieter,
    };

    private static bool HatWeitereAnmeldung(Konto k, Guid? ohnePasskey = null, Guid? ohneAnmeldung = null)
        => k.PasswortHash is not null || k.Passkeys.Any(p => p.Id != ohnePasskey) || k.Anmeldungen.Any(a => a.Id != ohneAnmeldung);

    private static string Code(string? code) => new((code ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());

    private static string Gruppiert(string s)
        => string.Join(' ', Enumerable.Range(0, (s.Length + 3) / 4).Select(i => s.Substring(i * 4, Math.Min(4, s.Length - (i * 4))))).ToLowerInvariant();

    private async Task EigenesKontoAsync(Konto konto, CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        if (wer.KontoId != konto.Id)
        {
            throw new FachlicherFehler("Nur am eigenen Konto möglich.");
        }
    }

    private async Task EigenesKontoOderVorAnmeldungAsync(Konto konto, CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        if (wer.Angemeldet && wer.KontoId != konto.Id)
        {
            throw new FachlicherFehler("Nur am eigenen Konto möglich.");
        }
    }
}
