using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Drittpruefung;

public enum Signaturschritt
{
    Antragstellung = 1,
    Erstpruefung = 2,
    Studiendekanat = 3,

    /// <summary>Einladungsverfahren: Unterschrift der oder des Zweitprüfenden (zwischen Erstprüfung und Studiendekanat).</summary>
    Zweitpruefung = 4,
}

public enum Signaturart
{
    /// <summary>Kryptografisch im Browser mit dem privaten Schlüssel der Person (WebCrypto); der Schlüssel verlässt nie den Rechner.</summary>
    Schluessel = 1,

    /// <summary>Mit Maus, Stift oder Finger gezeichnete Unterschrift (einfache elektronische Signatur) plus Systemsiegel.</summary>
    Handschrift = 2,
}

public static class SignaturText
{
    public static string Anzeigename(this Signaturschritt schritt) => schritt switch
    {
        Signaturschritt.Antragstellung => "Antragstellung (Studierende/r)",
        Signaturschritt.Erstpruefung => "Festlegung (Erstprüfende/r)",
        Signaturschritt.Zweitpruefung => "Zustimmung (Zweitprüfende/r)",
        Signaturschritt.Studiendekanat => "Bestellung (Studiendekanat)",
        _ => schritt.ToString(),
    };

    public static string Anzeigename(this Signaturart art) => art switch
    {
        Signaturart.Schluessel => "Digitale Signatur (eigener Schlüssel)",
        Signaturart.Handschrift => "Gezeichnete Unterschrift",
        _ => art.ToString(),
    };
}

/// <summary>
/// Eine Unterschrift unter einen festen Stand des Antrags. <see cref="Inhalt"/> ist der vollständige, kanonische Text, der
/// unterschrieben wurde (er enthält den Hash der vorherigen Unterschrift → Kette). Das <see cref="Systemsiegel"/> ist eine
/// Signatur des Servers über Inhalt, Art und Signaturwert bzw. Bild; damit fällt jede spätere Änderung in der Datenbank auf.
/// </summary>
public sealed class Signatur
{
    private Signatur()
    {
    }

    private Signatur(Guid antragId, Signaturschritt schritt, Signaturart art, DateTimeOffset zeitpunkt, string unterzeichner, Guid? kontoId,
        string inhalt, string inhaltHash, string? vorgaengerHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unterzeichner);
        ArgumentException.ThrowIfNullOrWhiteSpace(inhalt);
        Id = Ids.Neu();
        AntragId = antragId;
        Schritt = schritt;
        Art = art;
        Zeitpunkt = zeitpunkt;
        Unterzeichner = unterzeichner;
        KontoId = kontoId;
        Inhalt = inhalt;
        InhaltHash = inhaltHash;
        VorgaengerHash = vorgaengerHash;
    }

    public static Signatur MitSchluessel(Guid antragId, Signaturschritt schritt, DateTimeOffset zeitpunkt, string unterzeichner, Guid? kontoId,
        string inhalt, string inhaltHash, string? vorgaengerHash, string algorithmus, string signaturWert, string oeffentlicherSchluesselJwk,
        string fingerabdruck, bool schluesselHinterlegt)
        => new(antragId, schritt, Signaturart.Schluessel, zeitpunkt, unterzeichner, kontoId, inhalt, inhaltHash, vorgaengerHash)
        {
            Algorithmus = algorithmus,
            SignaturWert = signaturWert,
            OeffentlicherSchluesselJwk = oeffentlicherSchluesselJwk,
            Fingerabdruck = fingerabdruck,
            SchluesselHinterlegt = schluesselHinterlegt,
        };

    public static Signatur MitHandschrift(Guid antragId, Signaturschritt schritt, DateTimeOffset zeitpunkt, string unterzeichner, Guid? kontoId,
        string inhalt, string inhaltHash, string? vorgaengerHash, byte[] bildPng, string bildHash)
    {
        ArgumentNullException.ThrowIfNull(bildPng);
        return new Signatur(antragId, schritt, Signaturart.Handschrift, zeitpunkt, unterzeichner, kontoId, inhalt, inhaltHash, vorgaengerHash)
        {
            BildPng = bildPng,
            BildHash = bildHash,
        };
    }

    public Guid Id { get; private set; }

    public Guid AntragId { get; private set; }

    public Signaturschritt Schritt { get; private set; }

    public Signaturart Art { get; private set; }

    public DateTimeOffset Zeitpunkt { get; private set; }

    /// <summary>Name, wie er unterschrieben hat (Konto-Anzeigename bzw. Name der oder des Studierenden).</summary>
    public string Unterzeichner { get; private set; } = string.Empty;

    public Guid? KontoId { get; private set; }

    /// <summary>Der unterschriebene Text (UTF-8, LF), genau so, wie er der Person angezeigt und signiert wurde.</summary>
    public string Inhalt { get; private set; } = string.Empty;

    /// <summary>SHA-256 von <see cref="Inhalt"/> (hex, klein).</summary>
    public string InhaltHash { get; private set; } = string.Empty;

    /// <summary>InhaltHash der vorherigen gültigen Unterschrift; bei der Antragstellung <c>null</c>.</summary>
    public string? VorgaengerHash { get; private set; }

    // ---- Signatur mit Schlüssel ----
    /// <summary>WebCrypto-Algorithmus, z. B. „ECDSA-P256-SHA256“, „RSASSA-PKCS1-v1_5-SHA256“, „RSA-PSS-SHA256“.</summary>
    public string? Algorithmus { get; private set; }

    /// <summary>Signaturwert Base64 (ECDSA im Format r‖s wie WebCrypto).</summary>
    public string? SignaturWert { get; private set; }

    public string? OeffentlicherSchluesselJwk { get; private set; }

    /// <summary>JWK-Fingerabdruck (RFC 7638, SHA-256 hex).</summary>
    public string? Fingerabdruck { get; private set; }

    /// <summary>Der Schlüssel war beim Unterschreiben im Konto hinterlegt (Zuordnung zur Person geprüft).</summary>
    public bool SchluesselHinterlegt { get; private set; }

    // ---- Gezeichnete Unterschrift ----
    public byte[]? BildPng { get; private set; }

    public string? BildHash { get; private set; }

    // ---- Systemsiegel ----
    /// <summary>ECDSA-Signatur des Servers über <see cref="SiegelText"/> (Base64).</summary>
    public string Systemsiegel { get; private set; } = string.Empty;

    /// <summary>Fingerabdruck des Systemsiegel-Schlüssels.</summary>
    public string SiegelSchluessel { get; private set; } = string.Empty;

    /// <summary>Überholt (z. B. Rückgabe durch das Studiendekanat): bleibt sichtbar, zählt aber nicht mehr.</summary>
    public DateTimeOffset? Ueberholt { get; private set; }

    public string? UeberholtGrund { get; private set; }

    /// <summary>Was das Systemsiegel abdeckt (stabil, nie ändern – sonst sind alte Siegel nicht mehr prüfbar).</summary>
    public string SiegelText => string.Join('\n',
        "profomat-siegel-v1",
        Id.ToString("D"),
        AntragId.ToString("D"),
        Schritt.ToString(),
        Art.ToString(),
        Zeitpunkt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Unterzeichner,
        KontoId?.ToString("D") ?? "-",
        InhaltHash,
        VorgaengerHash ?? "-",
        Algorithmus ?? "-",
        SignaturWert ?? "-",
        Fingerabdruck ?? "-",
        BildHash ?? "-");

    public void Versiegeln(string siegel, string siegelSchluessel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siegel);
        if (!string.IsNullOrEmpty(Systemsiegel))
        {
            throw new InvalidOperationException("Die Unterschrift ist bereits versiegelt.");
        }

        Systemsiegel = siegel;
        SiegelSchluessel = siegelSchluessel;
    }

    internal void Ueberholen(DateTimeOffset jetzt, string grund)
    {
        Ueberholt ??= jetzt;
        UeberholtGrund ??= grund;
    }
}
