using ProfOMat.Domain.Drittpruefung;
using ProfOMat.Domain.Katalog;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Drittpruefung;

/// <summary>Antrag mit aufgelösten Namen und geprüften Unterschriften – für Seiten, Mails und PDF.</summary>
public sealed record AntragAnsicht(
    Antrag Antrag,
    Studiengang Studiengang,
    Pruefung Pruefung,
    Fakultaet? Fakultaet,
    Konto Erstpruefende,
    Konto? ZweitpruefendeKonto,
    Konto? Studiendekanat,
    IReadOnlyList<SignaturBefund> Signaturen,
    string Hochschule,
    string Link,
    string? Termin,
    string SekretariatEmail)
{
    /// <summary>Alle gültigen Unterschriften sind in Ordnung (Kette, Siegel, Signaturen).</summary>
    public bool KetteInOrdnung => Signaturen.Where(s => s.Signatur.Ueberholt is null).All(s => s.Gueltig);

    /// <summary>Hash der letzten gültigen Unterschrift = Prüfwert des gesamten Vorgangs.</summary>
    public string? Pruefwert => Antrag.LetzteGueltigeSignatur?.InhaltHash;
}

/// <param name="Signatur">Die gespeicherte Unterschrift.</param>
/// <param name="Gueltig">Inhalt, Kette, Systemsiegel und (bei Schlüssel) Signatur stimmen.</param>
/// <param name="Hinweise">Was geprüft wurde bzw. was nicht stimmt, für Menschen.</param>
public sealed record SignaturBefund(Signatur Signatur, bool Gueltig, IReadOnlyList<string> Hinweise);

/// <summary>Vorlage zum Unterschreiben: genau dieser Text wird angezeigt und signiert.</summary>
public sealed record Signiervorlage(Guid AntragId, Signaturschritt Schritt, string Text, DateTimeOffset Zeitpunkt, string? VorgaengerHash, string Unterzeichner);

/// <summary>Filter für die Antragsliste.</summary>
public enum AntragFilter
{
    /// <summary>Wartet auf mich (als Erst-, Zweitprüfende/r oder Studiendekanat).</summary>
    MeineAufgaben = 1,

    /// <summary>Alle offenen, die ich sehen darf.</summary>
    Offen = 2,

    /// <summary>Alle, die ich sehen darf.</summary>
    Alle = 3,
}

/// <summary>Zeile der Antragsliste.</summary>
public sealed record AntragZeile(Guid Id, string Nummer, AntragStatus Status, string Name, string Matrikelnummer, string Pruefung, string Studiengang,
    string Erstpruefende, DateTimeOffset Erstellt, DateTimeOffset? Termin, bool WartetAufMich);
