using ProfOMat.Domain.Beschriftung;

namespace ProfOMat.Application.Beschriftung;

/// <summary>Misst Text in der Druckschrift (Liberation Sans = maßgleich Arial), Ergebnis in mm.</summary>
public interface ITextmass
{
    double BreiteMm(string text, double groessePt, bool fett);
}

/// <summary>Zeichenelement eines Beschriftungsstreifens; Koordinaten in mm ab der linken oberen Streifenecke.</summary>
public abstract record Element;

public sealed record Flaeche(double X, double Y, double B, double H, string Farbe) : Element;

public sealed record Strecke(double X1, double Y1, double X2, double Y2, string Farbe, double Staerke) : Element;

/// <param name="X">Linker Rand bzw. Mitte (bei <paramref name="Mittig"/>).</param>
/// <param name="Y">Grundlinie.</param>
public sealed record Schrift(double X, double Y, string Text, double GroessePt, bool Fett, string Farbe, bool Mittig) : Element;

/// <param name="X">Linke obere Ecke des quadratischen Symbolfelds.</param>
public sealed record Bild(Symbol Symbol, double X, double Y, double Groesse, string Farbe) : Element;

/// <summary>Ein fertig gesetzter Streifen (eine Reihe bzw. ein Etikett).</summary>
public sealed record Streifen(double Breite, double Hoehe, IReadOnlyList<Element> Elemente);

/// <summary>
/// Setzt Etiketten: Kennzeichen klein oben links, Symbol links (breite Geräte) bzw. oben (schmale), Text und Zusatz so groß wie möglich,
/// bei Bedarf umbrochen („|“ erzwingt einen Umbruch) und bis 3,5 pt verkleinert. Editor und PDF nutzen dasselbe Ergebnis.
/// </summary>
public sealed class Streifenlayout(ITextmass mass)
{
    public const string Trennfarbe = "#9A9A9A";
    public const double PtZuMm = 25.4 / 72.0;
    private const double Zeilenhoehe = 1.12;
    private const double Rand = 0.7;
    private const double MinPt = 3.5;
    private const double MaxPt = 11;

    /// <summary>Streifen einer Reihe: alle Geräte an ihrer Position, Trennlinien an den Gerätegrenzen.</summary>
    public Streifen Reihe(Schaltschrank schrank, Feld feld, int reihe)
    {
        ArgumentNullException.ThrowIfNull(schrank);
        ArgumentNullException.ThrowIfNull(feld);
        var te = (double)schrank.TeBreiteMm;
        var h = (double)schrank.HoeheMm;
        var elemente = new List<Element>();
        foreach (var g in feld.InReihe(reihe))
        {
            elemente.AddRange(Etikett(g, g.Start * te, g.Breite * te, h));
        }

        foreach (var x in feld.InReihe(reihe).SelectMany(g => new[] { g.Start, g.Ende }).Distinct().Where(t => t > 0 && t < schrank.TeProReihe))
        {
            elemente.Add(new Strecke(x * te, 0, x * te, h, Trennfarbe, 0.15));
        }

        return new Streifen(schrank.TeProReihe * te, h, elemente);
    }

    /// <summary>Einzelnes Etikett (Vorschau im Editor).</summary>
    public Streifen Einzeln(Schaltschrank schrank, Geraet g)
    {
        ArgumentNullException.ThrowIfNull(schrank);
        ArgumentNullException.ThrowIfNull(g);
        var b = g.Breite * (double)schrank.TeBreiteMm;
        return new Streifen(b, (double)schrank.HoeheMm, Etikett(g, 0, b, (double)schrank.HoeheMm));
    }

    public IReadOnlyList<Element> Etikett(Geraet g, double x, double b, double h)
    {
        ArgumentNullException.ThrowIfNull(g);
        var e = new List<Element>();
        if (!string.Equals(g.Hintergrund, "#FFFFFF", StringComparison.OrdinalIgnoreCase))
        {
            e.Add(new Flaeche(x, 0, b, h, g.Hintergrund));
        }

        var (ix, iy, ib, ih) = (x + Rand, Rand, b - (2 * Rand), h - (2 * Rand));
        if (g.Kennzeichen is { } kz)
        {
            var pt = Math.Clamp(h * 0.55, 4.5, 7);
            var mm = pt * PtZuMm;
            e.Add(new Schrift(ix, iy + (mm * 0.8), Passend(kz, ib, pt, true), pt, true, g.Schrift, false));
            iy += mm * 0.95;
            ih -= mm * 0.95;
        }

        var hatText = g.Text is not null || g.Zusatz is not null;
        if (g.Symbol != Symbol.Keins)
        {
            double s;
            if (!hatText)
            {
                s = Math.Min(ib, ih);
                e.Add(new Bild(g.Symbol, ix + ((ib - s) / 2), iy + ((ih - s) / 2), s, g.Schrift));
            }
            else if (ib >= ih * 2.2)
            {
                s = Math.Min(ih, ib * 0.35);
                e.Add(new Bild(g.Symbol, ix, iy + ((ih - s) / 2), s, g.Schrift));
                ix += s + 0.6;
                ib -= s + 0.6;
            }
            else
            {
                s = Math.Min(ih * 0.42, ib);
                e.Add(new Bild(g.Symbol, ix + ((ib - s) / 2), iy, s, g.Schrift));
                iy += s + 0.3;
                ih -= s + 0.3;
            }
        }

        if (hatText)
        {
            e.AddRange(Textblock(g.Text, g.Zusatz, ix, iy, ib, ih, g.Schrift));
        }

        return e;
    }

    private IEnumerable<Element> Textblock(string? text, string? zusatz, double x, double y, double b, double h, string farbe)
    {
        var start = Math.Min(MaxPt, h / Zeilenhoehe / PtZuMm);
        List<string> haupt = [], klein = [];
        var pt = MinPt;
        var passt = false;
        for (var p = Math.Max(start, MinPt); p >= MinPt; p -= 0.25)
        {
            if (Umbrechen(text, b, p, out haupt) && Umbrechen(zusatz, b, p * 0.8, out klein)
                && Hoehe(haupt.Count, klein.Count, p) <= h + 0.01)
            {
                (pt, passt) = (p, true);
                break;
            }
        }

        if (!passt)
        {
            // Passt auch klein nicht: harter Umbruch, überzählige Zeilen weglassen (mit „…“).
            haupt = Hart(text, b, pt);
            klein = Hart(zusatz, b, pt * 0.8);
            while (Hoehe(haupt.Count, klein.Count, pt) > h + 0.01 && haupt.Count + klein.Count > 1)
            {
                if (klein.Count > 0)
                {
                    klein.RemoveAt(klein.Count - 1);
                }
                else
                {
                    haupt.RemoveAt(haupt.Count - 1);
                    haupt[^1] = Passend(haupt[^1] + "…", b, pt, false);
                }
            }
        }

        var gesamt = Hoehe(haupt.Count, klein.Count, pt);
        var oben = y + Math.Max(0, (h - gesamt) / 2);
        var mitte = x + (b / 2);
        foreach (var zeile in haupt)
        {
            var mm = pt * PtZuMm;
            yield return new Schrift(mitte, oben + (mm * 0.86), zeile, pt, false, farbe, true);
            oben += mm * Zeilenhoehe;
        }

        foreach (var zeile in klein)
        {
            var mm = pt * 0.8 * PtZuMm;
            yield return new Schrift(mitte, oben + (mm * 0.86), zeile, pt * 0.8, false, farbe, true);
            oben += mm * Zeilenhoehe;
        }
    }

    private static double Hoehe(int haupt, int klein, double pt) => ((haupt * pt) + (klein * pt * 0.8)) * PtZuMm * Zeilenhoehe;

    /// <summary>Wortweise umbrechen; <c>false</c>, wenn ein Wort allein zu breit ist.</summary>
    private bool Umbrechen(string? text, double b, double pt, out List<string> zeilen)
    {
        zeilen = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (var absatz in text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var zeile = "";
            foreach (var wort in absatz.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (mass.BreiteMm(wort, pt, false) > b)
                {
                    return false;
                }

                var versuch = zeile.Length == 0 ? wort : $"{zeile} {wort}";
                if (mass.BreiteMm(versuch, pt, false) <= b)
                {
                    zeile = versuch;
                }
                else
                {
                    zeilen.Add(zeile);
                    zeile = wort;
                }
            }

            if (zeile.Length > 0)
            {
                zeilen.Add(zeile);
            }
        }

        return true;
    }

    /// <summary>Zeichenweise umbrechen (letzter Ausweg für sehr lange Wörter).</summary>
    private List<string> Hart(string? text, double b, double pt)
    {
        var zeilen = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return zeilen;
        }

        var zeile = "";
        foreach (var c in text.Replace('|', ' ').Trim())
        {
            if (zeile.Length > 0 && mass.BreiteMm(zeile + c, pt, false) > b)
            {
                zeilen.Add(zeile.TrimEnd());
                zeile = c == ' ' ? "" : c.ToString();
            }
            else
            {
                zeile += c;
            }
        }

        if (zeile.Trim().Length > 0)
        {
            zeilen.Add(zeile.Trim());
        }

        return zeilen;
    }

    /// <summary>Kürzt mit „…“, bis der Text in die Breite passt.</summary>
    private string Passend(string text, double b, double pt, bool fett)
    {
        if (mass.BreiteMm(text, pt, fett) <= b)
        {
            return text;
        }

        var t = text.TrimEnd('…');
        while (t.Length > 1 && mass.BreiteMm(t + "…", pt, fett) > b)
        {
            t = t[..^1];
        }

        return t + "…";
    }
}
