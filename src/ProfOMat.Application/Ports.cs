using ProfOMat.Application.Drittpruefung;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application;

/// <summary>
/// Allgemeiner Datenzugang (wie FraktionPerfekt): Abfragen als <see cref="IQueryable{T}"/>, die Anwendung kennt EF Core nicht.
/// <see cref="SpeichernAsync"/> schreibt fachliche Änderung, Postausgang und Protokoll gemeinsam fest.
/// Begrenzte Abfragen immer eindeutig sortieren.
/// </summary>
public interface IDaten
{
    IQueryable<T> Menge<T>()
        where T : class;

    void Hinzufuegen<T>(T eintrag)
        where T : class;

    /// <summary>Löscht eine Wurzel (abhängige Einträge per Kaskade).</summary>
    void Entfernen<T>(T eintrag)
        where T : class;

    Task<T?> FindenAsync<T>(Guid id, CancellationToken abbruch = default)
        where T : class;

    Task<List<T>> ListeAsync<T>(IQueryable<T> abfrage, CancellationToken abbruch = default);

    Task<T?> ErsterAsync<T>(IQueryable<T> abfrage, CancellationToken abbruch = default);

    Task<int> AnzahlAsync<T>(IQueryable<T> abfrage, CancellationToken abbruch = default);

    Task SpeichernAsync(CancellationToken abbruch = default);
}

/// <summary>Wer handelt gerade? Ohne Anmeldung (Studierende über ihren Link, Hintergrundjobs) ist es <see cref="Benutzer.Niemand"/>.</summary>
public interface IAktuellerBenutzer
{
    ValueTask<Benutzer> ErmittelnAsync(CancellationToken abbruch = default);
}

/// <param name="Kennung">Anmeldename.</param>
/// <param name="Name">Anzeigename.</param>
/// <param name="KontoId">Konto; <c>null</c> bei <see cref="Niemand"/>.</param>
/// <param name="Rollen">Alle Rollen des Kontos gelten gleichzeitig (z. B. Prüfende und Studiendekanat).</param>
public sealed record Benutzer(string Kennung, string Name, Guid? KontoId, IReadOnlyList<Rolle> Rollen)
{
    public static Benutzer Niemand { get; } = new("niemand", "Ohne Anmeldung", null, []);

    public static Benutzer Automatik { get; } = new("automatik", "Automatik", null, []);

    public bool Angemeldet => KontoId is not null;

    public bool Hat(Rolle rolle) => Rollen.Contains(rolle);

    public Guid KontoIdPflicht => KontoId ?? throw new Domain.Gemeinsam.FachlicherFehler("Bitte zuerst anmelden.");
}

/// <summary>Systemsiegel: Signatur des Servers über jede Unterschrift (Schlüssel liegt nur auf dem Server).</summary>
public interface ISystemsiegel
{
    /// <summary>Fingerabdruck des aktuellen Siegelschlüssels.</summary>
    string SchluesselFingerabdruck { get; }

    string Versiegeln(string text);

    bool Pruefen(string text, string siegel, string schluesselFingerabdruck);
}

/// <summary>Quelle für Studiengänge und Prüfungen (HISinOne o. Ä.); heute ein Mock.</summary>
public interface IHochschulKatalog
{
    /// <summary>Name der Quelle für Anzeige und Protokoll.</summary>
    string Quelle { get; }

    Task<KatalogStand> LadenAsync(CancellationToken abbruch = default);
}

public sealed record KatalogStand(IReadOnlyList<FakultaetQuelle> Fakultaeten);

public sealed record FakultaetQuelle(string Kuerzel, string Name, IReadOnlyList<StudiengangQuelle> Studiengaenge);

public sealed record StudiengangQuelle(string Schluessel, string Kuerzel, string Name, string Abschluss, string Pruefungsordnung, IReadOnlyList<PruefungQuelle> Pruefungen);

public sealed record PruefungQuelle(string Schluessel, string Nummer, string Titel, string? Pruefungsform);

/// <summary>Erzeugt das PDF des abgeschlossenen Antrags.</summary>
public interface IAntragPdf
{
    byte[] Erzeugen(AntragAnsicht antrag);
}

/// <summary>Stellt eine Mail zu (SMTP); <c>false</c> = nur simuliert (kein Mailserver eingerichtet).</summary>
public interface IMailZustellung
{
    Task<bool> ZustellenAsync(string an, string betreff, string text, (string Name, string Typ, byte[] Inhalt)? anhang, CancellationToken abbruch = default);
}

/// <summary>Einstellungen (Abschnitt „ProfOMat“).</summary>
public sealed class ProfOMatOptionen
{
    public const string Abschnitt = "ProfOMat";

    public string Hochschule { get; set; } = "Hochschule Osnabrück";

    /// <summary>Öffentliche Adresse mit abschließendem Schrägstrich, für Links in Mails und im PDF.</summary>
    public string OeffentlicheAdresse { get; set; } = "http://localhost:5310/";

    /// <summary>Zentrales Postfach des Studierendensekretariats (falls die Fakultät keines festlegt).</summary>
    public string SekretariatEmail { get; set; } = "studierendensekretariat@example.invalid";

    /// <summary>Vorschlag, wie viele Tage ein Einladungslink zur Drittprüfung gilt (änderbar je Link, 1–60).</summary>
    public int EinladungGueltigTage { get; set; } = 14;

    /// <summary>Zeitzone für Anzeige und Eingabe von Prüfungsterminen.</summary>
    public string Zeitzone { get; set; } = "Europe/Berlin";
}
