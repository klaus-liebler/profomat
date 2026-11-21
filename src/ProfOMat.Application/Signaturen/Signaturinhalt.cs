using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProfOMat.Domain.Drittpruefung;

namespace ProfOMat.Application.Signaturen;

/// <summary>Alles, was im unterschriebenen Text steht – aufgelöst zu lesbaren Angaben (Namen, Titel, Schlüssel).</summary>
public sealed record Antragstext(
    Guid AntragId,
    string Nummer,
    DateTimeOffset Erstellt,
    string Vorname,
    string Nachname,
    string Matrikelnummer,
    string Email,
    string Studiengang,
    string StudiengangSchluessel,
    string Pruefung,
    string PruefungSchluessel,
    string Erstpruefende,
    Guid ErstpruefendeKontoId,
    string? Terminwuensche,
    string? Zweitpruefungswunsch);

/// <summary>Festlegung im unterschriebenen Text (Termin bereits in Ortszeit formatiert).</summary>
public sealed record Festlegungstext(string Termin, string Ort, string Zweitpruefende, string? Bemerkung);

/// <summary>
/// Baut den kanonischen, menschenlesbaren Text, der unterschrieben wird. Jede Stufe enthält den vollständigen Antrag, den
/// Hash der vorherigen Unterschrift und ihren eigenen Abschnitt. Der Text wird der Person vor dem Unterschreiben angezeigt
/// („What you see is what you sign“) und unverändert gespeichert. Format „profomat-antrag-v1“: nie nachträglich ändern,
/// sonst lassen sich alte Unterschriften nicht mehr nachrechnen – für Änderungen „v2“ einführen.
/// </summary>
public static class Signaturinhalt
{
    public const string Format = "profomat-antrag-v1";

    public static string Erstellen(Signaturschritt schritt, Antragstext antrag, string? vorgaengerHash, Festlegungstext? festlegung,
        string unterzeichner, DateTimeOffset zeitpunkt, string? studiendekanat = null)
    {
        ArgumentNullException.ThrowIfNull(antrag);
        var t = new StringBuilder();
        Zeile(t, "ProfOMat – Antrag auf mündliche Prüfung im dritten Versuch (ATPO § 18 Abs. 1 Satz 3)");
        Zeile(t, "Format", Format);
        Zeile(t, "Schritt", schritt.Anzeigename());
        Zeile(t, "Antrag", antrag.AntragId.ToString("D"));
        Zeile(t, "Nummer", antrag.Nummer);
        Zeile(t, "Erstellt", Utc(antrag.Erstellt));
        Zeile(t, string.Empty);
        Zeile(t, "[Antrag der/des Studierenden]");
        Zeile(t, "Vorname", antrag.Vorname);
        Zeile(t, "Nachname", antrag.Nachname);
        Zeile(t, "Matrikelnummer", antrag.Matrikelnummer);
        Zeile(t, "E-Mail", antrag.Email);
        Zeile(t, "Studiengang", $"{antrag.Studiengang} [{antrag.StudiengangSchluessel}]");
        Zeile(t, "Prüfung", $"{antrag.Pruefung} [{antrag.PruefungSchluessel}]");
        Zeile(t, "Gewünschte/r Erstprüfende/r", $"{antrag.Erstpruefende} [{antrag.ErstpruefendeKontoId:D}]");
        Zeile(t, "Terminliche Einschränkungen und Wünsche", antrag.Terminwuensche);
        Zeile(t, "Wunsch Zweitprüfende/r", antrag.Zweitpruefungswunsch);
        Zeile(t, "Erklärung", "Ich beantrage, die zweite Wiederholung der oben genannten schriftlichen Prüfung als mündliche Prüfung abzulegen.");

        if (schritt >= Signaturschritt.Erstpruefung)
        {
            ArgumentNullException.ThrowIfNull(festlegung);
            Zeile(t, string.Empty);
            Zeile(t, "[Festlegung durch die/den Erstprüfende/n]");
            Zeile(t, "Erstprüfende/r", antrag.Erstpruefende);
            Zeile(t, "Zweitprüfende/r", festlegung.Zweitpruefende);
            Zeile(t, "Termin", festlegung.Termin);
            Zeile(t, "Ort", festlegung.Ort);
            Zeile(t, "Bemerkung", festlegung.Bemerkung);
            if (schritt == Signaturschritt.Erstpruefung)
            {
                Zeile(t, "Erklärung", "Termin und Ort sind mit der/dem Zweitprüfenden abgestimmt. Ich übernehme die Prüfung als Erstprüfende/r.");
            }
        }

        if (schritt == Signaturschritt.Studiendekanat)
        {
            Zeile(t, string.Empty);
            Zeile(t, "[Bestellung durch das Studiendekanat]");
            Zeile(t, "Studiendekan/in", studiendekanat ?? unterzeichner);
            Zeile(t, "Erklärung", "Ich bestelle die oben genannten Prüfenden für diese mündliche Prüfung (ATPO § 24 Abs. 2).");
        }

        Zeile(t, string.Empty);
        Zeile(t, "[Unterschrift]");
        Zeile(t, "Vorherige Unterschrift (SHA-256)", vorgaengerHash ?? "-");
        Zeile(t, "Unterzeichnet von", unterzeichner);
        Zeile(t, "Zeitpunkt", Utc(zeitpunkt));
        return t.ToString();
    }

    /// <summary>SHA-256 des Textes als UTF-8 (hex, klein).</summary>
    public static string Hash(string inhalt) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inhalt)));

    public static string Hash(byte[] daten) => Convert.ToHexStringLower(SHA256.HashData(daten));

    /// <summary>Zeitpunkte im Text immer UTC auf Millisekunden – so lassen sie sich exakt nachbilden.</summary>
    public static DateTimeOffset Kappen(DateTimeOffset zeitpunkt)
        => new DateTimeOffset(zeitpunkt.UtcTicks - (zeitpunkt.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);

    private static string Utc(DateTimeOffset zeitpunkt) => Kappen(zeitpunkt).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void Zeile(StringBuilder t, string text) => t.Append(text).Append('\n');

    private static void Zeile(StringBuilder t, string feld, string? wert)
    {
        var normal = (wert ?? "-").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (normal.Length == 0)
        {
            normal = "-";
        }

        // Mehrzeilige Freitexte eingerückt, damit niemand eine Zeile „Feld: …“ untermischen kann.
        t.Append(feld).Append(": ").Append(normal.Replace("\n", "\n    ", StringComparison.Ordinal)).Append('\n');
    }
}
