using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace ProfOMat.Application.Gefache;

/// <summary>
/// Schnittvorlagen für den Lasercutter: je Platte eine SVG-Datei (Maße in mm, Dokument = Plattengröße) und eine DXF-Datei (R12, mm).
/// Schnitt rot (#FF0000, Haarlinie), Gravur blau (#0000FF) – in der Lasersoftware den Farben Schneiden bzw. Gravieren zuordnen.
/// </summary>
public static class Schnittvorlage
{
    public const string Schnittfarbe = "#FF0000";
    public const string Gravurfarbe = "#0000FF";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>SVG einer Platte; mit <paramref name="vorschau"/> für die Seite (Plattenumriss, Füllung, skalierbar).</summary>
    public static string Svg(Gefachplan plan, Platte platte, string titel, bool vorschau = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(platte);
        var (pl, pb) = ((double)plan.Angaben.PlatteLaengeMm, (double)plan.Angaben.PlatteBreiteMm);
        var sb = new StringBuilder();
        if (!vorschau)
        {
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {Z(pl)} {Z(pb)}\"");
        sb.Append(vorschau
            ? " class=\"pm-gf-platte\" role=\"img\" preserveAspectRatio=\"xMidYMid meet\""
            : $" width=\"{Z(pl)}mm\" height=\"{Z(pb)}mm\" version=\"1.1\"");
        sb.Append(">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<title>{WebUtility.HtmlEncode(titel)} – Platte {platte.Nr}</title>\n");
        if (vorschau)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"0\" y=\"0\" width=\"{Z(pl)}\" height=\"{Z(pb)}\" fill=\"#F3E6D3\" stroke=\"#8A7A66\" stroke-width=\"1\"/>\n");
        }

        var linie = vorschau ? "fill=\"#D9B88F\" stroke=\"#B00000\" stroke-width=\"0.8\"" : $"fill=\"none\" stroke=\"{Schnittfarbe}\" stroke-width=\"0.1\"";
        sb.Append(CultureInfo.InvariantCulture, $"<g id=\"schnitt\" {linie}>\n");
        foreach (var p in platte.Teile)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<path data-teil=\"{p.Wand.Name}\" d=\"{Pfad(p)}\"/>\n");
        }

        sb.Append("</g>\n");
        var gravuren = platte.Teile.Where(p => p.Wand.Gravur is not null).ToList();
        if (gravuren.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<g id=\"gravur\" fill=\"{Gravurfarbe}\" stroke=\"none\" font-family=\"Arial, Helvetica, sans-serif\">\n");
            foreach (var p in gravuren)
            {
                var g = p.Wand.Gravur!;
                var (x, y) = p.Abbilden(g.X, g.Y);
                var drehung = p.Gedreht ? $" transform=\"rotate(90 {Z(x)} {Z(y)})\"" : "";
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text x=\"{Z(x)}\" y=\"{Z(y)}\" font-size=\"{Z(g.Hoehe / 0.716)}\"{drehung}>{WebUtility.HtmlEncode(g.Text)}</text>\n");
            }

            sb.Append("</g>\n");
        }

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    /// <summary>
    /// SVG-Pfad eines Teils in Plattenkoordinaten. Wand → Platte kehrt die y-Achse um (y nach unten), damit auch den Drehsinn: ein
    /// positiver Bulge (gegen den Uhrzeigersinn in Wandkoordinaten) ist in SVG die negative Winkelrichtung, also sweep-flag 0.
    /// </summary>
    public static string Pfad(Platzierung p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var k = p.Wand.Kontur;
        var sb = new StringBuilder();
        var (x0, y0) = p.Abbilden(k[0].X, k[0].Y);
        sb.Append(CultureInfo.InvariantCulture, $"M{Z(x0)} {Z(y0)}");
        for (var i = 0; i < k.Count; i++)
        {
            var a = k[i];
            var b = k[(i + 1) % k.Count];
            var (x, y) = p.Abbilden(b.X, b.Y);
            if (a.Bulge == 0)
            {
                if (i < k.Count - 1)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" L{Z(x)} {Z(y)}");
                }
            }
            else
            {
                var sehne = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                var r = sehne * (1 + a.Bulge * a.Bulge) / (4 * Math.Abs(a.Bulge));
                sb.Append(CultureInfo.InvariantCulture, $" A{Z(r)} {Z(r)} 0 {(Math.Abs(a.Bulge) > 1 ? 1 : 0)} {(a.Bulge > 0 ? 0 : 1)} {Z(x)} {Z(y)}");
            }
        }

        return sb.Append('Z').ToString();
    }

    /// <summary>DXF (AutoCAD R12, mm): je Teil eine geschlossene Polylinie mit Bögen (Layer SCHNITT, rot), Gravur als TEXT (Layer GRAVUR, blau).</summary>
    public static string Dxf(Gefachplan plan, Platte platte)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(platte);
        var pb = (double)plan.Angaben.PlatteBreiteMm;
        var sb = new StringBuilder();
        void G(int code, string wert) => sb.Append(code.ToString(Inv)).Append("\r\n").Append(wert).Append("\r\n");
        void Gz(int code, double wert) => G(code, Z(wert));

        G(0, "SECTION");
        G(2, "HEADER");
        G(9, "$ACADVER");
        G(1, "AC1009");
        G(9, "$INSUNITS");
        G(70, "4");
        G(9, "$EXTMIN");
        Gz(10, 0);
        Gz(20, 0);
        G(9, "$EXTMAX");
        Gz(10, (double)plan.Angaben.PlatteLaengeMm);
        Gz(20, pb);
        G(0, "ENDSEC");
        G(0, "SECTION");
        G(2, "TABLES");
        G(0, "TABLE");
        G(2, "LAYER");
        G(70, "2");
        foreach (var (name, farbe) in new[] { ("SCHNITT", "1"), ("GRAVUR", "5") })
        {
            G(0, "LAYER");
            G(2, name);
            G(70, "0");
            G(62, farbe);
            G(6, "CONTINUOUS");
        }

        G(0, "ENDTAB");
        G(0, "ENDSEC");
        G(0, "SECTION");
        G(2, "ENTITIES");
        foreach (var p in platte.Teile)
        {
            G(0, "POLYLINE");
            G(8, "SCHNITT");
            G(66, "1");
            Gz(10, 0);
            Gz(20, 0);
            Gz(30, 0);
            G(70, "1");
            foreach (var e in p.Wand.Kontur)
            {
                var (x, y) = p.Abbilden(e.X, e.Y);
                G(0, "VERTEX");
                G(8, "SCHNITT");
                Gz(10, x);
                Gz(20, pb - y);
                Gz(30, 0);
                if (e.Bulge != 0)
                {
                    Gz(42, e.Bulge);   // Wand → Platte ist (mit der y-Umkehr der DXF) eine Drehung: Bulge bleibt
                }
            }

            G(0, "SEQEND");
            G(8, "SCHNITT");
        }

        foreach (var p in platte.Teile.Where(p => p.Wand.Gravur is not null))
        {
            var g = p.Wand.Gravur!;
            var (x, y) = p.Abbilden(g.X, g.Y);
            G(0, "TEXT");
            G(8, "GRAVUR");
            Gz(10, x);
            Gz(20, pb - y);
            Gz(30, 0);
            Gz(40, g.Hoehe);
            G(1, g.Text);
            if (p.Gedreht)
            {
                Gz(50, 270);
            }
        }

        G(0, "ENDSEC");
        G(0, "EOF");
        return sb.ToString();
    }

    /// <summary>Alle Platten als SVG und DXF in einem ZIP.</summary>
    public static byte[] Zip(Gefachplan plan, string titel)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var platte in plan.Platten)
            {
                Eintrag(zip, $"platte-{platte.Nr}.svg", Svg(plan, platte, titel));
                Eintrag(zip, $"platte-{platte.Nr}.dxf", Dxf(plan, platte));
            }
        }

        return ms.ToArray();
    }

    private static void Eintrag(ZipArchive zip, string name, string inhalt)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        var bytes = new UTF8Encoding(false).GetBytes(inhalt);
        s.Write(bytes);
    }

    private static string Z(double d) => Math.Round(d, 3).ToString("0.###", Inv);
}
