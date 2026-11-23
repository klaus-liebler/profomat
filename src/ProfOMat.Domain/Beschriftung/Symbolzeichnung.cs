namespace ProfOMat.Domain.Beschriftung;

/// <summary>Bestandteil einer Symbolzeichnung im Raster 0…10 (x nach rechts, y nach unten).</summary>
public abstract record Strich;

/// <summary>Offener Linienzug (Punkte x0,y0,x1,y1,…).</summary>
public sealed record Linienzug(double[] Punkte) : Strich;

/// <summary>Geschlossene Fläche; <paramref name="Gefuellt"/> = mit Strichfarbe gefüllt.</summary>
public sealed record Vieleck(double[] Punkte, bool Gefuellt) : Strich;

public sealed record Kreis(double X, double Y, double R, bool Gefuellt) : Strich;

public sealed record Rechteck(double X, double Y, double B, double H) : Strich;

/// <summary>Buchstabe(n) in der Mitte, z. B. „M“ für Motor (Höhe im Raster).</summary>
public sealed record Zeichen(string Text, double X, double Y, double Hoehe) : Strich;

/// <summary>
/// Einfache, gut druckbare Symbole für Verteilerbeschriftungen. Strichstärke im Raster: <see cref="Strichstaerke"/>.
/// Die Zeichnungen sind die einzige Quelle für Editor (SVG) und PDF.
/// </summary>
public static class Symbolzeichnung
{
    public const double Strichstaerke = 0.8;

    public static string Name(Symbol s) => s switch
    {
        Symbol.Keins => "kein Symbol",
        Symbol.Steckdose => "Steckdose",
        Symbol.Licht => "Licht",
        Symbol.Herd => "Herd",
        Symbol.Backofen => "Backofen",
        Symbol.Waschmaschine => "Waschmaschine",
        Symbol.Trockner => "Trockner",
        Symbol.Spuelmaschine => "Spülmaschine",
        Symbol.Kuehlen => "Kühlen",
        Symbol.Heizung => "Heizung",
        Symbol.Warmwasser => "Warmwasser",
        Symbol.Lueftung => "Lüftung",
        Symbol.Pumpe => "Pumpe",
        Symbol.Motor => "Motor",
        Symbol.Rollladen => "Rollladen",
        Symbol.Klingel => "Klingel",
        Symbol.Netzwerk => "Netzwerk",
        Symbol.Computer => "Computer",
        Symbol.Garten => "Garten/Außen",
        Symbol.Haus => "Haus",
        Symbol.Solar => "Solar/PV",
        Symbol.Elektroauto => "E-Auto",
        Symbol.Blitz => "Überspannung",
        _ => s.ToString(),
    };

    public static IReadOnlyList<Strich> Von(Symbol s) => s switch
    {
        Symbol.Steckdose =>
        [
            new Kreis(5, 5, 4, false), new Kreis(3.4, 5, 0.7, true), new Kreis(6.6, 5, 0.7, true),
            new Linienzug([5, 1, 5, 2.3]), new Linienzug([5, 7.7, 5, 9]),
        ],
        Symbol.Licht => [new Kreis(5, 5, 3.8, false), new Linienzug([2.3, 2.3, 7.7, 7.7]), new Linienzug([7.7, 2.3, 2.3, 7.7])],
        Symbol.Herd =>
        [
            new Rechteck(1, 1, 8, 8), new Kreis(3.4, 3.4, 1.3, false), new Kreis(6.6, 3.4, 1.3, false),
            new Kreis(3.4, 6.6, 1.3, false), new Kreis(6.6, 6.6, 1.3, false),
        ],
        Symbol.Backofen => [new Rechteck(1, 1, 8, 8), new Linienzug([1, 3, 9, 3]), new Rechteck(2.5, 4.5, 5, 3), new Kreis(3, 2, 0.4, true), new Kreis(5, 2, 0.4, true)],
        Symbol.Waschmaschine => [new Rechteck(1, 1, 8, 8), new Kreis(5, 5.7, 2.6, false), new Linienzug([1, 2.6, 9, 2.6])],
        Symbol.Trockner =>
        [
            new Rechteck(1, 1, 8, 8), new Kreis(5, 5.7, 2.6, false), new Linienzug([1, 2.6, 9, 2.6]),
            new Linienzug([3.6, 5.9, 4.3, 5.2, 5, 5.9, 5.7, 5.2, 6.4, 5.9]),
        ],
        Symbol.Spuelmaschine =>
        [
            new Rechteck(1, 1, 8, 8), new Linienzug([1, 2.8, 9, 2.8]),
            new Linienzug([3, 4.2, 3, 5.4]), new Linienzug([5, 4.2, 5, 5.4]), new Linienzug([7, 4.2, 7, 5.4]),
            new Linienzug([3, 6.4, 3, 7.6]), new Linienzug([5, 6.4, 5, 7.6]), new Linienzug([7, 6.4, 7, 7.6]),
        ],
        Symbol.Kuehlen =>
        [
            new Linienzug([5, 0.8, 5, 9.2]), new Linienzug([1.4, 2.9, 8.6, 7.1]), new Linienzug([1.4, 7.1, 8.6, 2.9]),
            new Linienzug([3.8, 1.6, 5, 2.8, 6.2, 1.6]), new Linienzug([3.8, 8.4, 5, 7.2, 6.2, 8.4]),
        ],
        Symbol.Heizung =>
        [
            new Rechteck(1, 2, 8, 6.5), new Linienzug([3.7, 2, 3.7, 8.5]), new Linienzug([6.3, 2, 6.3, 8.5]),
            new Linienzug([2, 8.5, 2, 9.5]), new Linienzug([8, 8.5, 8, 9.5]),
        ],
        Symbol.Warmwasser => [new Vieleck([5, 0.8, 7.7, 5, 8, 6.6, 7.2, 8.3, 5, 9.2, 2.8, 8.3, 2, 6.6, 2.3, 5], false), new Linienzug([4, 6, 4, 7.6, 5.4, 7.6])],
        Symbol.Lueftung =>
        [
            new Kreis(5, 5, 4.2, false), new Kreis(5, 5, 0.7, true),
            new Vieleck([5, 5, 4.2, 1.6, 6.2, 2, 5.6, 4.4], true), new Vieleck([5, 5, 8.3, 5.9, 7.4, 7.6, 5.2, 5.9], true),
            new Vieleck([5, 5, 2.4, 7.8, 1.6, 5.9, 4.1, 5.3], true),
        ],
        Symbol.Pumpe => [new Kreis(5, 5, 4, false), new Vieleck([3.4, 2.4, 3.4, 7.6, 8, 5], true)],
        Symbol.Motor => [new Kreis(5, 5, 4, false), new Zeichen("M", 5, 5, 4.4)],
        Symbol.Rollladen =>
        [
            new Rechteck(1, 1, 8, 8), new Linienzug([1, 3, 9, 3]), new Linienzug([1, 5, 9, 5]), new Linienzug([1, 7, 9, 7]),
            new Vieleck([8.4, 4.2, 9.8, 4.2, 9.1, 5.4], true),
        ],
        Symbol.Klingel => [new Vieleck([5, 1.2, 6.9, 2.6, 7.3, 6.4, 8.6, 7.6, 1.4, 7.6, 2.7, 6.4, 3.1, 2.6], false), new Kreis(5, 8.7, 0.9, true)],
        Symbol.Netzwerk =>
        [
            new Rechteck(3.6, 1, 2.8, 2.2), new Rechteck(0.8, 6.8, 2.8, 2.2), new Rechteck(6.4, 6.8, 2.8, 2.2),
            new Linienzug([5, 3.2, 5, 5]), new Linienzug([2.2, 6.8, 2.2, 5, 7.8, 5, 7.8, 6.8]),
        ],
        Symbol.Computer => [new Rechteck(1, 1.4, 8, 5.6), new Linienzug([5, 7, 5, 8.6]), new Linienzug([2.8, 8.6, 7.2, 8.6])],
        Symbol.Garten => [new Vieleck([5, 0.8, 8.4, 6.8, 1.6, 6.8], false), new Linienzug([5, 6.8, 5, 9.2])],
        Symbol.Haus => [new Vieleck([1, 5, 5, 1, 9, 5, 7.8, 5, 7.8, 9, 2.2, 9, 2.2, 5], false), new Rechteck(4.1, 6, 1.8, 3)],
        Symbol.Solar =>
        [
            new Kreis(5, 5, 2, false),
            new Linienzug([5, 0.8, 5, 2.2]), new Linienzug([5, 7.8, 5, 9.2]), new Linienzug([0.8, 5, 2.2, 5]), new Linienzug([7.8, 5, 9.2, 5]),
            new Linienzug([2, 2, 3, 3]), new Linienzug([7, 7, 8, 8]), new Linienzug([8, 2, 7, 3]), new Linienzug([3, 7, 2, 8]),
        ],
        Symbol.Elektroauto =>
        [
            new Vieleck([0.8, 7, 0.8, 5, 2.4, 4.6, 3.6, 2.6, 6.6, 2.6, 7.8, 4.6, 9.2, 5, 9.2, 7], false),
            new Kreis(2.9, 7.3, 1, true), new Kreis(7.1, 7.3, 1, true), new Vieleck([5.4, 3.1, 4, 5.1, 5, 5.1, 4.5, 6.6, 6, 4.4, 5, 4.4], true),
        ],
        Symbol.Blitz => [new Vieleck([6.2, 0.6, 2.4, 5.6, 4.9, 5.6, 3.6, 9.4, 7.8, 4.1, 5.3, 4.1], true)],
        _ => [],
    };
}
