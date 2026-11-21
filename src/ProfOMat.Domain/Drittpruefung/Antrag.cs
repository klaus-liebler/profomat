using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Drittpruefung;

/// <summary>Bearbeitungsstand eines Antrags auf mündliche Drittprüfung (ATPO § 18 Abs. 1 S. 3).</summary>
public enum AntragStatus
{
    /// <summary>Eingereicht und unterschrieben, die Mailadresse ist aber noch nicht bestätigt.</summary>
    WartetAufBestaetigung = 1,

    /// <summary>Bei der oder dem gewünschten Erstprüfenden: Termin, Ort und Zweitprüfende festlegen und unterschreiben.</summary>
    BeimErstpruefer = 2,

    /// <summary>Beim Studiendekanat: Bestellung der Prüfenden unterschreiben.</summary>
    BeimStudiendekanat = 3,

    /// <summary>Von allen unterschrieben; PDF erzeugt und an das Studierendensekretariat gegeben.</summary>
    Abgeschlossen = 4,

    /// <summary>Von der oder dem Erstprüfenden bzw. vom Studiendekanat mit Begründung abgelehnt.</summary>
    Abgelehnt = 5,

    /// <summary>Von der oder dem Studierenden zurückgezogen.</summary>
    Zurueckgezogen = 6,

    /// <summary>Mailadresse nicht rechtzeitig bestätigt.</summary>
    Verfallen = 7,
}

public static class AntragStatusText
{
    public static string Anzeigename(this AntragStatus status) => status switch
    {
        AntragStatus.WartetAufBestaetigung => "Wartet auf Bestätigung der Mailadresse",
        AntragStatus.BeimErstpruefer => "Bei der/dem Erstprüfenden",
        AntragStatus.BeimStudiendekanat => "Beim Studiendekanat",
        AntragStatus.Abgeschlossen => "Abgeschlossen",
        AntragStatus.Abgelehnt => "Abgelehnt",
        AntragStatus.Zurueckgezogen => "Zurückgezogen",
        AntragStatus.Verfallen => "Verfallen (nicht bestätigt)",
        _ => status.ToString(),
    };

    public static bool IstOffen(this AntragStatus status)
        => status is AntragStatus.WartetAufBestaetigung or AntragStatus.BeimErstpruefer or AntragStatus.BeimStudiendekanat;
}

/// <summary>Die Angaben der oder des Studierenden (alles, was bei der Antragstellung unterschrieben wird).</summary>
public sealed record Antragsangaben(
    string Vorname,
    string Nachname,
    string Matrikelnummer,
    string Email,
    Guid StudiengangId,
    Guid PruefungId,
    Guid ErstpruefendeKontoId,
    string? Terminwuensche,
    string? Zweitpruefungswunsch)
{
    public const int MaxFreitext = 2000;

    /// <summary>Prüft die Pflichtangaben; liefert die erste Beanstandung oder <c>null</c>.</summary>
    public string? Beanstandung()
    {
        if (string.IsNullOrWhiteSpace(Vorname) || string.IsNullOrWhiteSpace(Nachname))
        {
            return "Bitte Vor- und Nachnamen angeben.";
        }

        if (string.IsNullOrWhiteSpace(Matrikelnummer) || !Matrikelnummer.Trim().All(char.IsAsciiDigit) || Matrikelnummer.Trim().Length is < 5 or > 10)
        {
            return "Die Matrikelnummer besteht aus 5 bis 10 Ziffern.";
        }

        if (string.IsNullOrWhiteSpace(Email) || !Email.Contains('@', StringComparison.Ordinal) || Email.Trim().Contains(' ', StringComparison.Ordinal))
        {
            return "Bitte eine gültige Mailadresse angeben.";
        }

        if (StudiengangId == Guid.Empty || PruefungId == Guid.Empty)
        {
            return "Bitte Studiengang und Prüfung auswählen.";
        }

        if (ErstpruefendeKontoId == Guid.Empty)
        {
            return "Bitte die gewünschte Erstprüferin bzw. den gewünschten Erstprüfer auswählen.";
        }

        if ((Terminwuensche?.Length ?? 0) > MaxFreitext || (Zweitpruefungswunsch?.Length ?? 0) > MaxFreitext)
        {
            return $"Freitexte bitte mit höchstens {MaxFreitext} Zeichen.";
        }

        return null;
    }

    public Antragsangaben Bereinigt() => this with
    {
        Vorname = Vorname.Trim(),
        Nachname = Nachname.Trim(),
        Matrikelnummer = Matrikelnummer.Trim(),
        Email = Email.Trim(),
        Terminwuensche = string.IsNullOrWhiteSpace(Terminwuensche) ? null : Terminwuensche.Trim(),
        Zweitpruefungswunsch = string.IsNullOrWhiteSpace(Zweitpruefungswunsch) ? null : Zweitpruefungswunsch.Trim(),
    };
}

/// <summary>Festlegung durch die oder den Erstprüfenden nach Rücksprache mit der oder dem Zweitprüfenden.</summary>
/// <param name="Termin">Beginn der Prüfung (UTC).</param>
/// <param name="Ort">Raum bzw. Ort; die letzte Wiederholung findet nicht per Video statt (ATPO § 6 Abs. 1a S. 2).</param>
/// <param name="ZweitpruefendeKontoId">Zweitprüfende mit Konto im System, sonst <c>null</c>.</param>
/// <param name="ZweitpruefendeName">Name der oder des Zweitprüfenden (bei Konto aus dem Konto übernommen).</param>
/// <param name="ZweitpruefendeEmail">Mailadresse für die Benachrichtigung (optional bei Personen ohne Konto).</param>
/// <param name="Bemerkung">Optionaler Hinweis an Studiendekanat und Studierende.</param>
public sealed record Festlegung(
    DateTimeOffset Termin,
    string Ort,
    Guid? ZweitpruefendeKontoId,
    string ZweitpruefendeName,
    string? ZweitpruefendeEmail,
    string? Bemerkung)
{
    public string? Beanstandung(Guid erstpruefendeKontoId, DateTimeOffset jetzt)
    {
        if (Termin <= jetzt)
        {
            return "Der Prüfungstermin muss in der Zukunft liegen.";
        }

        if (string.IsNullOrWhiteSpace(Ort))
        {
            return "Bitte den Prüfungsort angeben.";
        }

        if (string.IsNullOrWhiteSpace(ZweitpruefendeName))
        {
            return "Bitte die Zweitprüferin bzw. den Zweitprüfer angeben (ATPO § 16 Abs. 1 S. 3: letzte Wiederholung mit zwei Prüfenden).";
        }

        if (ZweitpruefendeKontoId == erstpruefendeKontoId)
        {
            return "Erst- und Zweitprüfende müssen verschiedene Personen sein.";
        }

        return (Bemerkung?.Length ?? 0) > Antragsangaben.MaxFreitext ? $"Die Bemerkung bitte mit höchstens {Antragsangaben.MaxFreitext} Zeichen." : null;
    }
}

/// <summary>
/// Antrag auf mündliche Drittprüfung (zweite Wiederholung einer schriftlichen Prüfung als mündliche Prüfung).
/// Ablauf: Studierende stellen und unterschreiben → bestätigen ihre Mailadresse → Erstprüfende legen Termin, Ort und
/// Zweitprüfende fest und unterschreiben → Studiendekanat unterschreibt (Bestellung, ATPO § 24 Abs. 2) → PDF an das
/// Studierendensekretariat. Jede Unterschrift bezieht sich auf den vollständigen Stand und die vorherige Unterschrift.
/// </summary>
public sealed class Antrag
{
    private readonly List<Signatur> signaturen = [];

    private Antrag()
    {
    }

    /// <param name="id">Vorab vergebene Id (sie steht im unterschriebenen Text).</param>
    /// <param name="nummer">Lesbare Nummer, z. B. „DP-2026-0007“.</param>
    /// <param name="erstellt">Zeitpunkt, der im unterschriebenen Text steht.</param>
    public Antrag(Guid id, string nummer, Antragsangaben angaben, DateTimeOffset erstellt, string bestaetigungsTokenHash, Signatur unterschrift)
    {
        ArgumentNullException.ThrowIfNull(angaben);
        ArgumentNullException.ThrowIfNull(unterschrift);
        ArgumentException.ThrowIfNullOrWhiteSpace(nummer);
        ArgumentException.ThrowIfNullOrWhiteSpace(bestaetigungsTokenHash);
        if (angaben.Beanstandung() is { } fehler)
        {
            throw new FachlicherFehler(fehler);
        }

        if (unterschrift.Schritt != Signaturschritt.Antragstellung || unterschrift.AntragId != id)
        {
            throw new FachlicherFehler("Die Unterschrift gehört nicht zu dieser Antragstellung.");
        }

        var a = angaben.Bereinigt();
        Id = id;
        Nummer = nummer;
        Vorname = a.Vorname;
        Nachname = a.Nachname;
        Matrikelnummer = a.Matrikelnummer;
        Email = a.Email;
        StudiengangId = a.StudiengangId;
        PruefungId = a.PruefungId;
        ErstpruefendeKontoId = a.ErstpruefendeKontoId;
        Terminwuensche = a.Terminwuensche;
        Zweitpruefungswunsch = a.Zweitpruefungswunsch;
        Erstellt = erstellt;
        StatusTokenHash = bestaetigungsTokenHash;
        Status = AntragStatus.WartetAufBestaetigung;
        signaturen.Add(unterschrift);
    }

    public Guid Id { get; private set; }

    public string Nummer { get; private set; } = string.Empty;

    public AntragStatus Status { get; private set; }

    public DateTimeOffset Erstellt { get; private set; }

    // ---- Angaben der oder des Studierenden (unveränderlich nach dem Einreichen) ----
    public string Vorname { get; private set; } = string.Empty;

    public string Nachname { get; private set; } = string.Empty;

    public string Matrikelnummer { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public Guid StudiengangId { get; private set; }

    public Guid PruefungId { get; private set; }

    public Guid ErstpruefendeKontoId { get; private set; }

    public string? Terminwuensche { get; private set; }

    public string? Zweitpruefungswunsch { get; private set; }

    /// <summary>SHA-256 des geheimen Links für Studierende (Bestätigung und Statusseite); das Token selbst wird nie gespeichert.</summary>
    public string StatusTokenHash { get; private set; } = string.Empty;

    public DateTimeOffset? EmailBestaetigt { get; private set; }

    // ---- Festlegung durch die oder den Erstprüfenden ----
    public DateTimeOffset? Termin { get; private set; }

    public string? Ort { get; private set; }

    public Guid? ZweitpruefendeKontoId { get; private set; }

    public string? ZweitpruefendeName { get; private set; }

    public string? ZweitpruefendeEmail { get; private set; }

    public string? Bemerkung { get; private set; }

    /// <summary>Offene Rückfrage des Studiendekanats an die Erstprüfenden (bis zur nächsten Unterschrift).</summary>
    public string? Rueckfrage { get; private set; }

    /// <summary>Studiendekanat, das beim Weiterleiten zuständig war.</summary>
    public Guid? StudiendekanatKontoId { get; private set; }

    // ---- Abschluss ----
    public DateTimeOffset? Abgeschlossen { get; private set; }

    public string? Ablehnungsgrund { get; private set; }

    public string? AbgelehntVon { get; private set; }

    public DateTimeOffset? Beendet { get; private set; }

    public IReadOnlyList<Signatur> Signaturen => signaturen;

    public string Name => $"{Vorname} {Nachname}";

    /// <summary>Gültige (nicht überholte) Unterschriften in Reihenfolge.</summary>
    public IReadOnlyList<Signatur> GueltigeSignaturen => signaturen.Where(s => s.Ueberholt is null).OrderBy(s => s.Zeitpunkt).ThenBy(s => s.Schritt).ToList();

    public Signatur? LetzteGueltigeSignatur => GueltigeSignaturen.LastOrDefault();

    public Festlegung? AktuelleFestlegung => Termin is { } t && Ort is { } o && ZweitpruefendeName is { } z
        ? new Festlegung(t, o, ZweitpruefendeKontoId, z, ZweitpruefendeEmail, Bemerkung)
        : null;

    public void EmailBestaetigen(DateTimeOffset jetzt)
    {
        if (Status != AntragStatus.WartetAufBestaetigung)
        {
            return;   // doppelt geklickter Link: nichts tun
        }

        EmailBestaetigt = jetzt;
        Status = AntragStatus.BeimErstpruefer;
    }

    public Freigabe KannFestlegen(Guid kontoId) => Status != AntragStatus.BeimErstpruefer
        ? Freigabe.Nein($"Termin und Ort lassen sich nur festlegen, solange der Antrag bei der/dem Erstprüfenden liegt (Stand: {Status.Anzeigename()}).")
        : kontoId != ErstpruefendeKontoId
            ? Freigabe.Nein("Nur die gewählte Erstprüferin bzw. der gewählte Erstprüfer legt Termin und Ort fest.")
            : Freigabe.Ja;

    /// <summary>Vorläufige Festlegung speichern (nach Rücksprache, noch ohne Unterschrift).</summary>
    public void FestlegungVormerken(Guid kontoId, Festlegung festlegung, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(festlegung);
        KannFestlegen(kontoId).Sicherstellen();
        if (festlegung.Beanstandung(ErstpruefendeKontoId, jetzt) is { } fehler)
        {
            throw new FachlicherFehler(fehler);
        }

        Termin = festlegung.Termin;
        Ort = festlegung.Ort.Trim();
        ZweitpruefendeKontoId = festlegung.ZweitpruefendeKontoId;
        ZweitpruefendeName = festlegung.ZweitpruefendeName.Trim();
        ZweitpruefendeEmail = string.IsNullOrWhiteSpace(festlegung.ZweitpruefendeEmail) ? null : festlegung.ZweitpruefendeEmail.Trim();
        Bemerkung = string.IsNullOrWhiteSpace(festlegung.Bemerkung) ? null : festlegung.Bemerkung.Trim();
    }

    /// <summary>
    /// Erstprüfende unterschreiben die (vorher vorgemerkte) Festlegung; der Antrag geht an das Studiendekanat.
    /// <paramref name="studiendekanatKontoId"/> = <c>null</c>: jede Person mit der Rolle Studiendekanat darf unterschreiben
    /// (z. B. weil die zuständige Person selbst prüft).
    /// </summary>
    public void ErstpruefungUnterschreiben(Guid kontoId, Signatur unterschrift, Guid? studiendekanatKontoId)
    {
        ArgumentNullException.ThrowIfNull(unterschrift);
        KannFestlegen(kontoId).Sicherstellen();
        if (AktuelleFestlegung is null)
        {
            throw new FachlicherFehler("Bitte zuerst Termin, Ort und Zweitprüfende festlegen.");
        }

        PasstZumAntrag(unterschrift, Signaturschritt.Erstpruefung);
        signaturen.Add(unterschrift);
        Rueckfrage = null;
        StudiendekanatKontoId = studiendekanatKontoId;
        Status = AntragStatus.BeimStudiendekanat;
    }

    public Freigabe KannGenehmigen(Guid kontoId, bool istStudiendekanat) => Status != AntragStatus.BeimStudiendekanat
        ? Freigabe.Nein($"Das Studiendekanat unterschreibt erst nach der/dem Erstprüfenden (Stand: {Status.Anzeigename()}).")
        : !istStudiendekanat
            ? Freigabe.Nein("Nur das Studiendekanat bestellt die Prüfenden (ATPO § 24 Abs. 2).")
            : StudiendekanatKontoId is { } zustaendig && zustaendig != kontoId
                ? Freigabe.Nein("Für diesen Studiengang ist eine andere Studiendekanin bzw. ein anderer Studiendekan zuständig.")
                : kontoId == ErstpruefendeKontoId || kontoId == ZweitpruefendeKontoId
                    ? Freigabe.Nein("Wer selbst prüft, bestellt sich nicht selbst; bitte die Vertretung im Studiendekanat unterschreiben lassen.")
                    : Freigabe.Ja;

    /// <summary>Studiendekanat unterschreibt; damit ist der Antrag abgeschlossen (PDF und Versand übernimmt der Dienst).</summary>
    public void StudiendekanatUnterschreiben(Guid kontoId, Signatur unterschrift, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(unterschrift);
        KannGenehmigen(kontoId, istStudiendekanat: true).Sicherstellen();
        PasstZumAntrag(unterschrift, Signaturschritt.Studiendekanat);
        signaturen.Add(unterschrift);
        StudiendekanatKontoId = kontoId;
        Status = AntragStatus.Abgeschlossen;
        Abgeschlossen = jetzt;
        Beendet = jetzt;
    }

    /// <summary>Studiendekanat gibt an die Erstprüfenden zurück (z. B. anderer Termin nötig); deren Unterschrift wird überholt.</summary>
    public void Zurueckgeben(Guid kontoId, string grund, DateTimeOffset jetzt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grund);
        KannGenehmigen(kontoId, istStudiendekanat: true).Sicherstellen();
        foreach (var s in signaturen.Where(s => s.Schritt == Signaturschritt.Erstpruefung && s.Ueberholt is null))
        {
            s.Ueberholen(jetzt, grund);
        }

        Rueckfrage = grund.Trim();
        Status = AntragStatus.BeimErstpruefer;
    }

    public Freigabe KannAblehnen(Guid kontoId, bool istStudiendekanat) => Status switch
    {
        AntragStatus.BeimErstpruefer when kontoId == ErstpruefendeKontoId => Freigabe.Ja,
        AntragStatus.BeimStudiendekanat when istStudiendekanat => KannGenehmigen(kontoId, istStudiendekanat),
        AntragStatus.BeimErstpruefer => Freigabe.Nein("Ablehnen kann nur die gewählte Erstprüferin bzw. der gewählte Erstprüfer."),
        AntragStatus.BeimStudiendekanat => Freigabe.Nein("In diesem Stand entscheidet das Studiendekanat."),
        _ => Freigabe.Nein($"Der Antrag ist nicht mehr in Bearbeitung (Stand: {Status.Anzeigename()})."),
    };

    public void Ablehnen(Guid kontoId, bool istStudiendekanat, string wer, string grund, DateTimeOffset jetzt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grund);
        KannAblehnen(kontoId, istStudiendekanat).Sicherstellen();
        Ablehnungsgrund = grund.Trim();
        AbgelehntVon = wer;
        Status = AntragStatus.Abgelehnt;
        Beendet = jetzt;
    }

    public Freigabe KannZurueckziehen() => Status.IstOffen()
        ? Freigabe.Ja
        : Freigabe.Nein($"Der Antrag ist nicht mehr offen (Stand: {Status.Anzeigename()}). Bitte an das Studierendensekretariat wenden.");

    public void Zurueckziehen(DateTimeOffset jetzt)
    {
        KannZurueckziehen().Sicherstellen();
        Status = AntragStatus.Zurueckgezogen;
        Beendet = jetzt;
    }

    public void Verfallen(DateTimeOffset jetzt)
    {
        if (Status == AntragStatus.WartetAufBestaetigung)
        {
            Status = AntragStatus.Verfallen;
            Beendet = jetzt;
        }
    }

    private void PasstZumAntrag(Signatur unterschrift, Signaturschritt schritt)
    {
        if (unterschrift.AntragId != Id || unterschrift.Schritt != schritt)
        {
            throw new FachlicherFehler("Die Unterschrift gehört nicht zu diesem Schritt des Antrags.");
        }

        if (LetzteGueltigeSignatur is { } vorher && unterschrift.VorgaengerHash != vorher.InhaltHash)
        {
            throw new FachlicherFehler("Der Antrag wurde inzwischen geändert. Bitte die Seite neu laden und erneut unterschreiben.");
        }
    }
}
