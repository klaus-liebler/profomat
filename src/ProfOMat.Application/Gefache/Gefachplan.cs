using System.Globalization;
using ProfOMat.Domain.Gefache;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Application.Gefache;

/// <summary>
/// Punkt einer geschlossenen Kontur in mm (x nach rechts, y nach oben). <see cref="Bulge"/> beschreibt wie in DXF den Bogen zum nächsten
/// Punkt: tan(Öffnungswinkel/4), positiv = gegen den Uhrzeigersinn, 0 = Gerade.
/// </summary>
public readonly record struct Ecke(double X, double Y, double Bulge = 0);

public enum Wandart
{
    /// <summary>Läuft entlang der Kistenlänge; Schlitze von oben.</summary>
    Laengswand,

    /// <summary>Läuft entlang der Kistenbreite; Schlitze von unten.</summary>
    Querwand,
}

/// <summary>Gravierter Text auf einer Wand (Grundlinie links bei X/Y, Höhe in mm).</summary>
public sealed record Gravurtext(string Text, double X, double Y, double Hoehe);

/// <param name="Lage">Mitte der Wand in der Kiste: Längswand ab der vorderen, Querwand ab der linken Innenwand (oben gemessen).</param>
/// <param name="Laenge">Länge oben (= Breite des umschließenden Rechtecks).</param>
/// <param name="Kontur">Umriss in Wandkoordinaten (x = 0 am linken bzw. vorderen Ende oben, y = 0 am Boden).</param>
public sealed record Wand(Wandart Art, int Nr, double Lage, double Laenge, double Hoehe, IReadOnlyList<Ecke> Kontur, Gravurtext? Gravur)
{
    public string Name => (Art == Wandart.Laengswand ? "L" : "Q") + Nr.ToString(CultureInfo.InvariantCulture);

    public string Bezeichnung => (Art == Wandart.Laengswand ? "Längswand " : "Querwand ") + Nr.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Ein Teil auf einer Platte: linke obere Ecke des umschließenden Rechtecks (Plattenkoordinaten, y nach unten).</summary>
/// <param name="Gedreht">Um 90° gedreht (die Oberkante der Wand zeigt dann nach rechts).</param>
public sealed record Platzierung(Wand Wand, double X, double Y, bool Gedreht)
{
    public double Breite => Gedreht ? Wand.Hoehe : Wand.Laenge;

    public double Hoehe => Gedreht ? Wand.Laenge : Wand.Hoehe;

    /// <summary>Wandpunkt → Plattenpunkt (y nach unten). Beide Lagen sind Drehungen, nie Spiegelungen (Gravur bleibt lesbar).</summary>
    public (double X, double Y) Abbilden(double x, double y) => Gedreht ? (X + y, Y + x) : (X + x, Y + Wand.Hoehe - y);
}

public sealed record Platte(int Nr, IReadOnlyList<Platzierung> Teile);

/// <summary>Ergebnis der Planung: Fächer, Wände, Platten – oder die Begründung, warum es so nicht geht.</summary>
public sealed record Gefachplan(
    Gefachangaben Angaben,
    IReadOnlyList<double> FaecherLaenge,
    IReadOnlyList<double> FaecherBreite,
    IReadOnlyList<Wand> Waende,
    IReadOnlyList<Platte> Platten,
    Freigabe Freigabe)
{
    /// <summary>Kleinstes zulässiges Fach (mm).</summary>
    public const double MinFach = 10;

    private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

    public IEnumerable<Wand> Laengswaende => Waende.Where(w => w.Art == Wandart.Laengswand);

    public IEnumerable<Wand> Querwaende => Waende.Where(w => w.Art == Wandart.Querwand);

    public static Gefachplan Berechnen(Gefachangaben angaben)
    {
        ArgumentNullException.ThrowIfNull(angaben);
        var a = angaben;
        var t = (double)a.DickeMm;
        try
        {
            var fl = Faecher(a.Laenge, (double)a.InnenLaengeMm, t, "entlang der Länge");
            var fb = Faecher(a.Breite, (double)a.InnenBreiteMm, t, "entlang der Breite");
            var waende = WaendeBauen(a, Mitten(fl, t), Mitten(fb, t));
            if (waende.Count == 0)
            {
                return new(a, fl, fb, [], [], Freigabe.Nein("Mit je einem Fach in beiden Richtungen gibt es nichts zu schneiden."));
            }

            var platten = Belegen(waende, (double)a.PlatteLaengeMm, (double)a.PlatteBreiteMm, (double)a.AbstandMm);
            return new(a, fl, fb, waende, platten, Freigabe.Ja);
        }
        catch (FachlicherFehler ex)
        {
            return new(a, [], [], [], [], Freigabe.Nein(ex.Message));
        }
    }

    /// <summary>Fachgrößen (lichte Weite) entlang einer Innenseite; zwischen zwei Fächern steht eine Wand der Dicke <paramref name="dicke"/>.</summary>
    public static IReadOnlyList<double> Faecher(Teilung teilung, double innen, double dicke, string richtung)
    {
        ArgumentNullException.ThrowIfNull(teilung);
        List<double?> angabe = teilung.Art == Teilungsart.Anzahl
            ? [.. Enumerable.Repeat<double?>(null, Math.Max(1, teilung.Anzahl))]
            : [.. Teilung.MasseZerlegen(teilung.Masse).Select(m => (double?)m)];
        if (angabe.Count == 0)
        {
            throw new FachlicherFehler($"Fächer {richtung}: bitte Fachgrößen angeben.");
        }

        var rest = innen - (angabe.Count - 1) * dicke - angabe.Sum(m => m ?? 0);
        if (!angabe.Contains(null))
        {
            if (rest < -0.5)
            {
                throw new FachlicherFehler($"Fächer {richtung}: Fächer und Wände ergeben {Mm(innen - rest)} mm, innen sind nur {Mm(innen)} mm Platz.");
            }

            if (rest <= 0.5)
            {
                // passt (bis auf Rundung): das letzte Fach gleicht aus
                angabe[^1] += rest;
                return Pruefen([.. angabe.Select(m => m!.Value)], richtung);
            }

            angabe.Add(null);   // ohne „*“ bekommt der Rest ein eigenes Fach am Ende
            rest -= dicke;
        }

        var sterne = angabe.Count(m => m is null);
        if (rest / sterne < MinFach)
        {
            throw new FachlicherFehler(rest < 0
                ? $"Fächer {richtung}: Fächer und Wände ergeben mehr als die {Mm(innen)} mm innen."
                : $"Fächer {richtung}: für {(sterne == 1 ? "das Restfach" : "die Restfächer")} bleiben nur {Mm(rest / sterne)} mm – mindestens {Mm(MinFach)} mm.");
        }

        return Pruefen([.. angabe.Select(m => m ?? rest / sterne)], richtung);
    }

    private static List<double> Pruefen(List<double> faecher, string richtung)
    {
        var klein = faecher.FindIndex(f => f < MinFach - 1e-9);
        return klein >= 0
            ? throw new FachlicherFehler($"Fächer {richtung}: Fach {klein + 1} wäre nur {Mm(faecher[klein])} mm breit – mindestens {Mm(MinFach)} mm.")
            : faecher;
    }

    /// <summary>Mitten der Trennwände ab der Innenwand.</summary>
    public static IReadOnlyList<double> Mitten(IReadOnlyList<double> faecher, double dicke)
    {
        ArgumentNullException.ThrowIfNull(faecher);
        var mitten = new List<double>();
        var x = 0.0;
        for (var i = 0; i < faecher.Count - 1; i++)
        {
            x += faecher[i];
            mitten.Add(x + dicke / 2);
            x += dicke;
        }

        return mitten;
    }

    private static List<Wand> WaendeBauen(Gefachangaben a, IReadOnlyList<double> querMitten, IReadOnlyList<double> laengsMitten)
    {
        var s = (double)a.SpielMm;
        var h = (double)a.HoeheMm;
        var r = (double)a.RadiusMm;
        var schlitz = (double)(a.DickeMm + a.SchlitzzugabeMm);
        var gravur = Math.Clamp(h / 8, 3, 7);
        var waende = new List<Wand>();

        // Längswände: Schlitze von oben dort, wo die Querwände kreuzen
        var (konturL, gravurL) = Kontur((double)a.InnenLaengeMm - 2 * s, (double)a.BodenLaengeMm - 2 * s, h, r,
            [.. querMitten.Select(x => x - s)], schlitz, vonOben: true, "Längswand", "Querwand");
        for (var i = 0; i < laengsMitten.Count; i++)
        {
            waende.Add(new(Wandart.Laengswand, i + 1, laengsMitten[i], (double)a.InnenLaengeMm - 2 * s, h, konturL,
                a.Gravur ? new($"L{i + 1} links", gravurL.X, gravurL.Y - gravur / 2, gravur) : null));
        }

        // Querwände: Schlitze von unten dort, wo die Längswände kreuzen
        var (konturQ, gravurQ) = Kontur((double)a.InnenBreiteMm - 2 * s, (double)a.BodenBreiteMm - 2 * s, h, r,
            [.. laengsMitten.Select(z => z - s)], schlitz, vonOben: false, "Querwand", "Längswand");
        for (var i = 0; i < querMitten.Count; i++)
        {
            waende.Add(new(Wandart.Querwand, i + 1, querMitten[i], (double)a.InnenBreiteMm - 2 * s, h, konturQ,
                a.Gravur ? new($"Q{i + 1} vorn", gravurQ.X, gravurQ.Y - gravur / 2, gravur) : null));
        }

        return waende;
    }

    /// <summary>
    /// Umriss einer Wand: Trapez (oben <paramref name="oben"/>, am Boden <paramref name="unten"/> lang, mittig), untere Ecken mit
    /// Radius <paramref name="radius"/> abgerundet (Rundung zwischen Kistenboden und -wand), Schlitze halb so tief wie die Wand.
    /// Gegen den Uhrzeigersinn, Beginn am Boden links. Liefert außerdem einen freien Platz für die Gravur (links, in der ungeschlitzten Hälfte).
    /// </summary>
    internal static (IReadOnlyList<Ecke> Kontur, (double X, double Y) Gravur) Kontur(double oben, double unten, double hoehe, double radius,
        IReadOnlyList<double> schlitze, double schlitzbreite, bool vonOben, string wand, string andere)
    {
        if (unten <= 0 || oben <= 0)
        {
            throw new FachlicherFehler($"Die {wand} hätte keine Länge – Innenmaße und Spiel prüfen.");
        }

        var d = (oben - unten) / 2;          // Versatz der unteren Ecken (schräge Kistenwände)
        var seite = Math.Sqrt(d * d + hoehe * hoehe);
        var (ux, uy) = (-d / seite, hoehe / seite);   // linke Seite von unten nach oben
        var theta = Math.Acos(Math.Clamp(ux, -1, 1));     // Innenwinkel zwischen Boden (1,0) und linker Seite
        var a = radius > 0 ? radius / Math.Tan(theta / 2) : 0;   // Tangentenlänge
        if (2 * a > unten - 1 || a > seite / 2)
        {
            throw new FachlicherFehler($"Der Radius am Boden ({Mm(radius)} mm) ist für die {wand} zu groß.");
        }

        var bulge = radius > 0 ? Math.Tan((Math.PI - theta) / 4) : 0;
        var tiefe = hoehe / 2;
        var punkte = new List<Ecke>();
        var sortiert = schlitze.Order().ToList();
        foreach (var m in sortiert)
        {
            var (l, rr) = (m - schlitzbreite / 2, m + schlitzbreite / 2);
            // unten verläuft die Kante zwischen den Rundungen, oben zwischen den (schrägen) Seiten auf Schlitztiefe
            var (min, max) = vonOben ? (d * (1 - (hoehe - tiefe) / hoehe), oben - d * (1 - (hoehe - tiefe) / hoehe)) : (d + a, d + unten - a);
            if (l < min + 1 || rr > max - 1)
            {
                throw new FachlicherFehler(vonOben
                    ? $"Eine {andere} steht zu nah an der Kistenwand – das äußere Fach vergrößern."
                    : $"Eine {andere} träfe unten die Rundung am Boden (Radius {Mm(radius)} mm) – das äußere Fach vergrößern oder den Radius prüfen.");
            }
        }

        // Boden von links nach rechts (Schlitze von unten)
        punkte.Add(new(d + a, 0));
        if (!vonOben)
        {
            foreach (var m in sortiert)
            {
                var (l, rr) = (m - schlitzbreite / 2, m + schlitzbreite / 2);
                punkte.Add(new(l, 0));
                punkte.Add(new(l, tiefe));
                punkte.Add(new(rr, tiefe));
                punkte.Add(new(rr, 0));
            }
        }

        // rechte untere Rundung, rechte Seite hinauf
        if (a > 0)
        {
            punkte.Add(new(d + unten - a, 0, bulge));
            punkte.Add(new(d + unten + d / seite * a, uy * a));
        }
        else
        {
            punkte.Add(new(d + unten, 0));
        }

        punkte.Add(new(oben, hoehe));

        // Oberkante von rechts nach links (Schlitze von oben)
        if (vonOben)
        {
            foreach (var m in Enumerable.Reverse(sortiert))
            {
                var (l, rr) = (m - schlitzbreite / 2, m + schlitzbreite / 2);
                punkte.Add(new(rr, hoehe));
                punkte.Add(new(rr, hoehe - tiefe));
                punkte.Add(new(l, hoehe - tiefe));
                punkte.Add(new(l, hoehe));
            }
        }

        punkte.Add(new(0, hoehe));

        // linke Seite hinab, linke untere Rundung zurück zum Anfang
        if (a > 0)
        {
            punkte.Add(new(d + ux * a, uy * a, bulge));
        }
        else
        {
            punkte[0] = new(d, 0);
        }

        // Gravur in der ungeschlitzten Hälfte, etwas rechts der linken Kante
        var gy = vonOben ? hoehe / 4 : hoehe * 3 / 4;
        var gx = Math.Max(d * (1 - gy / hoehe), d + a) + 4;
        return (Entdoppeln(punkte), (gx, gy));
    }

    private static List<Ecke> Entdoppeln(List<Ecke> punkte)
    {
        var ergebnis = new List<Ecke>();
        foreach (var p in punkte)
        {
            if (ergebnis.Count > 0 && Math.Abs(ergebnis[^1].X - p.X) < 1e-9 && Math.Abs(ergebnis[^1].Y - p.Y) < 1e-9)
            {
                ergebnis[^1] = p with { Bulge = ergebnis[^1].Bulge != 0 ? ergebnis[^1].Bulge : p.Bulge };
                continue;
            }

            ergebnis.Add(p);
        }

        return ergebnis;
    }

    /// <summary>
    /// Verteilt die Wände auf Platten. Alle Wände sind gleich hoch: Jede Platte wird in Bahnen der Wandhöhe geteilt (quer oder längs),
    /// die Bahnen werden nach „größte zuerst“ gefüllt. Je Platte gewinnt die Richtung, in der mehr Wandlänge Platz findet.
    /// </summary>
    public static IReadOnlyList<Platte> Belegen(IReadOnlyList<Wand> waende, double plattenLaenge, double plattenBreite, double abstand)
    {
        ArgumentNullException.ThrowIfNull(waende);
        var offen = waende.OrderByDescending(w => w.Laenge).ThenBy(w => w.Art).ThenBy(w => w.Nr).ToList();
        var platten = new List<Platte>();
        foreach (var w in offen)
        {
            if (Bahnen(w, plattenLaenge, plattenBreite, abstand, gedreht: false) == 0 && Bahnen(w, plattenLaenge, plattenBreite, abstand, gedreht: true) == 0)
            {
                throw new FachlicherFehler($"Die {w.Bezeichnung} ({Mm(w.Laenge)} × {Mm(w.Hoehe)} mm) passt nicht auf die Platte "
                                           + $"({Mm(plattenLaenge)} × {Mm(plattenBreite)} mm, {Mm(abstand)} mm Rand).");
            }
        }

        while (offen.Count > 0)
        {
            var quer = Fuellen(offen, plattenLaenge, plattenBreite, abstand, gedreht: false);
            var laengs = Fuellen(offen, plattenLaenge, plattenBreite, abstand, gedreht: true);
            var wahl = laengs.Sum(p => p.Wand.Laenge) > quer.Sum(p => p.Wand.Laenge) + 1e-9 ? laengs : quer;
            platten.Add(new(platten.Count + 1, wahl));
            foreach (var p in wahl)
            {
                offen.Remove(p.Wand);
            }
        }

        return platten;
    }

    private static int Bahnen(Wand w, double pl, double pb, double abstand, bool gedreht)
    {
        var (bahnlaenge, quer) = gedreht ? (pb - 2 * abstand, pl - 2 * abstand) : (pl - 2 * abstand, pb - 2 * abstand);
        return w.Laenge > bahnlaenge + 1e-9 || w.Hoehe > quer + 1e-9 ? 0 : (int)Math.Floor((quer + abstand + 1e-9) / (w.Hoehe + abstand));
    }

    private static List<Platzierung> Fuellen(List<Wand> offen, double pl, double pb, double abstand, bool gedreht)
    {
        var ergebnis = new List<Platzierung>();
        var bahnlaenge = gedreht ? pb - 2 * abstand : pl - 2 * abstand;
        var belegt = new List<double>();   // je Bahn: bereits belegte Länge (inkl. Abstände)
        var hoehe = offen.Count > 0 ? offen[0].Hoehe : 0;   // alle Wände sind gleich hoch
        var quer = (gedreht ? pl : pb) - 2 * abstand;
        var bahnen = hoehe > quer + 1e-9 ? 0 : (int)Math.Floor((quer + abstand + 1e-9) / (hoehe + abstand));
        for (var i = 0; i < bahnen; i++)
        {
            belegt.Add(0);
        }

        foreach (var w in offen)
        {
            if (w.Laenge > bahnlaenge + 1e-9)
            {
                continue;
            }

            for (var i = 0; i < belegt.Count; i++)
            {
                var start = belegt[i] == 0 ? 0 : belegt[i] + abstand;
                if (start + w.Laenge <= bahnlaenge + 1e-9)
                {
                    var q = abstand + i * (w.Hoehe + abstand);
                    var l = abstand + start;
                    ergebnis.Add(gedreht ? new(w, q, l, true) : new(w, l, q, false));
                    belegt[i] = start + w.Laenge;
                    break;
                }
            }
        }

        return ergebnis;
    }

    internal static string Mm(double d) => d.ToString("0.#", Deutsch);
}
