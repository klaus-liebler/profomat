using System.Globalization;
using ClosedXML.Excel;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes.Charts;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using ProfOMat.Application.Klausuren;
using ProfOMat.Domain.Klausuren;

namespace ProfOMat.Infrastructure.Dokumente;

/// <summary>Sitzlisten, Aushang und Ergebnisveröffentlichung (PDF, XLSX). Die Veröffentlichung enthält nur Klausur-IDs.</summary>
internal sealed class KlausurDokumente : IKlausurDokumente
{
    private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

    static KlausurDokumente() => Schriften.Einrichten();

    public byte[] Sitzlisten(KlausurAnsicht a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var dok = Dokument($"Sitzlisten {a.Klausur.Titel}");
        foreach (var raum in a.GewaehlteRaeume)
        {
            var s = Abschnitt(dok, a);
            Titel(s, $"Sitzliste {raum.Kuerzel}", $"{a.Klausur.Titel} · {a.TerminText ?? "Termin offen"} · {raum.Bezeichnung}");
            var personen = a.Klausur.Angemeldete.Where(t => t.RaumId == raum.Id).OrderBy(t => t.Platz).ToList();
            var t = Tabelle(s, [1.4, 3.0, 5.6, 2.6, 4.2], "Platz", "Klausur-ID", "Name", "Matrikelnr.", "Unterschrift");
            foreach (var p in personen)
            {
                var r = Zeile(t, p.Platz?.ToString(Deutsch) ?? "", p.Code, p.Name, p.Matrikelnummer, "");
                r.Height = Unit.FromCentimeter(0.75);
                r.VerticalAlignment = VerticalAlignment.Center;
            }

            Klein(s, $"{personen.Count} Personen, {raum.Plaetze} Plätze. Bitte Ausweis prüfen und unterschreiben lassen.");
        }

        if (a.GewaehlteRaeume.Count == 0)
        {
            Abschnitt(dok, a).AddParagraph("Keine Räume gewählt.");
        }

        return Rendern(dok);
    }

    public byte[] Aushang(KlausurAnsicht a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var dok = Dokument($"Raumaufteilung {a.Klausur.Titel}");
        var s = Abschnitt(dok, a);
        Titel(s, $"Raumaufteilung {a.Klausur.Titel}", a.TerminText ?? "Termin offen");
        if (!a.Klausur.ZufaelligVerteilt)
        {
            var t = Tabelle(s, [7.0, 9.8], "Nachname", "Raum");
            foreach (var raum in a.GewaehlteRaeume)
            {
                var p = a.Klausur.Angemeldete.Where(x => x.RaumId == raum.Id).OrderBy(x => x.Platz).ToList();
                if (p.Count > 0)
                {
                    var z = Zeile(t, $"{p[0].Nachname} – {p[^1].Nachname}", $"{raum.Kuerzel}{(raum.Bezeichnung is null ? "" : $" ({raum.Bezeichnung})")}");
                    z.Format.Font.Size = 16;
                    z.Height = Unit.FromCentimeter(1.2);
                }
            }
        }
        else
        {
            var t = Tabelle(s, [7.0, 4.0, 2.5], "Name", "Raum", "Platz");
            foreach (var p in a.Klausur.Angemeldete.OrderBy(x => x.Nachname, StringComparer.Create(Deutsch, true)).ThenBy(x => x.Vorname))
            {
                Zeile(t, $"{p.Nachname}, {p.Vorname[..Math.Min(1, p.Vorname.Length)]}.", a.RaumVon(p), p.Platz?.ToString(Deutsch) ?? "");
            }
        }

        Klein(s, "Bitte Studierendenausweis bzw. Lichtbildausweis mitbringen.");
        return Rendern(dok);
    }

    public byte[] ErgebnisPdf(KlausurAnsicht a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var k = a.Klausur;
        var w = a.Auswertung;
        var dok = Dokument($"Ergebnisse {k.Titel}");
        var s = Abschnitt(dok, a);
        Titel(s, $"Ergebnisse der Klausur {k.Titel}", $"{a.TerminText ?? ""} · Höchstpunktzahl {Zahl(k.Hoechstpunktzahl)} · Aushang nur mit Klausur-ID");

        var oben = s.AddTable();
        oben.AddColumn(Unit.FromCentimeter(8.2));
        oben.AddColumn(Unit.FromCentimeter(8.6));
        var r = oben.AddRow();
        var kz = new Table();
        kz.Borders.Width = 0.4;
        kz.Borders.Color = Colors.LightGray;
        kz.AddColumn(Unit.FromCentimeter(5.4));
        kz.AddColumn(Unit.FromCentimeter(2.4));
        foreach (var (feld, wert) in Kennzahlen(a))
        {
            var z = kz.AddRow();
            z.Cells[0].AddParagraph(feld);
            var p = z.Cells[1].AddParagraph(wert);
            p.Format.Alignment = ParagraphAlignment.Right;
        }

        r.Cells[0].Elements.Add(kz);
        var fuss = r.Cells[0].AddParagraph($"* bezogen auf aktiv Teilnehmende (mehr als {Zahl(k.AktivSchwelle)} Punkte)");
        fuss.Format.Font.Size = 8;

        var chart = new Chart(ChartType.Column2D) { Width = Unit.FromCentimeter(8.4), Height = Unit.FromCentimeter(6.2) };
        var reihe = chart.SeriesCollection.AddSeries();
        reihe.Add(Enumerable.Range(1, 5).Select(n => (double)w.AnzahlGanzeNote[n]).ToArray());
        reihe.HasDataLabel = true;
        reihe.FillFormat.Color = new Color(0, 79, 140);
        chart.XValues.AddXSeries().Add("1", "2", "3", "4", "5");
        chart.XAxis.MajorTickMark = TickMarkType.Outside;
        chart.YAxis.MajorTickMark = TickMarkType.Outside;
        chart.YAxis.HasMajorGridlines = true;
        chart.YAxis.MajorTick = Math.Max(1, Math.Ceiling(w.AnzahlGanzeNote.Values.DefaultIfEmpty(0).Max() / 5.0));   // ganze Personen
        chart.YAxis.MinimumScale = 0;
        chart.HeaderArea.AddParagraph("Notenverteilung");
        r.Cells[1].Elements.Add(chart);

        s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(8);
        var aufgaben = k.AufgabenSortiert.ToList();
        var breiteAufgabe = Math.Min(1.6, 9.0 / Math.Max(1, aufgaben.Count));
        var spalten = new List<double> { 2.6, 1.3 };
        spalten.AddRange(aufgaben.Select(_ => breiteAufgabe));
        spalten.AddRange([1.5, 1.2]);
        var kopf = new List<string> { "Klausur-ID", "Anw." };
        kopf.AddRange(aufgaben.Select(x => x.Sonderpunkte ? "Sonder" : $"A{x.Nr}"));
        kopf.AddRange(["Summe", "Note"]);
        var tab = Tabelle(s, [.. spalten], [.. kopf]);
        tab.Format.Font.Size = 8.5;
        foreach (var z in w.Zeilen)
        {
            var werte = new List<string> { z.Teilnahme.Code, z.Anwesend ? "j" : "n" };
            werte.AddRange(z.Punkte.Select(p => z.Anwesend ? Zahl(p ?? 0) : ""));
            werte.AddRange([z.Anwesend ? Zahl(z.Summe) : "", z.Note?.ToString("0.0", Deutsch) ?? "NE"]);
            Zeile(tab, [.. werte]);
        }

        Klein(s, $"Max. Punkte je Aufgabe: {string.Join(" · ", aufgaben.Select(x => $"{(x.Sonderpunkte ? "Sonder" : $"A{x.Nr}")} {Zahl(x.MaxPunkte)}"))}. NE = nicht erschienen.");
        Klein(s, $"Notenschlüssel: {string.Join(" · ", k.Notenstufen.Select(n => $"ab {Zahl(n.Mindestpunkte)} → {n.Note.ToString("0.0", Deutsch)}"))}.");
        return Rendern(dok);
    }

    public byte[] ErgebnisXlsx(KlausurAnsicht a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var k = a.Klausur;
        var w = a.Auswertung;
        using var mappe = new XLWorkbook();
        var blatt = mappe.Worksheets.Add("Ergebnisse");
        blatt.Cell(1, 1).Value = $"Ergebnisse der Klausur {k.Titel}";
        blatt.Cell(1, 1).Style.Font.FontSize = 18;
        blatt.Cell(3, 1).Value = "Kennzahl";
        blatt.Cell(3, 2).Value = "Wert";
        blatt.Range(3, 1, 3, 2).Style.Font.Bold = true;
        var zeile = 4;
        foreach (var (feld, wert) in Kennzahlen(a))
        {
            blatt.Cell(zeile, 1).Value = feld;
            blatt.Cell(zeile, 2).Value = wert;
            blatt.Cell(zeile, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            zeile++;
        }

        blatt.Cell(zeile, 1).Value = $"* bezogen auf aktiv Teilnehmende (mehr als {Zahl(k.AktivSchwelle)} Punkte)";
        zeile += 2;
        var aufgaben = k.AufgabenSortiert.ToList();
        var kopf = new List<string> { "Klausur-ID", "Anwesend" };
        kopf.AddRange(aufgaben.Select(x => x.Sonderpunkte ? x.Titel : $"Aufgabe {x.Nr}"));
        kopf.AddRange(["Summe", "Note"]);
        for (var i = 0; i < kopf.Count; i++)
        {
            blatt.Cell(zeile, i + 1).Value = kopf[i];
        }

        blatt.Row(zeile).Style.Font.Bold = true;
        var kopfzeile = zeile;
        foreach (var z in w.Zeilen)
        {
            zeile++;
            blatt.Cell(zeile, 1).Value = z.Teilnahme.Code;
            blatt.Cell(zeile, 2).Value = z.Anwesend ? "j" : "n";
            for (var i = 0; i < z.Punkte.Count; i++)
            {
                if (z.Anwesend)
                {
                    blatt.Cell(zeile, 3 + i).Value = z.Punkte[i] ?? 0;
                }
            }

            if (z.Anwesend)
            {
                blatt.Cell(zeile, 3 + aufgaben.Count).Value = z.Summe;
            }

            if (z.Note is { } note)
            {
                blatt.Cell(zeile, 4 + aufgaben.Count).Value = note;
                blatt.Cell(zeile, 4 + aufgaben.Count).Style.NumberFormat.Format = "0.0";
            }
            else
            {
                blatt.Cell(zeile, 4 + aufgaben.Count).Value = "NE";
            }
        }

        blatt.Range(kopfzeile, 1, zeile, kopf.Count).SetAutoFilter();
        blatt.Columns().AdjustToContents();
        blatt.Column(1).Width = 30;

        var verteilung = mappe.Worksheets.Add("Notenverteilung");
        verteilung.Cell(1, 1).Value = "Note";
        verteilung.Cell(1, 2).Value = "Anzahl*";
        var n = 2;
        foreach (var (note, anzahl) in w.AnzahlNote)
        {
            verteilung.Cell(n, 1).Value = note;
            verteilung.Cell(n, 1).Style.NumberFormat.Format = "0.0";
            verteilung.Cell(n++, 2).Value = anzahl;
        }

        using var strom = new MemoryStream();
        mappe.SaveAs(strom);
        return strom.ToArray();
    }

    // ---------------------------------------------------------------- Hilfen
    private static IEnumerable<(string, string)> Kennzahlen(KlausurAnsicht a)
    {
        var w = a.Auswertung;
        yield return ("Angemeldete", w.Angemeldet.ToString(Deutsch));
        yield return ("Anwesende Teilnehmende", w.Anwesend.ToString(Deutsch));
        yield return ($"Aktiv Teilnehmende (>{Zahl(a.Klausur.AktivSchwelle)} Punkte)", w.Aktiv.ToString(Deutsch));
        yield return ("Durchfallquote*", w.Durchfallquote is { } q ? q.ToString("P1", Deutsch) : "–");
        yield return ("Durchschnittsnote*", w.Durchschnittsnote is { } d ? d.ToString("0.00", Deutsch) : "–");
        yield return ("Maximalpunktzahl*", w.Maximalpunktzahl is { } mx ? Zahl(mx) : "–");
        yield return ("Minimalpunktzahl", w.Minimalpunktzahl is { } mn ? Zahl(mn) : "–");
        for (var n = 1; n <= 5; n++)
        {
            yield return ($"Anzahl Note {n}*", w.AnzahlGanzeNote[n].ToString(Deutsch));
        }
    }

    private static string Zahl(decimal d) => d.ToString("0.##", Deutsch);

    private static Document Dokument(string titel)
    {
        var dok = new Document();
        dok.Info.Title = titel;
        dok.Info.Author = "ProfOMat";
        var stil = dok.Styles[StyleNames.Normal]!;
        stil.Font.Name = "Arial";
        stil.Font.Size = 10;
        return dok;
    }

    private static Section Abschnitt(Document dok, KlausurAnsicht a)
    {
        var s = dok.AddSection();
        s.PageSetup.PageFormat = PageFormat.A4;
        s.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        s.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        s.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        s.PageSetup.RightMargin = Unit.FromCentimeter(2);
        var f = s.Footers.Primary.AddParagraph();
        f.Format.Font.Size = 7.5;
        f.Format.Font.Color = Colors.DimGray;
        f.AddText($"{a.Klausur.Titel} · {a.Verantwortlich} · Seite ");
        f.AddPageField();
        f.AddText(" von ");
        f.AddNumPagesField();
        return s;
    }

    private static void Titel(Section s, string titel, string unter)
    {
        var p = s.AddParagraph(titel);
        p.Format.Font.Size = 17;
        p.Format.Font.Bold = true;
        var u = s.AddParagraph(unter);
        u.Format.Font.Color = Colors.DimGray;
        u.Format.SpaceAfter = Unit.FromPoint(10);
    }

    private static void Klein(Section s, string text)
    {
        var p = s.AddParagraph(text);
        p.Format.Font.Size = 8;
        p.Format.Font.Color = Colors.DimGray;
        p.Format.SpaceBefore = Unit.FromPoint(6);
    }

    private static Table Tabelle(Section s, double[] breitenCm, params string[] kopf)
    {
        var t = s.AddTable();
        t.Borders.Width = 0.4;
        t.Borders.Color = Colors.LightGray;
        foreach (var b in breitenCm)
        {
            t.AddColumn(Unit.FromCentimeter(b));
        }

        var r = t.AddRow();
        r.HeadingFormat = true;
        r.Format.Font.Bold = true;
        r.Shading.Color = new Color(227, 238, 247);
        for (var i = 0; i < kopf.Length; i++)
        {
            r.Cells[i].AddParagraph(kopf[i]);
        }

        return t;
    }

    private static Row Zeile(Table t, params string[] werte)
    {
        var r = t.AddRow();
        r.TopPadding = Unit.FromPoint(1.5);
        r.BottomPadding = Unit.FromPoint(1.5);
        for (var i = 0; i < werte.Length; i++)
        {
            r.Cells[i].AddParagraph(werte[i]);
        }

        return r;
    }

    private static byte[] Rendern(Document dok)
    {
        var renderer = new PdfDocumentRenderer { Document = dok };
        renderer.RenderDocument();
        using var strom = new MemoryStream();
        renderer.PdfDocument.Save(strom, false);
        return strom.ToArray();
    }
}
