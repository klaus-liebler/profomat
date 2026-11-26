using System.Security.Cryptography;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Konten;

/// <summary>Rollen der Beschäftigten. Studierende brauchen für die erste Funktion kein Konto (Bestätigung per Mail).</summary>
public enum Rolle
{
    /// <summary>Professorinnen, Professoren und weitere Prüfungsberechtigte.</summary>
    Pruefende = 1,

    /// <summary>Studiendekanin oder Studiendekan (bestellt Prüfende, ATPO § 24 Abs. 2).</summary>
    Studiendekanat = 2,

    /// <summary>Studierendensekretariat bzw. Prüfungsverwaltung: sieht abgeschlossene Anträge.</summary>
    Studierendensekretariat = 3,

    /// <summary>Konten, Katalog, Postausgang, Protokoll.</summary>
    Systemverwaltung = 4,
}

/// <summary>Woher ein Konto stammt.</summary>
public enum KontoHerkunft
{
    /// <summary>Lokal angelegt, Anmeldung mit Kennung und Passwort.</summary>
    Lokal = 1,

    /// <summary>Über einen Identitätsdienst (SAML/Shibboleth oder OIDC) per Einladung angelegt; Anmeldung über die verknüpfte externe Kennung.</summary>
    Hochschule = 2,
}

public static class RollenText
{
    public static string Anzeigename(this Rolle rolle) => rolle switch
    {
        Rolle.Pruefende => "Prüfende",
        Rolle.Studiendekanat => "Studiendekanat",
        Rolle.Studierendensekretariat => "Studierendensekretariat",
        Rolle.Systemverwaltung => "Systemverwaltung",
        _ => rolle.ToString(),
    };
}

/// <summary>Benutzerkonto einer oder eines Beschäftigten.</summary>
public sealed class Konto
{
    private readonly List<KontoRolle> rollen = [];
    private readonly List<HinterlegterSchluessel> schluessel = [];
    private readonly List<ExterneAnmeldung> anmeldungen = [];
    private readonly List<KontoPasskey> passkeys = [];

    private Konto()
    {
    }

    public Konto(string kennung, string? titel, string vorname, string nachname, string email, KontoHerkunft herkunft = KontoHerkunft.Lokal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kennung);
        ArgumentException.ThrowIfNullOrWhiteSpace(nachname);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        Id = Ids.Neu();
        Kennung = kennung.Trim().ToLowerInvariant();
        Titel = string.IsNullOrWhiteSpace(titel) ? null : titel.Trim();
        Vorname = vorname?.Trim() ?? string.Empty;
        Nachname = nachname.Trim();
        Email = email.Trim();
        Herkunft = herkunft;
        Aktiv = true;
        StempelErneuern();
    }

    public Guid Id { get; private set; }

    /// <summary>Anmeldename (klein geschrieben); bei Hochschulkonten der eduPersonPrincipalName.</summary>
    public string Kennung { get; private set; } = string.Empty;

    public string? Titel { get; private set; }

    public string Vorname { get; private set; } = string.Empty;

    public string Nachname { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public KontoHerkunft Herkunft { get; private set; }

    public bool Aktiv { get; private set; }

    /// <summary>Hash nach ASP.NET Core Identity (PBKDF2); <c>null</c> = keine Passwortanmeldung.</summary>
    public string? PasswortHash { get; private set; }

    /// <summary>
    /// Sicherheitsstempel (ASP.NET Core Identity): steht im Anmelde-Cookie und ändert sich mit Passwort, Rollen, Sperre, zweitem Faktor
    /// und verknüpften Anmeldungen. Passt er nicht mehr, endet die Sitzung.
    /// </summary>
    public string SicherheitsStempel { get; private set; } = string.Empty;

    /// <summary>Fehlgeschlagene Anmeldeversuche seit der letzten erfolgreichen Anmeldung (Sperre nach Fehlversuchen).</summary>
    public int Fehlversuche { get; private set; }

    /// <summary>Wegen zu vieler Fehlversuche vorübergehend gesperrt bis (nicht zu verwechseln mit <see cref="Aktiv"/>).</summary>
    public DateTimeOffset? GesperrtBis { get; private set; }

    /// <summary>Anmeldung mit Passwort verlangt zusätzlich einen Code aus der Authenticator-App (TOTP).</summary>
    public bool ZweiterFaktorAktiv { get; private set; }

    /// <summary>TOTP-Geheimnis, verschlüsselt (Data Protection); nie im Klartext gespeichert.</summary>
    public string? AuthenticatorSchluessel { get; private set; }

    /// <summary>SHA-256-Hashes der noch nicht verwendeten Wiederherstellungscodes, durch „;“ getrennt.</summary>
    public string? Wiederherstellungscodes { get; private set; }

    public IReadOnlyList<KontoRolle> Rollen => rollen;

    /// <summary>Verknüpfte Konten bei Identitätsdiensten (SAML, OIDC).</summary>
    public IReadOnlyList<ExterneAnmeldung> Anmeldungen => anmeldungen;

    public IReadOnlyList<KontoPasskey> Passkeys => passkeys;

    public IReadOnlyList<string> WiederherstellungscodeHashes => Wiederherstellungscodes?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];

    public IReadOnlyList<HinterlegterSchluessel> Schluessel => schluessel;

    /// <summary>„Prof. Dr. Erika Muster“.</summary>
    public string Anzeigename => string.Join(' ', new[] { Titel, Vorname, Nachname }.Where(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>Für Listen: „Muster, Erika (Prof. Dr.)“.</summary>
    public string Sortiername => Titel is null ? $"{Nachname}, {Vorname}" : $"{Nachname}, {Vorname} ({Titel})";

    public bool Hat(Rolle rolle) => rollen.Any(r => r.Rolle == rolle);

    public void RolleGeben(Rolle rolle)
    {
        if (!Hat(rolle))
        {
            rollen.Add(new KontoRolle(Id, rolle));
            StempelErneuern();
        }
    }

    public void RolleNehmen(Rolle rolle)
    {
        if (rollen.RemoveAll(r => r.Rolle == rolle) > 0)
        {
            StempelErneuern();
        }
    }

    public void PasswortSetzen(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        PasswortHash = hash;
        StempelErneuern();
    }

    /// <summary>Neuer Sicherheitsstempel: alle bestehenden Sitzungen dieses Kontos enden.</summary>
    public void StempelErneuern() => SicherheitsStempel = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Für ASP.NET Core Identity, das den Stempel bei eigenen Änderungen selbst erzeugt.</summary>
    public void StempelSetzen(string stempel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stempel);
        SicherheitsStempel = stempel;
    }

    public void FehlversucheSetzen(int anzahl) => Fehlversuche = Math.Max(0, anzahl);

    public void SperreSetzen(DateTimeOffset? bis) => GesperrtBis = bis;

    public void ZweitenFaktorSetzen(bool aktiv) => ZweiterFaktorAktiv = aktiv;

    public void AuthenticatorSchluesselSetzen(string? geschuetzt) => AuthenticatorSchluessel = string.IsNullOrEmpty(geschuetzt) ? null : geschuetzt;

    public void WiederherstellungscodesSetzen(IEnumerable<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        var liste = hashes.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        Wiederherstellungscodes = liste.Count == 0 ? null : string.Join(';', liste);
    }

    /// <summary>Zweiter Faktor aus (z. B. Gerät verloren): Geheimnis und Wiederherstellungscodes verfallen, Sitzungen enden.</summary>
    public void ZweitenFaktorZuruecksetzen()
    {
        ZweiterFaktorAktiv = false;
        AuthenticatorSchluessel = null;
        Wiederherstellungscodes = null;
        StempelErneuern();
    }

    /// <summary>Externe Kennung (dauerhaft: subject-id, pairwise-id, eppn bzw. „sub“) mit diesem Konto verknüpfen.</summary>
    public ExterneAnmeldung AnmeldungVerknuepfen(string anbieter, string schluessel, string? anzeigename, DateTimeOffset jetzt)
    {
        if (anmeldungen.FirstOrDefault(a => a.Anbieter == anbieter && a.Schluessel == schluessel) is { } vorhanden)
        {
            return vorhanden;
        }

        var neu = new ExterneAnmeldung(Id, anbieter, schluessel, anzeigename, jetzt);
        anmeldungen.Add(neu);
        return neu;
    }

    public bool AnmeldungTrennen(string anbieter, string schluessel) => anmeldungen.RemoveAll(a => a.Anbieter == anbieter && a.Schluessel == schluessel) > 0;

    public KontoPasskey PasskeySpeichern(KontoPasskey.Angaben angaben, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(angaben);
        if (passkeys.FirstOrDefault(p => p.CredentialId.AsSpan().SequenceEqual(angaben.CredentialId)) is { } vorhanden)
        {
            vorhanden.Aktualisieren(angaben);
            return vorhanden;
        }

        var neu = new KontoPasskey(Id, angaben, jetzt);
        passkeys.Add(neu);
        return neu;
    }

    public bool PasskeyEntfernen(byte[] credentialId) => passkeys.RemoveAll(p => p.CredentialId.AsSpan().SequenceEqual(credentialId)) > 0;

    public void Aendern(string? titel, string vorname, string nachname, string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nachname);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        Titel = string.IsNullOrWhiteSpace(titel) ? null : titel.Trim();
        Vorname = vorname?.Trim() ?? string.Empty;
        Nachname = nachname.Trim();
        Email = email.Trim();
    }

    public void Sperren()
    {
        if (Aktiv)
        {
            Aktiv = false;
            StempelErneuern();
        }
    }

    /// <summary>Wieder freigeben; hebt auch eine Sperre nach Fehlversuchen auf.</summary>
    public void Entsperren()
    {
        Aktiv = true;
        Fehlversuche = 0;
        GesperrtBis = null;
    }

    /// <summary>Öffentlichen Schlüssel für Signaturen hinterlegen (der private Schlüssel verlässt nie den Browser).</summary>
    public HinterlegterSchluessel SchluesselHinterlegen(string fingerabdruck, string oeffentlicherSchluesselJwk, string algorithmus, string bezeichnung, DateTimeOffset jetzt)
    {
        if (schluessel.FirstOrDefault(s => s.Fingerabdruck == fingerabdruck && s.WiderrufenAm is null) is { } vorhanden)
        {
            return vorhanden;
        }

        var neu = new HinterlegterSchluessel(Id, fingerabdruck, oeffentlicherSchluesselJwk, algorithmus, bezeichnung, jetzt);
        schluessel.Add(neu);
        return neu;
    }

    public void SchluesselWiderrufen(Guid schluesselId, DateTimeOffset jetzt)
        => schluessel.FirstOrDefault(s => s.Id == schluesselId)?.Widerrufen(jetzt);

    public bool HatGueltigenSchluessel(string fingerabdruck) => schluessel.Any(s => s.Fingerabdruck == fingerabdruck && s.WiderrufenAm is null);
}

public sealed class KontoRolle
{
    private KontoRolle()
    {
    }

    internal KontoRolle(Guid kontoId, Rolle rolle)
    {
        KontoId = kontoId;
        Rolle = rolle;
    }

    public Guid KontoId { get; private set; }

    public Rolle Rolle { get; private set; }
}

/// <summary>Öffentlicher Signaturschlüssel eines Kontos (JWK ohne private Anteile).</summary>
public sealed class HinterlegterSchluessel
{
    private HinterlegterSchluessel()
    {
    }

    internal HinterlegterSchluessel(Guid kontoId, string fingerabdruck, string jwk, string algorithmus, string bezeichnung, DateTimeOffset jetzt)
    {
        Id = Ids.Neu();
        KontoId = kontoId;
        Fingerabdruck = fingerabdruck;
        OeffentlicherSchluesselJwk = jwk;
        Algorithmus = algorithmus;
        Bezeichnung = string.IsNullOrWhiteSpace(bezeichnung) ? "Schlüssel" : bezeichnung.Trim();
        Hinterlegt = jetzt;
    }

    public Guid Id { get; private set; }

    public Guid KontoId { get; private set; }

    /// <summary>SHA-256 über die kanonische Form des öffentlichen Schlüssels (RFC 7638), hex.</summary>
    public string Fingerabdruck { get; private set; } = string.Empty;

    public string OeffentlicherSchluesselJwk { get; private set; } = string.Empty;

    public string Algorithmus { get; private set; } = string.Empty;

    public string Bezeichnung { get; private set; } = string.Empty;

    public DateTimeOffset Hinterlegt { get; private set; }

    public DateTimeOffset? WiderrufenAm { get; private set; }

    internal void Widerrufen(DateTimeOffset jetzt) => WiderrufenAm ??= jetzt;
}

/// <summary>Verknüpfung mit einem Konto bei einem Identitätsdienst (in Identity: „Login“). Schlüssel = dauerhafte Kennung dort.</summary>
public sealed class ExterneAnmeldung
{
    public const int SchluesselMaxLaenge = 400;

    private ExterneAnmeldung()
    {
    }

    internal ExterneAnmeldung(Guid kontoId, string anbieter, string schluessel, string? anzeigename, DateTimeOffset jetzt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anbieter);
        ArgumentException.ThrowIfNullOrWhiteSpace(schluessel);
        if (schluessel.Length > SchluesselMaxLaenge)
        {
            throw new ArgumentException("Die externe Kennung ist zu lang.", nameof(schluessel));
        }

        Id = Ids.Neu();
        KontoId = kontoId;
        Anbieter = anbieter;
        Schluessel = schluessel;
        Anzeigename = string.IsNullOrWhiteSpace(anzeigename) ? anbieter : anzeigename.Trim();
        Verknuepft = jetzt;
    }

    public Guid Id { get; private set; }

    public Guid KontoId { get; private set; }

    /// <summary>„SAML“ oder „OIDC“.</summary>
    public string Anbieter { get; private set; } = string.Empty;

    /// <summary>Dauerhafte Kennung beim Identitätsdienst (subject-id, pairwise-id, eppn bzw. „sub“).</summary>
    public string Schluessel { get; private set; } = string.Empty;

    public string Anzeigename { get; private set; } = string.Empty;

    public DateTimeOffset Verknuepft { get; private set; }
}

/// <summary>Passkey (WebAuthn-Anmeldedaten) eines Kontos: nur öffentliche Daten, der private Schlüssel bleibt im Gerät.</summary>
public sealed class KontoPasskey
{
    public const int BezeichnungMaxLaenge = 100;

    private KontoPasskey()
    {
    }

    internal KontoPasskey(Guid kontoId, Angaben a, DateTimeOffset jetzt)
    {
        Id = Ids.Neu();
        KontoId = kontoId;
        CredentialId = a.CredentialId;
        Hinzugefuegt = jetzt;
        Aktualisieren(a);
    }

    /// <summary>Was ASP.NET Core Identity über einen Passkey speichert (UserPasskeyInfo), ohne Abhängigkeit vom Framework.</summary>
    public sealed record Angaben(byte[] CredentialId, byte[] OeffentlicherSchluessel, string? Bezeichnung, DateTimeOffset Erstellt, uint Zaehler,
        string? Transporte, bool NutzerGeprueft, bool SicherungMoeglich, bool Gesichert, byte[] Attestierung, byte[] ClientDaten);

    public Guid Id { get; private set; }

    public Guid KontoId { get; private set; }

    public byte[] CredentialId { get; private set; } = [];

    public byte[] OeffentlicherSchluessel { get; private set; } = [];

    public string? Bezeichnung { get; private set; }

    public DateTimeOffset Erstellt { get; private set; }

    public DateTimeOffset Hinzugefuegt { get; private set; }

    /// <summary>Signaturzähler (Schutz gegen geklonte Authenticatoren).</summary>
    public long Zaehler { get; private set; }

    /// <summary>Transportwege, durch „,“ getrennt (usb, nfc, internal, hybrid …).</summary>
    public string? Transporte { get; private set; }

    public bool NutzerGeprueft { get; private set; }

    public bool SicherungMoeglich { get; private set; }

    public bool Gesichert { get; private set; }

    public byte[] Attestierung { get; private set; } = [];

    public byte[] ClientDaten { get; private set; } = [];

    public Angaben AlsAngaben() => new(CredentialId, OeffentlicherSchluessel, Bezeichnung, Erstellt, (uint)Math.Clamp(Zaehler, 0, uint.MaxValue), Transporte,
        NutzerGeprueft, SicherungMoeglich, Gesichert, Attestierung, ClientDaten);

    internal void Aktualisieren(Angaben a)
    {
        OeffentlicherSchluessel = a.OeffentlicherSchluessel;
        if (a.Bezeichnung?.Trim() is { Length: > 0 } b)
        {
            Bezeichnung = b.Length > BezeichnungMaxLaenge ? b[..BezeichnungMaxLaenge] : b;
        }

        Erstellt = a.Erstellt;
        Zaehler = a.Zaehler;
        Transporte = a.Transporte;
        NutzerGeprueft = a.NutzerGeprueft;
        SicherungMoeglich = a.SicherungMoeglich;
        Gesichert = a.Gesichert;
        Attestierung = a.Attestierung;
        ClientDaten = a.ClientDaten;
    }
}
