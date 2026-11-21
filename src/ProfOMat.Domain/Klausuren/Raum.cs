using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Klausuren;

/// <summary>Was im Grundriss steht. Alles sind Rechtecke im Raster (Zellen).</summary>
public enum Raumelementart
{
    Tisch = 1,
    Tuer = 2,
    Fenster = 3,
    Tafel = 4,
    Projektion = 5,
    Pult = 6,
}

/// <summary>Ecke, in der die Tischnummerierung beginnt (bezogen auf die Zeichnung: oben = Wand oben).</summary>
public enum Startecke
{
    ObenLinks = 1,
    ObenRechts = 2,
    UntenLinks = 3,
    UntenRechts = 4,
}

/// <summary>Nummerierung reihenweise (waagerecht) oder spaltenweise (senkrecht).</summary>
public enum Laufrichtung
{
    Reihen = 1,
    Spalten = 2,
}

public static class RaumplanText
{
    public static string Anzeigename(this Raumelementart art) => art switch
    {
        Raumelementart.Tisch => "Tisch",
        Raumelementart.Tuer => "Tür",
        Raumelementart.Fenster => "Fenster",
        Raumelementart.Tafel => "Tafel",
        Raumelementart.Projektion => "Projektion",
        Raumelementart.Pult => "Pult",
        _ => art.ToString(),
    };

    public static string Anzeigename(this Startecke e) => e switch
    {
        Startecke.ObenLinks => "oben links",
        Startecke.ObenRechts => "oben rechts",
        Startecke.UntenLinks => "unten links",
        _ => "unten rechts",
    };
}

/// <summary>Element im Grundriss (Rasterzellen, links oben = 0/0).</summary>
public sealed class Raumelement
{
    private Raumelement()
    {
    }

    public Raumelement(Raumelementart art, int x, int y, int breite, int hoehe)
    {
        Id = Ids.Neu();
        (Art, X, Y, Breite, Hoehe) = (art, x, y, breite, hoehe);
    }

    public Guid Id { get; private set; }

    public Guid RaumId { get; private set; }

    public Raumelementart Art { get; private set; }

    public int X { get; private set; }

    public int Y { get; private set; }

    public int Breite { get; private set; }

    public int Hoehe { get; private set; }

    public bool Belegt(int x, int y) => x >= X && x < X + Breite && y >= Y && y < Y + Hoehe;

    public bool Ueberlappt(Raumelement o) => X < o.X + o.Breite && o.X < X + Breite && Y < o.Y + o.Hoehe && o.Y < Y + Hoehe;
}

/// <summary>Ein Sitzplatz mit fortlaufender Tischnummer; Reihe und Spalte zählen ab der Startecke (1 = erste).</summary>
public sealed record Sitzplatz(int Nummer, int Reihe, int Spalte, int X, int Y);

/// <summary>Beschreibung eines Grundrisses (für das Speichern aus dem Editor).</summary>
public sealed record Raumplan(int Spalten, int Zeilen, IReadOnlyList<(Raumelementart Art, int X, int Y, int Breite, int Hoehe)> Elemente,
    Startecke Startecke, Laufrichtung Richtung, bool Schlange);

/// <summary>
/// Raum mit Klausurplätzen. Ohne Grundriss gilt die eingetragene Platzzahl (Plätze 1 … n). Mit Grundriss ergeben sich die Plätze
/// aus den Tischen; die Tischnummern vergibt das gewählte Muster (Startecke, Reihen/Spalten, Schlangenlinie).
/// </summary>
public sealed class Raum
{
    public const int MaxRaster = 60;

    private readonly List<Raumelement> elemente = [];

    private Raum()
    {
    }

    public Raum(string kuerzel, string? bezeichnung, int plaetze)
    {
        Id = Ids.Neu();
        Aendern(kuerzel, bezeichnung, plaetze, true);
        RasterSpalten = 20;
        RasterZeilen = 14;
        Startecke = Startecke.ObenLinks;
        Richtung = Laufrichtung.Reihen;
    }

    public Guid Id { get; private set; }

    /// <summary>z. B. „SI 0012“.</summary>
    public string Kuerzel { get; private set; } = string.Empty;

    /// <summary>z. B. „Hörsaal, Campus Westerberg“.</summary>
    public string? Bezeichnung { get; private set; }

    /// <summary>Klausurplätze (mit Grundriss = Zahl der Tische).</summary>
    public int Plaetze { get; private set; }

    public bool Aktiv { get; private set; }

    public int RasterSpalten { get; private set; }

    public int RasterZeilen { get; private set; }

    public Startecke Startecke { get; private set; }

    public Laufrichtung Richtung { get; private set; }

    public bool Schlange { get; private set; }

    public IReadOnlyList<Raumelement> Elemente => elemente;

    public bool HatGrundriss => elemente.Any(e => e.Art == Raumelementart.Tisch);

    public string Anzeige => Bezeichnung is null ? $"{Kuerzel} ({Plaetze} Plätze)" : $"{Kuerzel} – {Bezeichnung} ({Plaetze} Plätze)";

    public void Aendern(string kuerzel, string? bezeichnung, int plaetze, bool aktiv)
    {
        if (string.IsNullOrWhiteSpace(kuerzel))
        {
            throw new FachlicherFehler("Bitte das Raumkürzel angeben.");
        }

        if (!HatGrundriss && plaetze is < 1 or > 2000)
        {
            throw new FachlicherFehler("Die Platzzahl liegt zwischen 1 und 2000.");
        }

        Kuerzel = kuerzel.Trim();
        Bezeichnung = string.IsNullOrWhiteSpace(bezeichnung) ? null : bezeichnung.Trim();
        Plaetze = HatGrundriss ? elemente.Count(e => e.Art == Raumelementart.Tisch) : plaetze;
        Aktiv = aktiv;
    }

    /// <summary>Grundriss übernehmen (prüft Raster, Überlappungen); die Platzzahl folgt aus den Tischen.</summary>
    public void GrundrissSetzen(Raumplan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Grundrissbeanstandung(plan) is { } fehler)
        {
            throw new FachlicherFehler(fehler);
        }

        RasterSpalten = plan.Spalten;
        RasterZeilen = plan.Zeilen;
        Startecke = plan.Startecke;
        Richtung = plan.Richtung;
        Schlange = plan.Schlange;
        elemente.Clear();
        elemente.AddRange(plan.Elemente.Select(e => new Raumelement(e.Art, e.X, e.Y, e.Breite, e.Hoehe)));
        var tische = elemente.Count(e => e.Art == Raumelementart.Tisch);
        if (tische > 0)
        {
            Plaetze = tische;
        }
    }

    public static string? Grundrissbeanstandung(Raumplan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Spalten is < 4 or > MaxRaster || plan.Zeilen is < 4 or > MaxRaster)
        {
            return $"Das Raster hat 4 bis {MaxRaster} Spalten und Zeilen.";
        }

        var liste = plan.Elemente.Select(e => new Raumelement(e.Art, e.X, e.Y, e.Breite, e.Hoehe)).ToList();
        if (liste.FirstOrDefault(e => e.Breite < 1 || e.Hoehe < 1 || e.X < 0 || e.Y < 0 || e.X + e.Breite > plan.Spalten || e.Y + e.Hoehe > plan.Zeilen) is { } aussen)
        {
            return $"{aussen.Art.Anzeigename()} bei Spalte {aussen.X + 1}, Zeile {aussen.Y + 1} liegt außerhalb des Rasters.";
        }

        // Tische und Pulte dürfen sich mit nichts überlappen; Wandelemente (Tür, Fenster, Tafel, Projektion) untereinander schon.
        var feste = liste.Where(e => e.Art is Raumelementart.Tisch or Raumelementart.Pult).ToList();
        for (var i = 0; i < feste.Count; i++)
        {
            if (liste.FirstOrDefault(o => !ReferenceEquals(o, feste[i]) && feste[i].Ueberlappt(o) && (o.Art is Raumelementart.Tisch or Raumelementart.Pult)) is { } o)
            {
                return $"Bei Spalte {o.X + 1}, Zeile {o.Y + 1} liegen zwei Tische bzw. Pulte übereinander.";
            }
        }

        return null;
    }

    /// <summary>Sitzplätze in Nummernfolge. Ohne Grundriss 1 … <see cref="Plaetze"/>.</summary>
    public IReadOnlyList<Sitzplatz> Sitzplaetze() => HatGrundriss
        ? Nummerieren(elemente.Where(e => e.Art == Raumelementart.Tisch).Select(e => (e.X, e.Y)).ToList(), Startecke, Richtung, Schlange)
        : Enumerable.Range(1, Plaetze).Select(n => new Sitzplatz(n, 0, 0, -1, -1)).ToList();

    /// <summary>
    /// Nummeriert Tische nach Muster: Reihen = gleiche Zeile im Raster (bei „Spalten“ gleiche Spalte), beginnend an der Startecke;
    /// mit Schlangenlinie kehrt sich die Laufrichtung in jeder zweiten Reihe um (kurze Wege beim Austeilen).
    /// </summary>
    public static IReadOnlyList<Sitzplatz> Nummerieren(IReadOnlyList<(int X, int Y)> tische, Startecke ecke, Laufrichtung richtung, bool schlange)
    {
        ArgumentNullException.ThrowIfNull(tische);
        var vonOben = ecke is Startecke.ObenLinks or Startecke.ObenRechts;
        var vonLinks = ecke is Startecke.ObenLinks or Startecke.UntenLinks;

        // Reihenschlüssel und Position innerhalb der Reihe
        var gruppen = tische
            .GroupBy(t => richtung == Laufrichtung.Reihen ? t.Y : t.X)
            .OrderBy(g => richtung == Laufrichtung.Reihen ? (vonOben ? g.Key : -g.Key) : (vonLinks ? g.Key : -g.Key))
            .ToList();
        var ergebnis = new List<Sitzplatz>(tische.Count);
        var nummer = 1;
        for (var r = 0; r < gruppen.Count; r++)
        {
            var vorwaerts = richtung == Laufrichtung.Reihen ? vonLinks : vonOben;
            if (schlange && r % 2 == 1)
            {
                vorwaerts = !vorwaerts;
            }

            var reihe = gruppen[r].OrderBy(t => richtung == Laufrichtung.Reihen ? (vorwaerts ? t.X : -t.X) : (vorwaerts ? t.Y : -t.Y)).ToList();
            for (var s = 0; s < reihe.Count; s++)
            {
                ergebnis.Add(new Sitzplatz(nummer++, r + 1, s + 1, reihe[s].X, reihe[s].Y));
            }
        }

        return ergebnis;
    }

    /// <summary>Positionen einer Tischmatrix: <paramref name="reihen"/> × <paramref name="jeReihe"/>, mit Abstand in Zellen zwischen den Tischen.</summary>
    public static IEnumerable<(int X, int Y)> Tischmatrix(int startX, int startY, int reihen, int jeReihe, int abstandX, int abstandY)
    {
        for (var r = 0; r < reihen; r++)
        {
            for (var t = 0; t < jeReihe; t++)
            {
                yield return (startX + (t * (abstandX + 1)), startY + (r * (abstandY + 1)));
            }
        }
    }

    /// <summary>
    /// Wählt <paramref name="anzahl"/> von <paramref name="plaetze"/> Sitzplätzen gleichmäßig gestreut aus (größtmögliche Lücken,
    /// in Nummernfolge). Bei vollem Raum alle.
    /// </summary>
    public static IReadOnlyList<Sitzplatz> Streuen(IReadOnlyList<Sitzplatz> plaetze, int anzahl)
    {
        ArgumentNullException.ThrowIfNull(plaetze);
        if (anzahl > plaetze.Count)
        {
            throw new FachlicherFehler($"Der Raum hat nur {plaetze.Count} Plätze für {anzahl} Personen.");
        }

        if (anzahl <= 0)
        {
            return [];
        }

        // Mittelpunkte gleich großer Abschnitte → erster und letzter Platz bleiben frei, wenn Luft ist
        return Enumerable.Range(0, anzahl).Select(i => plaetze[(int)Math.Floor((i + 0.5) * plaetze.Count / anzahl)]).ToList();
    }
}
