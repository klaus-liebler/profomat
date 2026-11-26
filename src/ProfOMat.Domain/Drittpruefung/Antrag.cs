using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Drittpruefung;

/// <summary>Bearbeitungsstand eines Antrags auf mündliche Drittprüfung (ATPO § 18 Abs. 1 S. 3). Gespeichert als Name: nie umbenennen.</summary>
public enum AntragStatus
{
    /// <summary>Altes Verfahren: eingereicht und unterschrieben, die Mailadresse ist aber noch nicht bestätigt.</summary>
    WartetAufBestaetigung = 1,

    /// <summary>Bei der oder dem Erstprüfenden: Termin, Ort und Zweitprüfende festlegen und unterschreiben.</summary>
    BeimErstpruefer = 2,

    /// <summary>Beim Studiendekanat im ProfOMat: Bestellung der Prüfenden unterschreiben.</summary>
    BeimStudiendekanat = 3,

    /// <summary>Von allen unterschrieben, auch vom Studiendekanat.</summary>
    Abgeschlossen = 4,

    /// <summary>Von der oder dem Erstprüfenden bzw. vom Studiendekanat mit Begründung abgelehnt.</summary>
    Abgelehnt = 5,

    /// <summary>Von der oder dem Studierenden zurückgezogen bzw. Einladung von der oder dem Erstprüfenden zurückgenommen.</summary>
    Zurueckgezogen = 6,

    /// <summary>Link nicht rechtzeitig genutzt (altes Verfahren: Mailadresse nicht bestätigt).</summary>
    Verfallen = 7,

    /// <summary>Einladungslink erzeugt; wartet auf Matrikelnummer, Terminwünsche und Unterschrift der oder des Studierenden.</summary>
    Eingeladen = 8,

    /// <summary>Bei der oder dem Zweitprüfenden: unterschreiben.</summary>
    BeimZweitpruefer = 9,

    /// <summary>Das Studiendekanat unterschreibt außerhalb des ProfOMat: die Zweitprüfenden leiten das PDF weiter.</summary>
    ExternWeiterleiten = 10,

    /// <summary>PDF an das Studiendekanat außerhalb des ProfOMat weitergeleitet (Ende im ProfOMat).</summary>
    Weitergeleitet = 11,
}

/// <summary>Wie der Vorgang begonnen hat.</summary>
public enum Antragsverfahren
{
    /// <summary>Bis November 2026: Studierende stellen den Antrag selbst über das öffentliche Formular (nur noch lesbar).</summary>
    Antrag = 1,

    /// <summary>Lehrende erzeugen einen Einladungslink mit vorausgefüllten Angaben; Studierende ergänzen und unterschreiben.</summary>
    Einladung = 2,
}

public static class AntragStatusText
{
    public static string Anzeigename(this AntragStatus status) => status switch
    {
        AntragStatus.WartetAufBestaetigung => "Wartet auf Bestätigung der Mailadresse",
        AntragStatus.Eingeladen => "Eingeladen (wartet auf die/den Studierende/n)",
        AntragStatus.BeimErstpruefer => "Bei der/dem Erstprüfenden",
        AntragStatus.BeimZweitpruefer => "Bei der/dem Zweitprüfenden",
        AntragStatus.BeimStudiendekanat => "Beim Studiendekanat",
        AntragStatus.ExternWeiterleiten => "An das Studiendekanat weiterzuleiten",
        AntragStatus.Weitergeleitet => "An das Studiendekanat weitergeleitet",
        AntragStatus.Abgeschlossen => "Abgeschlossen",
        AntragStatus.Abgelehnt => "Abgelehnt",
        AntragStatus.Zurueckgezogen => "Zurückgezogen",
        AntragStatus.Verfallen => "Verfallen",
        _ => status.ToString(),
    };

    public static bool IstOffen(this AntragStatus status)
        => status is AntragStatus.WartetAufBestaetigung or AntragStatus.Eingeladen or AntragStatus.BeimErstpruefer or AntragStatus.BeimZweitpruefer
            or AntragStatus.BeimStudiendekanat or AntragStatus.ExternWeiterleiten;
}

/// <summary>Was die oder der Lehrende beim Erzeugen des Einladungslinks vorausfüllt (für Studierende nicht änderbar).</summary>
public sealed record Einladungsangaben(string Vorname, string Nachname, Guid StudiengangId, Guid PruefungId, int GueltigTage)
{
    public const int MaxGueltigTage = 60;

    public string? Beanstandung()
    {
        if (string.IsNullOrWhiteSpace(Vorname) || string.IsNullOrWhiteSpace(Nachname))
        {
            return "Bitte Vor- und Nachnamen der/des Studierenden angeben.";
        }

        if (Vorname.Trim().Length > 100 || Nachname.Trim().Length > 100)
        {
            return "Vor- und Nachname bitte mit höchstens 100 Zeichen.";
        }

        if (StudiengangId == Guid.Empty || PruefungId == Guid.Empty)
        {
            return "Bitte Studiengang und Prüfung auswählen.";
        }

        return GueltigTage is < 1 or > MaxGueltigTage ? $"Der Link gilt 1 bis {MaxGueltigTage} Tage." : null;
    }
}

/// <summary>
/// Termin, Ort und Zweitprüfende. Die oder der Erstprüfende kann sie schon beim Einladen vorausfüllen und bis zur eigenen Unterschrift
/// ändern; zum Unterschreiben müssen sie vollständig sein.
/// </summary>
/// <param name="Termin">Beginn der Prüfung (UTC).</param>
/// <param name="Ort">Raum bzw. Ort; die letzte Wiederholung findet nicht per Video statt (ATPO § 6 Abs. 1a S. 2).</param>
/// <param name="ZweitpruefendeKontoId">Zweitprüfende (Konto im ProfOMat, denn sie unterschreiben hier).</param>
/// <param name="ZweitpruefendeName">Name aus dem Konto (setzt der Dienst).</param>
/// <param name="ZweitpruefendeEmail">Mailadresse aus dem Konto (setzt der Dienst).</param>
/// <param name="Bemerkung">Optionaler Hinweis, erscheint auf dem Beiblatt.</param>
public sealed record Festlegung(
    DateTimeOffset? Termin,
    string? Ort,
    Guid? ZweitpruefendeKontoId,
    string? ZweitpruefendeName,
    string? ZweitpruefendeEmail,
    string? Bemerkung)
{
    public string? Beanstandung(Guid erstpruefendeKontoId, DateTimeOffset jetzt, bool vollstaendig)
    {
        if (Termin is { } t && t <= jetzt)
        {
            return "Der Prüfungstermin muss in der Zukunft liegen.";
        }

        if (ZweitpruefendeKontoId == erstpruefendeKontoId)
        {
            return "Erst- und Zweitprüfende müssen verschiedene Personen sein.";
        }

        if ((Ort?.Trim().Length ?? 0) > 200)
        {
            return "Den Ort bitte mit höchstens 200 Zeichen.";
        }

        if ((Bemerkung?.Length ?? 0) > Antrag.MaxFreitext)
        {
            return $"Die Bemerkung bitte mit höchstens {Antrag.MaxFreitext} Zeichen.";
        }

        if (!vollstaendig)
        {
            return null;
        }

        if (Termin is null)
        {
            return "Bitte Datum und Uhrzeit der Prüfung festlegen.";
        }

        if (string.IsNullOrWhiteSpace(Ort))
        {
            return "Bitte den Prüfungsort (Raum) angeben.";
        }

        return ZweitpruefendeKontoId is null || string.IsNullOrWhiteSpace(ZweitpruefendeName)
            ? "Bitte die Zweitprüferin bzw. den Zweitprüfer auswählen (ATPO § 16 Abs. 1 S. 3: letzte Wiederholung mit zwei Prüfenden)."
            : null;
    }
}

/// <summary>
/// Antrag auf mündliche Drittprüfung (zweite Wiederholung einer schriftlichen Prüfung als mündliche Prüfung).
/// Ablauf (Einladung): Erstprüfende erzeugen einen Link mit vorausgefüllten Angaben → Studierende ergänzen Matrikelnummer und
/// Terminwünsche und unterschreiben → Erstprüfende ergänzen Termin, Ort und Zweitprüfende und unterschreiben → Zweitprüfende
/// unterschreiben → Studiendekanat unterschreibt im ProfOMat (Bestellung, ATPO § 24 Abs. 2) oder bekommt das PDF außerhalb.
/// Jede Unterschrift bezieht sich auf den vollständigen Stand und die vorherige Unterschrift.
/// </summary>
public sealed class Antrag
{
    public const int MaxFreitext = 2000;

    private readonly List<Signatur> signaturen = [];

    private Antrag()
    {
    }

    /// <summary>Neuer Vorgang mit Einladungslink. <paramref name="tokenHash"/> ist der SHA-256 des geheimen Links.</summary>
    public static Antrag Einladen(Guid id, string nummer, Einladungsangaben angaben, Guid erstpruefendeKontoId, Festlegung? vorbelegung,
        DateTimeOffset jetzt, string tokenHash)
    {
        ArgumentNullException.ThrowIfNull(angaben);
        ArgumentException.ThrowIfNullOrWhiteSpace(nummer);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        if (angaben.Beanstandung() is { } fehler)
        {
            throw new FachlicherFehler(fehler);
        }

        var antrag = new Antrag
        {
            Id = id,
            Nummer = nummer,
            Verfahren = Antragsverfahren.Einladung,
            Vorname = angaben.Vorname.Trim(),
            Nachname = angaben.Nachname.Trim(),
            StudiengangId = angaben.StudiengangId,
            PruefungId = angaben.PruefungId,
            ErstpruefendeKontoId = erstpruefendeKontoId,
            Erstellt = jetzt,
            StatusTokenHash = tokenHash,
            GueltigBis = jetzt.AddDays(angaben.GueltigTage),
            Status = AntragStatus.Eingeladen,
        };
        if (vorbelegung is not null)
        {
            antrag.FestlegungSetzen(erstpruefendeKontoId, vorbelegung, jetzt);
        }

        return antrag;
    }

    public Guid Id { get; private set; }

    public string Nummer { get; private set; } = string.Empty;

    public Antragsverfahren Verfahren { get; private set; } = Antragsverfahren.Antrag;

    public AntragStatus Status { get; private set; }

    /// <summary>Beginn des Vorgangs (Einladung bzw. altes Verfahren: Antragstellung); steht im unterschriebenen Text.</summary>
    public DateTimeOffset Erstellt { get; private set; }

    /// <summary>Bis dahin können Studierende über den Link unterschreiben (Einladung).</summary>
    public DateTimeOffset? GueltigBis { get; private set; }

    // ---- Angaben der oder des Studierenden ----
    public string Vorname { get; private set; } = string.Empty;

    public string Nachname { get; private set; } = string.Empty;

    /// <summary>Leer, bis die oder der Studierende unterschrieben hat.</summary>
    public string Matrikelnummer { get; private set; } = string.Empty;

    /// <summary>Nur im alten Verfahren; bei Einladungen leer (den Link schicken die Lehrenden selbst).</summary>
    public string Email { get; private set; } = string.Empty;

    public Guid StudiengangId { get; private set; }

    public Guid PruefungId { get; private set; }

    public Guid ErstpruefendeKontoId { get; private set; }

    public string? Terminwuensche { get; private set; }

    /// <summary>Nur im alten Verfahren.</summary>
    public string? Zweitpruefungswunsch { get; private set; }

    /// <summary>SHA-256 des geheimen Links für Studierende; das Token selbst wird nie gespeichert.</summary>
    public string StatusTokenHash { get; private set; } = string.Empty;

    /// <summary>Nur im alten Verfahren.</summary>
    public DateTimeOffset? EmailBestaetigt { get; private set; }

    // ---- Festlegung durch die oder den Erstprüfenden ----
    public DateTimeOffset? Termin { get; private set; }

    public string? Ort { get; private set; }

    public Guid? ZweitpruefendeKontoId { get; private set; }

    public string? ZweitpruefendeName { get; private set; }

    public string? ZweitpruefendeEmail { get; private set; }

    public string? Bemerkung { get; private set; }

    /// <summary>Offene Rückfrage an die Erstprüfenden (bis zur nächsten Unterschrift).</summary>
    public string? Rueckfrage { get; private set; }

    /// <summary>Zuständiges Studiendekanat; <c>null</c> = jede Person mit der Rolle außer den Prüfenden.</summary>
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

    public Festlegung AktuelleFestlegung => new(Termin, Ort, ZweitpruefendeKontoId, ZweitpruefendeName, ZweitpruefendeEmail, Bemerkung);

    // =====================================================================================================
    // Einladung
    // =====================================================================================================

    private Freigabe NurEinladung() => Verfahren == Antragsverfahren.Einladung
        ? Freigabe.Ja
        : Freigabe.Nein("Dieser Antrag stammt aus dem alten Verfahren; er lässt sich nur noch ablehnen.");

    /// <summary>Studierende können über den Link unterschreiben.</summary>
    public Freigabe KannStudierendUnterschreiben(DateTimeOffset jetzt) => NurEinladung().Und(() => Status != AntragStatus.Eingeladen
        ? Freigabe.Nein($"Der Antrag ist bereits unterschrieben oder nicht mehr offen (Stand: {Status.Anzeigename()}).")
        : GueltigBis is { } bis && jetzt > bis
            ? Freigabe.Nein("Der Link ist abgelaufen. Bitte bei der Prüferin bzw. dem Prüfer einen neuen Link anfordern.")
            : Freigabe.Ja);

    /// <summary>Die oder der Studierende ergänzt Matrikelnummer und Terminwünsche und unterschreibt; der Antrag geht an die Erstprüfenden.</summary>
    public void StudierendUnterschreiben(string matrikelnummer, string? terminwuensche, Signatur unterschrift, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(unterschrift);
        KannStudierendUnterschreiben(jetzt).Sicherstellen();
        if (StudierendenBeanstandung(matrikelnummer, terminwuensche) is { } fehler)
        {
            throw new FachlicherFehler(fehler);
        }

        PasstZumAntrag(unterschrift, Signaturschritt.Antragstellung);
        Matrikelnummer = matrikelnummer.Trim();
        Terminwuensche = string.IsNullOrWhiteSpace(terminwuensche) ? null : terminwuensche.Trim();
        signaturen.Add(unterschrift);
        Status = AntragStatus.BeimErstpruefer;
    }

    public static string? StudierendenBeanstandung(string? matrikelnummer, string? terminwuensche)
        => MatrikelnummerBeanstandung(matrikelnummer)
           ?? ((terminwuensche?.Length ?? 0) > Antrag.MaxFreitext ? $"Terminwünsche bitte mit höchstens {MaxFreitext} Zeichen." : null);

    /// <summary>Prüft eine Matrikelnummer (5 bis 10 Ziffern); liefert die Beanstandung oder <c>null</c>.</summary>
    public static string? MatrikelnummerBeanstandung(string? matrikelnummer)
        => string.IsNullOrWhiteSpace(matrikelnummer) || !matrikelnummer.Trim().All(char.IsAsciiDigit) || matrikelnummer.Trim().Length is < 5 or > 10
            ? "Die Matrikelnummer besteht aus 5 bis 10 Ziffern."
            : null;

    public Freigabe KannLinkErneuern(Guid kontoId) => NurEinladung().Und(() => kontoId != ErstpruefendeKontoId
        ? Freigabe.Nein("Den Link erneuert die Erstprüferin bzw. der Erstprüfer, die/der ihn erzeugt hat.")
        : Status == AntragStatus.Eingeladen || (Status == AntragStatus.Verfallen && signaturen.Count == 0)
            ? Freigabe.Ja
            : Freigabe.Nein("Die/der Studierende hat bereits unterschrieben; ein neuer Link ist nicht nötig."));

    /// <summary>Neuer Link (z. B. Mail verloren oder abgelaufen); der alte gilt nicht mehr.</summary>
    public void LinkErneuern(Guid kontoId, string tokenHash, int gueltigTage, DateTimeOffset jetzt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        KannLinkErneuern(kontoId).Sicherstellen();
        if (gueltigTage is < 1 or > Einladungsangaben.MaxGueltigTage)
        {
            throw new FachlicherFehler($"Der Link gilt 1 bis {Einladungsangaben.MaxGueltigTage} Tage.");
        }

        StatusTokenHash = tokenHash;
        GueltigBis = jetzt.AddDays(gueltigTage);
        Status = AntragStatus.Eingeladen;
        Beendet = null;
    }

    public Freigabe KannEinladungZuruecknehmen(Guid kontoId) => NurEinladung().Und(() => kontoId != ErstpruefendeKontoId
        ? Freigabe.Nein("Nur die Erstprüferin bzw. der Erstprüfer nimmt die Einladung zurück.")
        : Status == AntragStatus.Eingeladen
            ? Freigabe.Ja
            : Freigabe.Nein("Nach der Unterschrift der/des Studierenden bitte „Ablehnen“ verwenden."));

    public void EinladungZuruecknehmen(Guid kontoId, DateTimeOffset jetzt)
    {
        KannEinladungZuruecknehmen(kontoId).Sicherstellen();
        Status = AntragStatus.Zurueckgezogen;
        Beendet = jetzt;
    }

    // =====================================================================================================
    // Erstprüfende
    // =====================================================================================================

    public Freigabe KannFestlegen(Guid kontoId) => NurEinladung().Und(() => Status is not (AntragStatus.Eingeladen or AntragStatus.BeimErstpruefer)
        ? Freigabe.Nein($"Termin, Ort und Zweitprüfende lassen sich nur bis zur Unterschrift der/des Erstprüfenden ändern (Stand: {Status.Anzeigename()}).")
        : kontoId != ErstpruefendeKontoId
            ? Freigabe.Nein("Nur die Erstprüferin bzw. der Erstprüfer legt Termin und Ort fest.")
            : Freigabe.Ja);

    /// <summary>Termin, Ort, Zweitprüfende vormerken (beim Einladen oder danach; noch ohne Unterschrift).</summary>
    public void FestlegungSetzen(Guid kontoId, Festlegung festlegung, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(festlegung);
        KannFestlegen(kontoId).Sicherstellen();
        if (festlegung.Beanstandung(ErstpruefendeKontoId, jetzt, vollstaendig: false) is { } fehler)
        {
            throw new FachlicherFehler(fehler);
        }

        Termin = festlegung.Termin;
        Ort = string.IsNullOrWhiteSpace(festlegung.Ort) ? null : festlegung.Ort.Trim();
        ZweitpruefendeKontoId = festlegung.ZweitpruefendeKontoId;
        ZweitpruefendeName = festlegung.ZweitpruefendeKontoId is null || string.IsNullOrWhiteSpace(festlegung.ZweitpruefendeName) ? null : festlegung.ZweitpruefendeName.Trim();
        ZweitpruefendeEmail = ZweitpruefendeName is null || string.IsNullOrWhiteSpace(festlegung.ZweitpruefendeEmail) ? null : festlegung.ZweitpruefendeEmail.Trim();
        Bemerkung = string.IsNullOrWhiteSpace(festlegung.Bemerkung) ? null : festlegung.Bemerkung.Trim();
    }

    public Freigabe KannErstpruefungUnterschreiben(Guid kontoId, DateTimeOffset jetzt) => KannFestlegen(kontoId).Und(() => Status != AntragStatus.BeimErstpruefer
        ? Freigabe.Nein("Erst unterschreibt die/der Studierende über den Link, dann die Erstprüferin bzw. der Erstprüfer.")
        : AktuelleFestlegung.Beanstandung(ErstpruefendeKontoId, jetzt, vollstaendig: true) is { } fehler
            ? Freigabe.Nein(fehler)
            : Freigabe.Ja);

    /// <summary>Erstprüfende unterschreiben; der Antrag geht an die Zweitprüfenden.</summary>
    public void ErstpruefungUnterschreiben(Guid kontoId, Signatur unterschrift, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(unterschrift);
        KannErstpruefungUnterschreiben(kontoId, jetzt).Sicherstellen();
        PasstZumAntrag(unterschrift, Signaturschritt.Erstpruefung);
        signaturen.Add(unterschrift);
        Rueckfrage = null;
        Status = AntragStatus.BeimZweitpruefer;
    }

    // =====================================================================================================
    // Zweitprüfende
    // =====================================================================================================

    public Freigabe KannZweitpruefungUnterschreiben(Guid kontoId) => NurEinladung().Und(() => Status != AntragStatus.BeimZweitpruefer
        ? Freigabe.Nein($"Die/der Zweitprüfende unterschreibt nach der/dem Erstprüfenden (Stand: {Status.Anzeigename()}).")
        : kontoId != ZweitpruefendeKontoId
            ? Freigabe.Nein($"Hier unterschreibt die/der festgelegte Zweitprüfende ({ZweitpruefendeName}).")
            : Freigabe.Ja);

    /// <summary>
    /// Zweitprüfende unterschreiben. Mit <paramref name="imSystem"/> geht der Antrag an das Studiendekanat im ProfOMat
    /// (<paramref name="studiendekanatKontoId"/> = <c>null</c>: jede Person mit der Rolle außer den Prüfenden), sonst leiten die
    /// Zweitprüfenden das PDF außerhalb weiter.
    /// </summary>
    public void ZweitpruefungUnterschreiben(Guid kontoId, Signatur unterschrift, bool imSystem, Guid? studiendekanatKontoId)
    {
        ArgumentNullException.ThrowIfNull(unterschrift);
        KannZweitpruefungUnterschreiben(kontoId).Sicherstellen();
        PasstZumAntrag(unterschrift, Signaturschritt.Zweitpruefung);
        signaturen.Add(unterschrift);
        StudiendekanatKontoId = imSystem ? studiendekanatKontoId : null;
        Status = imSystem ? AntragStatus.BeimStudiendekanat : AntragStatus.ExternWeiterleiten;
    }

    public Freigabe KannWeitergeleitetMelden(Guid kontoId) => Status != AntragStatus.ExternWeiterleiten
        ? Freigabe.Nein("Das ist nur nötig, wenn das Studiendekanat außerhalb des ProfOMat unterschreibt.")
        : kontoId != ZweitpruefendeKontoId && kontoId != ErstpruefendeKontoId
            ? Freigabe.Nein("Das melden die Prüfenden.")
            : Freigabe.Ja;

    /// <summary>Das PDF ist an das Studiendekanat außerhalb gegangen; im ProfOMat ist der Vorgang damit beendet.</summary>
    public void WeitergeleitetMelden(Guid kontoId, DateTimeOffset jetzt)
    {
        KannWeitergeleitetMelden(kontoId).Sicherstellen();
        Status = AntragStatus.Weitergeleitet;
        Beendet = jetzt;
    }

    // =====================================================================================================
    // Studiendekanat
    // =====================================================================================================

    public Freigabe KannGenehmigen(Guid kontoId, bool istStudiendekanat) => Status != AntragStatus.BeimStudiendekanat
        ? Freigabe.Nein($"Das Studiendekanat unterschreibt nach den Prüfenden (Stand: {Status.Anzeigename()}).")
        : !istStudiendekanat
            ? Freigabe.Nein("Nur das Studiendekanat bestellt die Prüfenden (ATPO § 24 Abs. 2).")
            : StudiendekanatKontoId is { } zustaendig && zustaendig != kontoId
                ? Freigabe.Nein("Für diesen Studiengang ist eine andere Studiendekanin bzw. ein anderer Studiendekan zuständig.")
                : kontoId == ErstpruefendeKontoId || kontoId == ZweitpruefendeKontoId
                    ? Freigabe.Nein("Wer selbst prüft, bestellt sich nicht selbst; bitte die Vertretung im Studiendekanat unterschreiben lassen.")
                    : Freigabe.Ja;

    /// <summary>Studiendekanat unterschreibt; damit ist der Antrag abgeschlossen.</summary>
    public void StudiendekanatUnterschreiben(Guid kontoId, Signatur unterschrift, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(unterschrift);
        KannGenehmigen(kontoId, istStudiendekanat: true).Und(NurEinladung).Sicherstellen();
        PasstZumAntrag(unterschrift, Signaturschritt.Studiendekanat);
        signaturen.Add(unterschrift);
        StudiendekanatKontoId = kontoId;
        Status = AntragStatus.Abgeschlossen;
        Abgeschlossen = jetzt;
        Beendet = jetzt;
    }

    // =====================================================================================================
    // Zurückgeben, Ablehnen, Zurückziehen, Verfallen
    // =====================================================================================================

    public Freigabe KannZurueckgeben(Guid kontoId, bool istStudiendekanat) => NurEinladung().Und(() => Status switch
    {
        AntragStatus.BeimZweitpruefer or AntragStatus.ExternWeiterleiten when kontoId == ZweitpruefendeKontoId => Freigabe.Ja,
        AntragStatus.BeimStudiendekanat => KannGenehmigen(kontoId, istStudiendekanat),
        AntragStatus.BeimZweitpruefer or AntragStatus.ExternWeiterleiten => Freigabe.Nein("Zurückgeben kann hier nur die/der Zweitprüfende."),
        _ => Freigabe.Nein("Zurückgeben geht erst nach der Unterschrift der/des Erstprüfenden."),
    });

    /// <summary>Zurück an die Erstprüfenden (z. B. anderer Termin nötig); deren und die folgenden Unterschriften werden überholt.</summary>
    public void Zurueckgeben(Guid kontoId, bool istStudiendekanat, string wer, string grund, DateTimeOffset jetzt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grund);
        KannZurueckgeben(kontoId, istStudiendekanat).Sicherstellen();
        foreach (var s in signaturen.Where(s => s.Schritt != Signaturschritt.Antragstellung && s.Ueberholt is null))
        {
            s.Ueberholen(jetzt, grund);
        }

        Rueckfrage = $"{wer}: {grund.Trim()}";
        StudiendekanatKontoId = null;
        Status = AntragStatus.BeimErstpruefer;
    }

    public Freigabe KannAblehnen(Guid kontoId, bool istStudiendekanat) => Status switch
    {
        AntragStatus.BeimErstpruefer when kontoId == ErstpruefendeKontoId => Freigabe.Ja,
        AntragStatus.BeimStudiendekanat when istStudiendekanat => KannGenehmigen(kontoId, istStudiendekanat),
        AntragStatus.Eingeladen => Freigabe.Nein("Vor der Unterschrift der/des Studierenden die Einladung zurücknehmen."),
        AntragStatus.BeimErstpruefer => Freigabe.Nein("Ablehnen kann nur die Erstprüferin bzw. der Erstprüfer."),
        AntragStatus.BeimZweitpruefer or AntragStatus.ExternWeiterleiten => Freigabe.Nein("Bitte an die/den Erstprüfende/n zurückgeben."),
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

    /// <summary>Die oder der Studierende zieht über den Link zurück.</summary>
    public void Zurueckziehen(DateTimeOffset jetzt)
    {
        KannZurueckziehen().Sicherstellen();
        Status = AntragStatus.Zurueckgezogen;
        Beendet = jetzt;
    }

    /// <summary>Ungenutzte Links (bzw. im alten Verfahren unbestätigte Anträge) verfallen.</summary>
    public bool Verfallen(DateTimeOffset jetzt, DateTimeOffset altGrenze)
    {
        var faellig = Status switch
        {
            AntragStatus.Eingeladen => GueltigBis is { } bis && jetzt > bis,
            AntragStatus.WartetAufBestaetigung => Erstellt < altGrenze,
            _ => false,
        };
        if (faellig)
        {
            Status = AntragStatus.Verfallen;
            Beendet = jetzt;
        }

        return faellig;
    }

    private void PasstZumAntrag(Signatur unterschrift, Signaturschritt schritt)
    {
        if (unterschrift.AntragId != Id || unterschrift.Schritt != schritt)
        {
            throw new FachlicherFehler("Die Unterschrift gehört nicht zu diesem Schritt des Antrags.");
        }

        if (unterschrift.VorgaengerHash != LetzteGueltigeSignatur?.InhaltHash)
        {
            throw new FachlicherFehler("Der Antrag wurde inzwischen geändert. Bitte die Seite neu laden und erneut unterschreiben.");
        }
    }
}
