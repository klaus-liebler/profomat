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

    /// <summary>Über das Hochschulsystem (SAML/Shibboleth, später); Kennung = eduPersonPrincipalName.</summary>
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

    public IReadOnlyList<KontoRolle> Rollen => rollen;

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
        }
    }

    public void RolleNehmen(Rolle rolle) => rollen.RemoveAll(r => r.Rolle == rolle);

    public void PasswortSetzen(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        PasswortHash = hash;
    }

    public void Aendern(string? titel, string vorname, string nachname, string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nachname);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        Titel = string.IsNullOrWhiteSpace(titel) ? null : titel.Trim();
        Vorname = vorname?.Trim() ?? string.Empty;
        Nachname = nachname.Trim();
        Email = email.Trim();
    }

    public void Sperren() => Aktiv = false;

    public void Entsperren() => Aktiv = true;

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
