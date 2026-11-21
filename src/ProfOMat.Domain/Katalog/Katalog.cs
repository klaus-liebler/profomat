using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Katalog;

/// <summary>Woher ein Katalogeintrag stammt.</summary>
public enum Herkunft
{
    /// <summary>Aus dem Hochschulsystem abgeglichen (Schlüssel = dortiger Schlüssel).</summary>
    Hochschulsystem = 1,

    /// <summary>Hier ergänzt (z. B. Prüfung fehlt noch im Hochschulsystem). Der Abgleich fasst sie nie an.</summary>
    Lokal = 2,
}

/// <summary>
/// Gemeinsames Verhalten von Studiengängen und Prüfungen: Einträge werden NIE gelöscht. Verschwindet ein Eintrag aus dem
/// Hochschulsystem oder wird er von Hand archiviert, wird er „alt“. Alte Einträge erscheinen in Auswahlen nur auf Wunsch
/// („auch alte anzeigen“), bleiben aber für bestehende Anträge, Suche und Auswertung für immer erhalten.
/// </summary>
public abstract class Katalogeintrag
{
    public Guid Id { get; protected set; } = Ids.Neu();

    /// <summary>Schlüssel im Hochschulsystem; bei lokalen Einträgen ein eigener Schlüssel „lokal:…“.</summary>
    public string Schluessel { get; protected set; } = string.Empty;

    public Herkunft Herkunft { get; protected set; }

    /// <summary>Seit wann alt; <c>null</c> = aktuell.</summary>
    public DateTimeOffset? AltSeit { get; private set; }

    public string? AltGrund { get; private set; }

    /// <summary>Zuletzt im Hochschulsystem gesehen (Abgleich).</summary>
    public DateTimeOffset? ZuletztAbgeglichen { get; private set; }

    /// <summary>Von Hand archiviert: der Abgleich holt den Eintrag dann nicht von selbst zurück.</summary>
    public bool VonHandArchiviert { get; private set; }

    public bool IstAlt => AltSeit is not null;

    public void AlsAltMarkieren(DateTimeOffset jetzt, string grund, bool vonHand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grund);
        if (AltSeit is null)
        {
            AltSeit = jetzt;
            AltGrund = grund.Trim();
        }

        VonHandArchiviert |= vonHand;
    }

    public void Reaktivieren()
    {
        AltSeit = null;
        AltGrund = null;
        VonHandArchiviert = false;
    }

    /// <summary>Im Hochschulsystem (wieder) gesehen. Nicht von Hand archivierte alte Einträge werden wieder aktuell.</summary>
    internal bool Gesehen(DateTimeOffset jetzt)
    {
        ZuletztAbgeglichen = jetzt;
        if (AltSeit is not null && !VonHandArchiviert)
        {
            AltSeit = null;
            AltGrund = null;
            return true;
        }

        return false;
    }
}

public sealed class Fakultaet
{
    private Fakultaet()
    {
    }

    public Fakultaet(string kuerzel, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kuerzel);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = Ids.Neu();
        Kuerzel = kuerzel.Trim();
        Name = name.Trim();
    }

    public Guid Id { get; private set; }

    public string Kuerzel { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Zuständige Studiendekanin bzw. zuständiger Studiendekan (Konto), falls der Studiengang nichts anderes festlegt.</summary>
    public Guid? StudiendekanatKontoId { get; private set; }

    /// <summary>Postfach des Studierendensekretariats bzw. der Prüfungsverwaltung für diese Fakultät; leer = zentrale Adresse.</summary>
    public string? SekretariatEmail { get; private set; }

    public void Aendern(string name, Guid? studiendekanat, string? sekretariatEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        StudiendekanatKontoId = studiendekanat;
        SekretariatEmail = string.IsNullOrWhiteSpace(sekretariatEmail) ? null : sekretariatEmail.Trim();
    }
}

public sealed class Studiengang : Katalogeintrag
{
    private Studiengang()
    {
    }

    public Studiengang(Guid fakultaetId, string schluessel, string kuerzel, string name, string abschluss, string pruefungsordnung, Herkunft herkunft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schluessel);
        FakultaetId = fakultaetId;
        Schluessel = schluessel.Trim();
        Herkunft = herkunft;
        Uebernehmen(kuerzel, name, abschluss, pruefungsordnung);
    }

    public Guid FakultaetId { get; private set; }

    public string Kuerzel { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>B.Sc., B.Eng., M.Sc. …</summary>
    public string Abschluss { get; private set; } = string.Empty;

    /// <summary>Fassung der Prüfungsordnung (z. B. „PO 2023“).</summary>
    public string Pruefungsordnung { get; private set; } = string.Empty;

    /// <summary>Abweichende Studiendekanin bzw. abweichender Studiendekan für diesen Studiengang.</summary>
    public Guid? StudiendekanatKontoId { get; private set; }

    public string Anzeige => $"{Name} ({Abschluss}, {Pruefungsordnung})";

    /// <summary>Übernimmt Angaben aus dem Hochschulsystem; liefert, ob sich etwas geändert hat.</summary>
    public bool Uebernehmen(string kuerzel, string name, string abschluss, string pruefungsordnung)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var neu = (kuerzel?.Trim() ?? string.Empty, name.Trim(), abschluss?.Trim() ?? string.Empty, pruefungsordnung?.Trim() ?? string.Empty);
        if (neu == (Kuerzel, Name, Abschluss, Pruefungsordnung))
        {
            return false;
        }

        (Kuerzel, Name, Abschluss, Pruefungsordnung) = neu;
        return true;
    }

    public void StudiendekanatFestlegen(Guid? kontoId) => StudiendekanatKontoId = kontoId;

    internal bool Abgleichen(DateTimeOffset jetzt) => Gesehen(jetzt);
}

public sealed class Pruefung : Katalogeintrag
{
    private Pruefung()
    {
    }

    public Pruefung(Guid studiengangId, string schluessel, string nummer, string titel, string? pruefungsform, Herkunft herkunft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schluessel);
        StudiengangId = studiengangId;
        Schluessel = schluessel.Trim();
        Herkunft = herkunft;
        Uebernehmen(nummer, titel, pruefungsform);
    }

    public Guid StudiengangId { get; private set; }

    /// <summary>Prüfungsnummer im Hochschulsystem.</summary>
    public string Nummer { get; private set; } = string.Empty;

    public string Titel { get; private set; } = string.Empty;

    /// <summary>Reguläre Prüfungsform laut Modulbeschreibung (z. B. „Klausur 2 h“).</summary>
    public string? Pruefungsform { get; private set; }

    public string Anzeige => string.IsNullOrEmpty(Nummer) ? Titel : $"{Nummer} – {Titel}";

    public bool Uebernehmen(string nummer, string titel, string? pruefungsform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titel);
        var neu = (nummer?.Trim() ?? string.Empty, titel.Trim(), string.IsNullOrWhiteSpace(pruefungsform) ? null : pruefungsform.Trim());
        if (neu == (Nummer, Titel, Pruefungsform))
        {
            return false;
        }

        (Nummer, Titel, Pruefungsform) = neu;
        return true;
    }

    internal bool Abgleichen(DateTimeOffset jetzt) => Gesehen(jetzt);
}

/// <summary>Zugriff auf das interne Abgleichverhalten für den Abgleichdienst (Application).</summary>
public static class KatalogAbgleichHilfe
{
    /// <summary>Eintrag im Hochschulsystem gesehen; <c>true</c>, wenn er dadurch wieder aktuell wurde.</summary>
    public static bool Gesehen(Katalogeintrag eintrag, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(eintrag);
        return eintrag switch
        {
            Studiengang s => s.Abgleichen(jetzt),
            Pruefung p => p.Abgleichen(jetzt),
            _ => false,
        };
    }
}
