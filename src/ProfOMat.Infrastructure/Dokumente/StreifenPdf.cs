using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ProfOMat.Application.Beschriftung;
using ProfOMat.Domain.Beschriftung;

namespace ProfOMat.Infrastructure.Dokumente;

/// <summary>Misst Text mit PDFsharp in der eingebetteten Druckschrift (Liberation Sans, maßgleich Arial).</summary>
internal sealed class Textmass : ITextmass
{
    private readonly Lock sperre = new();
    private readonly Dictionary<(double, bool), XFont> schriften = [];
    private XGraphics? messung;

    static Textmass() => Schriften.Einrichten();

    public double BreiteMm(string text, double groessePt, bool fett)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        lock (sperre)
        {
            messung ??= XGraphics.CreateMeasureContext(new XSize(1000, 1000), XGraphicsUnit.Point, XPageDirection.Downwards);
            if (!schriften.TryGetValue((groessePt, fett), out var f))
            {
                f = new XFont("Arial", groessePt, fett ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                schriften[(groessePt, fett)] = f;
            }

            return messung.MeasureString(text, f).Width * Streifenlayout.PtZuMm;
        }
    }
}

/// <summary>
/// Beschriftungsstreifen auf DIN A4 quer: Streifen untereinander, Beschriftung darüber, Schnittmarken an allen Ecken (außerhalb des
/// Streifens, damit nach dem Schneiden nichts zu sehen ist) und ein 100-mm-Kontrollmaß für den Druck in Originalgröße.
/// </summary>
internal sealed class StreifenPdf : IStreifenPdf
{
    private const double Rand = 12;
    private const double Ueberschrift = 4;
    private const double Abstand = 7;
    private const double Markenabstand = 1.5;
    private const double Markenlaenge = 4;
    private const double Fuss = 12;

    static StreifenPdf() => Schriften.Einrichten();

    public byte[] Erzeugen(string titel, IReadOnlyList<(string Beschriftung, Streifen Streifen)> streifen)
    {
        ArgumentNullException.ThrowIfNull(streifen);
        using var dok = new PdfDocument();
        dok.Info.Title = titel;
        dok.Info.Author = "ProfOMat";
        var (seitenB, seitenH) = (297.0, 210.0);
        PdfPage? seite = null;
        XGraphics? gfx = null;
        var y = 0.0;
        var nr = 0;
        try
        {
            foreach (var (beschriftung, s) in streifen)
            {
                var block = Ueberschrift + s.Hoehe + Abstand;
                if (gfx is null || y + Ueberschrift + s.Hoehe > seitenH - Rand - Fuss)
                {
                    gfx?.Dispose();
                    seite = dok.AddPage();
                    seite.Width = XUnit.FromMillimeter(seitenB);
                    seite.Height = XUnit.FromMillimeter(seitenH);
                    gfx = XGraphics.FromPdfPage(seite);
                    Fusszeile(gfx, titel, ++nr, seitenH);
                    y = Rand + 2;
                }

                var x = (seitenB - s.Breite) / 2;
                Text(gfx, beschriftung, x + Markenabstand + 1, y + Ueberschrift - 1.3, 7, false, XColors.DimGray, XStringFormats.BaseLineLeft);
                Zeichnen(gfx, s, x, y + Ueberschrift);
                Schnittmarken(gfx, x, y + Ueberschrift, s.Breite, s.Hoehe);
                y += block;
            }
        }
        finally
        {
            gfx?.Dispose();
        }

        using var strom = new MemoryStream();
        dok.Save(strom, false);
        return strom.ToArray();
    }

    private static void Zeichnen(XGraphics g, Streifen s, double ox, double oy)
    {
        foreach (var e in s.Elemente)
        {
            switch (e)
            {
                case Flaeche f:
                    g.DrawRectangle(new XSolidBrush(Farbe(f.Farbe)), Mm(ox + f.X), Mm(oy + f.Y), Mm(f.B), Mm(f.H));
                    break;
                case Strecke l:
                    g.DrawLine(new XPen(Farbe(l.Farbe), Mm(l.Staerke)), Mm(ox + l.X1), Mm(oy + l.Y1), Mm(ox + l.X2), Mm(oy + l.Y2));
                    break;
                case Schrift t:
                    Text(g, t.Text, ox + t.X, oy + t.Y, t.GroessePt, t.Fett, Farbe(t.Farbe), t.Mittig ? XStringFormats.BaseLineCenter : XStringFormats.BaseLineLeft);
                    break;
                case Bild b:
                    SymbolZeichnen(g, b, ox, oy);
                    break;
            }
        }
    }

    private static void SymbolZeichnen(XGraphics g, Bild b, double ox, double oy)
    {
        var k = b.Groesse / 10.0;
        var farbe = Farbe(b.Farbe);
        var stift = new XPen(farbe, Mm(Symbolzeichnung.Strichstaerke * k)) { LineJoin = XLineJoin.Round, LineCap = XLineCap.Round };
        var pinsel = new XSolidBrush(farbe);
        XPoint P(double x, double y) => new(Mm(ox + b.X + (x * k)), Mm(oy + b.Y + (y * k)));
        XPoint[] Punkte(double[] p) => Enumerable.Range(0, p.Length / 2).Select(i => P(p[2 * i], p[(2 * i) + 1])).ToArray();
        foreach (var s in Symbolzeichnung.Von(b.Symbol))
        {
            switch (s)
            {
                case Linienzug l:
                    g.DrawLines(stift, Punkte(l.Punkte));
                    break;
                case Vieleck v when v.Gefuellt:
                    g.DrawPolygon(stift, pinsel, Punkte(v.Punkte), XFillMode.Winding);
                    break;
                case Vieleck v:
                    g.DrawPolygon(stift, Punkte(v.Punkte));
                    break;
                case Kreis c:
                    var a = P(c.X - c.R, c.Y - c.R);
                    if (c.Gefuellt)
                    {
                        g.DrawEllipse(pinsel, a.X, a.Y, Mm(2 * c.R * k), Mm(2 * c.R * k));
                    }
                    else
                    {
                        g.DrawEllipse(stift, a.X, a.Y, Mm(2 * c.R * k), Mm(2 * c.R * k));
                    }

                    break;
                case Rechteck r:
                    var o = P(r.X, r.Y);
                    g.DrawRectangle(stift, o.X, o.Y, Mm(r.B * k), Mm(r.H * k));
                    break;
                case Zeichen z:
                    var m = P(z.X, z.Y);
                    var pt = z.Hoehe * k / Streifenlayout.PtZuMm / 0.72;
                    g.DrawString(z.Text, new XFont("Arial", pt, XFontStyleEx.Bold), pinsel, m.X, m.Y + Mm(z.Hoehe * k * 0.5), XStringFormats.BaseLineCenter);
                    break;
            }
        }
    }

    private static void Schnittmarken(XGraphics g, double x, double y, double b, double h)
    {
        var stift = new XPen(XColors.Black, Mm(0.2));
        foreach (var ex in new[] { x, x + b })
        {
            g.DrawLine(stift, Mm(ex), Mm(y - Markenabstand - Markenlaenge), Mm(ex), Mm(y - Markenabstand));
            g.DrawLine(stift, Mm(ex), Mm(y + h + Markenabstand), Mm(ex), Mm(y + h + Markenabstand + Markenlaenge));
        }

        foreach (var ey in new[] { y, y + h })
        {
            g.DrawLine(stift, Mm(x - Markenabstand - Markenlaenge), Mm(ey), Mm(x - Markenabstand), Mm(ey));
            g.DrawLine(stift, Mm(x + b + Markenabstand), Mm(ey), Mm(x + b + Markenabstand + Markenlaenge), Mm(ey));
        }
    }

    private static void Fusszeile(XGraphics g, string titel, int seite, double seitenH)
    {
        var y = seitenH - Rand + 2;
        var stift = new XPen(XColors.Black, Mm(0.25));
        g.DrawLine(stift, Mm(Rand), Mm(y), Mm(Rand + 100), Mm(y));
        g.DrawLine(stift, Mm(Rand), Mm(y - 1.5), Mm(Rand), Mm(y + 1.5));
        g.DrawLine(stift, Mm(Rand + 100), Mm(y - 1.5), Mm(Rand + 100), Mm(y + 1.5));
        Text(g, "Kontrollmaß 100 mm – in Originalgröße (100 %, „Tatsächliche Größe“) drucken, an den Schnittmarken schneiden.",
            Rand + 104, y + 1, 7, false, XColors.DimGray, XStringFormats.BaseLineLeft);
        Text(g, $"{titel} · Seite {seite.ToString(CultureInfo.InvariantCulture)}", Rand, Rand - 5, 8, true, XColors.DimGray, XStringFormats.BaseLineLeft);
    }

    private static void Text(XGraphics g, string text, double x, double y, double pt, bool fett, XColor farbe, XStringFormat format)
        => g.DrawString(text, new XFont("Arial", pt, fett ? XFontStyleEx.Bold : XFontStyleEx.Regular), new XSolidBrush(farbe), Mm(x), Mm(y), format);

    private static double Mm(double mm) => XUnit.FromMillimeter(mm).Point;

    private static XColor Farbe(string hex)
    {
        var w = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return XColor.FromArgb((w >> 16) & 0xFF, (w >> 8) & 0xFF, w & 0xFF);
    }
}
