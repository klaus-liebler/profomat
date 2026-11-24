using System.Globalization;
using ClosedXML.Excel;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using ProfOMat.Application.Klausuren;
using ProfOMat.Domain.Klausuren;

namespace ProfOMat.Infrastructure.Dokumente;

/// <summary>Sitzlisten, Aushang und Ergebnisveröffentlichung (PDF, XLSX). Die Veröffentlichung enthält nur Anwesende und von der Matrikelnummer nur die letzten Stellen.</summary>
internal sealed class KlausurDokumente : IKlausurDokumente
{
    private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");
    private static readonly Color Blau = new(0, 79, 140);
    private static readonly Color BlauHell = new(200, 222, 240);
    private static readonly Color Flaeche = new(238, 244, 250);
    private static readonly Color Zebra = new(246, 248, 251);
    private static readonly Color Linie = new(218, 224, 231);
    private static readonly Color Grau = new(105, 112, 120);
    private static readonly Color Rot = new(180, 35, 35);

    /// <summary>Satzspiegel A4 mit 2 cm Rand.</summary>
    private const double Breite = 17.0;

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
            var t = Tabelle(s, [1.4, 7.0, 3.2, 5.2], "Platz", "Name", "Matrikelnr.", "Unterschrift");
            foreach (var p in personen)
            {
                var r = Zeile(t, p.Platz?.ToString(Deutsch) ?? "", p.Name, p.Matrikelnummer, "");
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

        // Kopfband
        var band = s.AddTable();
        band.AddColumn(Unit.FromCentimeter(Breite));
        Buendig(band);
        var br = band.AddRow();
        br.Format.LeftIndent = Unit.FromPoint(10);
        br.Format.RightIndent = Unit.FromPoint(10);
        br.Shading.Color = Blau;
        br.TopPadding = Unit.FromPoint(10);
        br.BottomPadding = Unit.FromPoint(12);
        var dach = br.Cells[0].AddParagraph("KLAUSURERGEBNISSE");
        dach.Format.Font.Size = 8;
        dach.Format.Font.Color = BlauHell;
        dach.Format.Font.Bold = true;
        var titel = br.Cells[0].AddParagraph(k.Titel);
        titel.Format.Font.Size = 19;
        titel.Format.Font.Bold = true;
        titel.Format.Font.Color = Colors.White;
        titel.Format.SpaceBefore = Unit.FromPoint(2);
        var angaben = new[] { a.TerminText, k.Pruefer ?? a.Verantwortlich, k.Pruefungsnummer, $"Höchstpunktzahl {Zahl(k.Hoechstpunktzahl)}" }
            .Where(x => !string.IsNullOrWhiteSpace(x));
        var unter = br.Cells[0].AddParagraph(string.Join("  ·  ", angaben));
        unter.Format.Font.Size = 9;
        unter.Format.Font.Color = BlauHell;
        unter.Format.SpaceBefore = Unit.FromPoint(4);
        Abstand(s, 6);

        // Kennzahlen als Kacheln
        var kacheln = new (string Wert, string Was, string Hinweis)[]
        {
            (w.Anwesend.ToString(Deutsch), "anwesend", $"von {w.Angemeldet} angemeldet"),
            (w.Aktiv.ToString(Deutsch), "aktiv teilnehmend", $"mehr als {Zahl(k.AktivSchwelle)} Punkte"),
            (w.Durchschnittsnote?.ToString("0.00", Deutsch) ?? "–", "Durchschnittsnote*", "der aktiv Teilnehmenden"),
            (w.Durchfallquote?.ToString("P1", Deutsch) ?? "–", "Durchfallquote*", "Anteil Note 5,0"),
            (w.Maximalpunktzahl is { } mx ? Zahl(mx) : "–", "beste Punktzahl*", w.Minimalpunktzahl is { } mn ? $"niedrigste: {Zahl(mn)}" : ""),
        };
        var kt = s.AddTable();
        kt.Borders.Color = Colors.White;
        kt.Borders.Width = 6;
        Buendig(kt);
        foreach (var _ in kacheln)
        {
            kt.AddColumn(Unit.FromCentimeter(Breite / kacheln.Length));
        }

        var kr = kt.AddRow();
        kr.Shading.Color = Flaeche;
        kr.TopPadding = Unit.FromPoint(7);
        kr.BottomPadding = Unit.FromPoint(7);
        for (var i = 0; i < kacheln.Length; i++)
        {
            var c = kr.Cells[i];
            var wert = c.AddParagraph(kacheln[i].Wert);
            wert.Format.Font.Size = 20;
            wert.Format.Font.Bold = true;
            wert.Format.Font.Color = Blau;
            var was = c.AddParagraph(kacheln[i].Was);
            was.Format.Font.Size = 8.5;
            was.Format.Font.Bold = true;
            var hin = c.AddParagraph(kacheln[i].Hinweis);
            hin.Format.Font.Size = 7.5;
            hin.Format.Font.Color = Grau;
        }

        // Notenverteilung neben dem Notenschlüssel
        Ueberschrift(s, "Notenverteilung und Notenschlüssel");
        var mindest = k.Notenstufen.ToDictionary(n => n.Note, n => n.Mindestpunkte);
        var groesste = Math.Max(1, w.AnzahlNote.Values.DefaultIfEmpty(0).Max());
        const double balkenBreite = 9.4;
        var vt = s.AddTable();
        vt.Borders.Bottom.Width = 0.4;
        vt.Borders.Bottom.Color = Linie;
        Buendig(vt);
        foreach (var b in new[] { 1.4, 2.6, balkenBreite + 0.6, 1.5, 1.5 })
        {
            vt.AddColumn(Unit.FromCentimeter(b));
        }

        Kopfzeile(vt, ["Note", "ab Punkten", "", "Anzahl*", "Anteil*"], [1, 3, 4]);
        foreach (var (note, anzahl) in w.AnzahlNote.OrderBy(x => x.Key))
        {
            var r = vt.AddRow();
            r.VerticalAlignment = VerticalAlignment.Center;
            r.TopPadding = Unit.FromPoint(1.5);
            r.BottomPadding = Unit.FromPoint(1.5);
            if (KlausurAuswertung.Ganz(note) % 2 == 0)
            {
                r.Shading.Color = Zebra;
            }

            var n = r.Cells[0].AddParagraph(note.ToString("0.0", Deutsch));
            n.Format.Font.Bold = true;
            n.Format.Font.Color = note >= 5 ? Rot : Colors.Black;
            Rechts(r.Cells[1].AddParagraph(mindest.TryGetValue(note, out var m) ? Zahl(m) : "–"));
            if (anzahl > 0)
            {
                var balken = new Table();
                balken.AddColumn(Unit.FromCentimeter(Math.Max(0.15, balkenBreite * anzahl / groesste)));
                var bz = balken.AddRow();
                bz.Height = Unit.FromPoint(9);
                bz.HeightRule = RowHeightRule.Exactly;
                bz.Shading.Color = note >= 5 ? Rot : Blau;
                r.Cells[2].Elements.Add(balken);
            }

            Rechts(r.Cells[3].AddParagraph(anzahl == 0 ? "–" : anzahl.ToString(Deutsch)));
            var anteil = r.Cells[4].AddParagraph(anzahl == 0 || w.Aktiv == 0 ? "" : ((decimal)anzahl / w.Aktiv).ToString("P0", Deutsch));
            anteil.Format.Font.Color = Grau;
            Rechts(anteil);
        }

        // Ergebnisliste: nur Anwesende
        var anwesend = w.Zeilen.Where(z => z.Anwesend).ToList();
        Ueberschrift(s, $"Ergebnisse ({anwesend.Count} Anwesende)");
        var aufgaben = k.AufgabenSortiert.ToList();
        if (anwesend.Count == 0)
        {
            s.AddParagraph("Noch keine Ergebnisse.");
        }
        else
        {
            var breiteAufgabe = Math.Min(2.4, (Breite - 2.6 - 1.8 - 1.5) / Math.Max(1, aufgaben.Count));
            var et = s.AddTable();
            et.Format.Font.Size = 8.5;
            et.Borders.Bottom.Width = 0.3;
            et.Borders.Bottom.Color = Linie;
            Buendig(et);
            et.AddColumn(Unit.FromCentimeter(2.6));
            foreach (var _ in aufgaben)
            {
                et.AddColumn(Unit.FromCentimeter(breiteAufgabe));
            }

            et.AddColumn(Unit.FromCentimeter(1.8));
            et.AddColumn(Unit.FromCentimeter(1.5));
            var kopf = new List<string> { "Matr.-Ende" };
            kopf.AddRange(aufgaben.Select(x => x.Sonderpunkte ? "Sonder" : $"A{x.Nr}"));
            kopf.AddRange(["Summe", "Note"]);
            var kz = Kopfzeile(et, [.. kopf], [.. Enumerable.Range(1, aufgaben.Count + 2)]);
            for (var i = 0; i < aufgaben.Count; i++)
            {
                var max = kz.Cells[i + 1].AddParagraph($"max {Zahl(aufgaben[i].MaxPunkte)}");
                max.Format.Font.Size = 6.5;
                max.Format.Font.Bold = false;
                Rechts(max);
            }

            var gerade = false;
            foreach (var z in anwesend)
            {
                var r = et.AddRow();
                r.TopPadding = Unit.FromPoint(1.5);
                r.BottomPadding = Unit.FromPoint(1.5);
                if (gerade)
                {
                    r.Shading.Color = Zebra;
                }

                gerade = !gerade;
                var code = r.Cells[0].AddParagraph(z.Teilnahme.Code);
                code.Format.Font.Name = "Courier New";
                code.Format.Font.Bold = true;
                for (var i = 0; i < z.Punkte.Count; i++)
                {
                    Rechts(r.Cells[i + 1].AddParagraph(Zahl(z.Punkte[i] ?? 0)));
                }

                var summe = r.Cells[aufgaben.Count + 1].AddParagraph(Zahl(z.Summe));
                summe.Format.Font.Bold = true;
                Rechts(summe);
                var note = r.Cells[aufgaben.Count + 2].AddParagraph(z.Note?.ToString("0.0", Deutsch) ?? "–");
                note.Format.Font.Bold = true;
                note.Format.Font.Color = z.Note >= 5 ? Rot : Colors.Black;
                Rechts(note);
            }
        }

        Klein(s, $"* bezogen auf die aktiv Teilnehmenden (mehr als {Zahl(k.AktivSchwelle)} Punkte). Aufgeführt sind nur Anwesende, "
            + "gekennzeichnet mit den letzten vier Stellen der Matrikelnummer (bei gleichen Endziffern mit so vielen Stellen wie nötig).");
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
        var kopf = new List<string> { "Matrikelnr. (Ende)", "Anwesend" };
        kopf.AddRange(aufgaben.Select(x => x.Sonderpunkte ? x.Titel : $"Aufgabe {x.Nr}"));
        kopf.AddRange(["Summe", "Note"]);
        for (var i = 0; i < kopf.Count; i++)
        {
            blatt.Cell(zeile, i + 1).Value = kopf[i];
        }

        blatt.Row(zeile).Style.Font.Bold = true;
        var kopfzeile = zeile;
        foreach (var z in w.Zeilen.Where(z => z.Anwesend))
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

    private static void Abstand(Section s, double punkte) => s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(punkte);

    private static void Ueberschrift(Section s, string text)
    {
        var p = s.AddParagraph(text);
        p.Format.Font.Size = 11.5;
        p.Format.Font.Bold = true;
        p.Format.Font.Color = Blau;
        p.Format.SpaceBefore = Unit.FromPoint(16);
        p.Format.SpaceAfter = Unit.FromPoint(5);
        p.Format.KeepWithNext = true;
    }

    private static Row Kopfzeile(Table t, string[] kopf, int[] rechts)
    {
        var r = t.AddRow();
        r.HeadingFormat = true;
        r.Shading.Color = Blau;
        r.TopPadding = Unit.FromPoint(3);
        r.BottomPadding = Unit.FromPoint(3);
        r.Format.Font.Bold = true;
        r.Format.Font.Size = 8;
        r.Format.Font.Color = Colors.White;
        for (var i = 0; i < kopf.Length; i++)
        {
            var p = r.Cells[i].AddParagraph(kopf[i]);
            if (rechts.Contains(i))
            {
                Rechts(p);
            }
        }

        return r;
    }

    /// <summary>MigraDoc rückt Zellen um den Innenabstand nach links aus; so schließt die Tabelle bündig mit dem Text ab.</summary>
    private static void Buendig(Table t) => t.Rows.LeftIndent = Unit.FromPoint((t.LeftPadding.IsEmpty ? Unit.FromMillimeter(1.2) : t.LeftPadding).Point / 2);

    private static void Rechts(Paragraph p) => p.Format.Alignment = ParagraphAlignment.Right;

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
