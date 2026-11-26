using System.Security.Claims;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Konten;

/// <summary>Anmeldeverfahren, die eine Instanz freischalten kann (Kacheln auf der Anmeldeseite).</summary>
public enum Anmeldeverfahren
{
    /// <summary>Hochschul-, Firmen- oder Behördenkonto über SAML 2.0 (Shibboleth/DFN-AAI, Entra ID, ADFS …).</summary>
    Saml = 1,

    /// <summary>Firmen- oder Behördenkonto über OpenID Connect (Entra ID, Keycloak, Google Workspace …).</summary>
    Oidc = 2,

    /// <summary>Kennung und Passwort; für Systemverwaltung und Studiendekanat zusätzlich TOTP (Authenticator-App).</summary>
    Passwort = 3,

    /// <summary>Passkey (WebAuthn): phishing-sicher, ohne Passwort.</summary>
    Passkey = 4,
}

/// <summary>
/// Abschnitt „Anmeldung“ der Konfiguration. Ohne <see cref="Verfahren"/> gelten Passwort und Passkey, dazu SAML, sobald
/// <c>Saml:IdpEntityId</c> gesetzt ist, und OIDC, sobald <c>Oidc:Authority</c> gesetzt ist. Mit <see cref="Verfahren"/> (z. B. „Saml,Passwort“)
/// gelten genau die genannten; fehlt dafür die nötige Konfiguration, startet die Anwendung nicht.
/// </summary>
public sealed class AnmeldeOptionen
{
    public const string Abschnitt = "Anmeldung";

    /// <summary>Kommagetrennt: Saml, Oidc, Passwort, Passkey. Leer = Vorgabe (siehe Klasse).</summary>
    public string? Verfahren { get; set; }

    /// <summary>Nur Entwicklung: Konto ohne Passwort wählen. Das Ausrollen bricht ab, wenn sie eingeschaltet ist.</summary>
    public bool TestanmeldungErlaubt { get; set; }

    /// <summary>Bei SAML/OIDC für Systemverwaltung und Studiendekanat einen zweiten Faktor beim Identitätsdienst verlangen (REFEDS-MFA bzw. „amr“).
    /// Fehlt der Nachweis, fragt ProfOMat den eigenen zweiten Faktor (TOTP) ab.</summary>
    public bool MfaBeiSsoVerlangen { get; set; } = true;

    /// <summary>Sperre nach so vielen Fehlversuchen in Folge (Passwort oder Code).</summary>
    public int SperreNachFehlversuchen { get; set; } = 5;

    public int SperreMinuten { get; set; } = 15;

    /// <summary>Wirksame Verfahren dieser Instanz.</summary>
    /// <exception cref="InvalidOperationException">Unbekanntes Verfahren oder fehlende Konfiguration für ein genanntes.</exception>
    public IReadOnlySet<Anmeldeverfahren> Wirksam(bool samlKonfiguriert, bool oidcKonfiguriert)
    {
        if (string.IsNullOrWhiteSpace(Verfahren))
        {
            var vorgabe = new HashSet<Anmeldeverfahren> { Anmeldeverfahren.Passwort, Anmeldeverfahren.Passkey };
            if (samlKonfiguriert)
            {
                vorgabe.Add(Anmeldeverfahren.Saml);
            }

            if (oidcKonfiguriert)
            {
                vorgabe.Add(Anmeldeverfahren.Oidc);
            }

            return vorgabe;
        }

        var ergebnis = new HashSet<Anmeldeverfahren>();
        foreach (var teil in Verfahren.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<Anmeldeverfahren>(teil, ignoreCase: true, out var v) || !Enum.IsDefined(v))
            {
                throw new InvalidOperationException($"Anmeldung:Verfahren: unbekanntes Verfahren „{teil}“ (erlaubt: Saml, Oidc, Passwort, Passkey).");
            }

            ergebnis.Add(v);
        }

        if (ergebnis.Contains(Anmeldeverfahren.Saml) && !samlKonfiguriert)
        {
            throw new InvalidOperationException("Anmeldung:Verfahren nennt Saml, aber Saml:IdpEntityId ist nicht gesetzt.");
        }

        if (ergebnis.Contains(Anmeldeverfahren.Oidc) && !oidcKonfiguriert)
        {
            throw new InvalidOperationException("Anmeldung:Verfahren nennt Oidc, aber Oidc:Authority und Oidc:ClientId sind nicht gesetzt.");
        }

        return ergebnis;
    }
}

/// <summary>Was ein Identitätsdienst (SAML/OIDC) über die Person übermittelt hat – schon auf das Nötige reduziert.</summary>
/// <param name="Anbieter">„SAML“ oder „OIDC“.</param>
/// <param name="Schluessel">Dauerhafte Kennung (subject-id, pairwise-id, eppn bzw. „sub“).</param>
public sealed record ExterneIdentitaet(string Anbieter, string Schluessel, string? Vorname, string? Nachname, string? Anzeigename, string? Email,
    IReadOnlyList<string> Zugehoerigkeiten, bool MfaNachgewiesen)
{
    public const string Saml = "SAML";
    public const string Oidc = "OIDC";

    /// <summary>Vorschlag für die ProfOMat-Kennung: lokaler Teil der Kennung bzw. Mailadresse, nur erlaubte Zeichen.</summary>
    public string KennungsVorschlag()
    {
        var roh = (Schluessel.Contains('@', StringComparison.Ordinal) ? Schluessel : Email ?? Schluessel).Split('@')[0].ToLowerInvariant();
        var basis = new string(roh.Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-').ToArray());
        if (basis.Length < 3)
        {
            basis = "sso-" + basis;
        }

        return basis.Length > 36 ? basis[..36] : basis;
    }
}

/// <summary>Regeln der Anmeldung, unabhängig von HTTP – damit sie sich testen lassen.</summary>
public static class Anmelderegeln
{
    /// <summary>Rollen mit Verwaltungs- oder Freigaberechten: zweiter Faktor Pflicht.</summary>
    public static readonly IReadOnlySet<Rolle> RollenMitPflichtZumZweitenFaktor = new HashSet<Rolle> { Rolle.Systemverwaltung, Rolle.Studiendekanat };

    public static bool ZweiterFaktorPflicht(IEnumerable<Rolle> rollen) => rollen.Any(RollenMitPflichtZumZweitenFaktor.Contains);

    public static bool ZweiterFaktorPflicht(Konto konto)
    {
        ArgumentNullException.ThrowIfNull(konto);
        return ZweiterFaktorPflicht(konto.Rollen.Select(r => r.Rolle));
    }

    /// <summary>Muss nach der ersten Stufe noch der eigene zweite Faktor (TOTP) abgefragt bzw. eingerichtet werden?</summary>
    /// <param name="mfaNachgewiesen">Bei SSO: der Identitätsdienst hat einen zweiten Faktor bestätigt; bei Passkeys: Nutzerprüfung im Gerät.</param>
    public static bool BrauchtZweitenFaktor(Konto konto, Anmeldeart art, bool mfaNachgewiesen, AnmeldeOptionen optionen)
    {
        ArgumentNullException.ThrowIfNull(konto);
        ArgumentNullException.ThrowIfNull(optionen);
        var pflicht = ZweiterFaktorPflicht(konto);
        return art switch
        {
            Anmeldeart.Test => false,
            Anmeldeart.Passwort => pflicht || konto.ZweiterFaktorAktiv,
            Anmeldeart.Passkey => pflicht && !mfaNachgewiesen,
            Anmeldeart.Saml or Anmeldeart.Oidc => pflicht && optionen.MfaBeiSsoVerlangen && !mfaNachgewiesen,
            _ => true,
        };
    }

    /// <summary>Zugehörigkeit erlaubt? Leere Liste = alle (z. B. OIDC ohne Zugehörigkeitsangabe); Vergleich ohne Groß-/Kleinschreibung.</summary>
    public static bool ZugehoerigkeitErlaubt(IEnumerable<string> erlaubt, IEnumerable<string> vorhanden)
    {
        var liste = erlaubt.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
        return liste.Count == 0 || vorhanden.Any(v => liste.Contains(v.Trim(), StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>Wie die Sitzung zustande kam; steht als Authentifizierungsmethode im Anmelde-Cookie.</summary>
public enum Anmeldeart
{
    Passwort = 1,
    Passkey = 2,
    Saml = 3,
    Oidc = 4,
    Test = 5,
}

/// <summary>Auswertung der Antworten von Identitätsdiensten (SAML-Attribute, OIDC-Claims).</summary>
public static class ExterneAntwort
{
    // SAML-Attributnamen (urn:oid-Form, wie Shibboleth sie ausliefert)
    public const string AttrSubjectId = "urn:oasis:names:tc:SAML:attribute:subject-id";
    public const string AttrPairwiseId = "urn:oasis:names:tc:SAML:attribute:pairwise-id";
    public const string AttrEppn = "urn:oid:1.3.6.1.4.1.5923.1.1.1.6";
    public const string AttrAffiliation = "urn:oid:1.3.6.1.4.1.5923.1.1.1.9";
    public const string AttrDisplayName = "urn:oid:2.16.840.1.113730.3.1.241";
    public const string AttrGivenName = "urn:oid:2.5.4.42";
    public const string AttrSurname = "urn:oid:2.5.4.4";
    public const string AttrMail = "urn:oid:0.9.2342.19200300.100.1.3";

    /// <summary>Authentifizierungskontext der SAML-Antwort (AuthnContextClassRef).</summary>
    public const string SamlAuthnKontext = ClaimTypes.AuthenticationMethod;

    /// <summary>REFEDS Multi-Factor Authentication Profile.</summary>
    public const string RefedsMfa = "https://refeds.org/profile/mfa";

    /// <summary>Werte von „amr“ (RFC 8176), die einen zweiten Faktor belegen.</summary>
    public static readonly IReadOnlySet<string> MfaAmr = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mfa", "mca", "otp", "hwk" };

    /// <summary>Attribute, die im Zwischen-Cookie bleiben (alles andere wird verworfen, damit es klein bleibt).</summary>
    public static readonly IReadOnlySet<string> SamlBenoetigt = new HashSet<string>(StringComparer.Ordinal)
    {
        AttrSubjectId, AttrPairwiseId, AttrEppn, AttrAffiliation, AttrDisplayName, AttrGivenName, AttrSurname, AttrMail, SamlAuthnKontext,
    };

    public static readonly IReadOnlySet<string> OidcBenoetigt = new HashSet<string>(StringComparer.Ordinal)
    {
        "sub", "amr", "acr", "name", "given_name", "family_name", "email", "preferred_username",
    };

    /// <summary>SAML: Kennung aus subject-id, sonst pairwise-id, sonst eppn; MFA über REFEDS-MFA (oder weitere konfigurierte Kontexte).</summary>
    public static ExterneIdentitaet? AusSaml(IEnumerable<Claim> claims, IEnumerable<string>? weitereMfaKontexte = null)
    {
        var liste = claims.ToList();
        var schluessel = Wert(liste, AttrSubjectId) ?? Wert(liste, AttrPairwiseId) ?? Wert(liste, AttrEppn);
        if (schluessel is null)
        {
            return null;
        }

        var mfaWerte = new HashSet<string>(weitereMfaKontexte ?? [], StringComparer.OrdinalIgnoreCase) { RefedsMfa };
        var mfa = liste.Any(c => c.Type == SamlAuthnKontext && mfaWerte.Contains(c.Value.Trim()));
        return new ExterneIdentitaet(ExterneIdentitaet.Saml, schluessel.ToLowerInvariant(), Wert(liste, AttrGivenName), Wert(liste, AttrSurname),
            Wert(liste, AttrDisplayName), Wert(liste, AttrMail), liste.Where(c => c.Type == AttrAffiliation).Select(c => c.Value.Trim()).ToList(), mfa);
    }

    /// <summary>OIDC: Kennung „sub“; MFA über „amr“ (mfa, mca, otp, hwk) oder einen konfigurierten „acr“-Wert.</summary>
    public static ExterneIdentitaet? AusOidc(IEnumerable<Claim> claims, IEnumerable<string>? mfaAcr = null)
    {
        var liste = claims.ToList();
        if (Wert(liste, "sub") is not { } sub)
        {
            return null;
        }

        var acr = new HashSet<string>(mfaAcr ?? [], StringComparer.OrdinalIgnoreCase);
        var mfa = liste.Any(c => c.Type == "amr" && MfaAmr.Contains(c.Value.Trim())) || (Wert(liste, "acr") is { } a && acr.Contains(a));
        return new ExterneIdentitaet(ExterneIdentitaet.Oidc, sub, Wert(liste, "given_name"), Wert(liste, "family_name"), Wert(liste, "name"),
            Wert(liste, "email"), [], mfa);
    }

    private static string? Wert(List<Claim> claims, string typ) => claims.FirstOrDefault(c => c.Type == typ)?.Value?.Trim() is { Length: > 0 } v ? v : null;
}
