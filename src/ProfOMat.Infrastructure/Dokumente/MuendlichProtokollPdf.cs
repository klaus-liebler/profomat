using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using ProfOMat.Application.Muendlich;
using ProfOMat.Domain.Klausuren;
using ProfOMat.Domain.Muendlich;

namespace ProfOMat.Infrastructure.Dokumente;

/// <summary>
/// Protokoll der mündlichen Prüfung: Angaben, gestellte Fragen mit Punkten und Bemerkungen, Notenvorschlag und festgesetzte Note,
/// Unterschriftsfelder für Erst- und Zweitprüfer/in. Leere Angaben bleiben als Feld zum handschriftlichen Ausfüllen stehen.
/// </summary>
internal sealed class MuendlichProtokollPdf : IMuendlichProtokollPdf
{
    private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");
    private static readonly Color Blau = new(0, 79, 140);
    private static readonly Color Hell = new(227, 238, 247);

    static MuendlichProtokollPdf() => Schriften.Einrichten();

    public byte[] Erzeugen(MuendlichProtokollAnsicht a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var p = a.Pruefung;
        var dok = new Document();
        dok.Info.Title = "Protokoll der mündlichen Prüfung";
        dok.Info.Author = "ProfOMat";
        var stil = dok.Styles[StyleNames.Normal]!;
        stil.Font.Name = "Arial";
        stil.Font.Size = 10;

        var s = dok.AddSection();
        s.PageSetup.PageFormat = PageFormat.A4;
        s.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        s.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        s.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        s.PageSetup.RightMargin = Unit.FromCentimeter(2);
        var fuss = s.Footers.Primary.AddParagraph();
        fuss.Format.Font.Size = 7.5;
        fuss.Format.Font.Color = Colors.DimGray;
        fuss.AddText($"Protokoll der mündlichen Prüfung · erstellt mit ProfOMat am {a.Festgelegt} · Seite ");
        fuss.AddPageField();
        fuss.AddText(" von ");
        fuss.AddNumPagesField();

        var kopf = s.AddParagraph(a.Hochschule);
        kopf.Format.Font.Color = Colors.DimGray;
        var titel = s.AddParagraph("Protokoll der mündlichen Prüfung");
        titel.Format.Font.Size = 17;
        titel.Format.Font.Bold = true;
        titel.Format.SpaceAfter = Unit.FromPoint(10);

        // Angaben
        var angaben = s.AddTable();
        angaben.Borders.Width = 0.4;
        angaben.Borders.Color = Colors.LightGray;
        angaben.AddColumn(Unit.FromCentimeter(4.2));
        angaben.AddColumn(Unit.FromCentimeter(12.8));
        foreach (var (feld, wert) in new (string, string?)[]
                 {
                     ("Prüfung", p.Bezeichnung), ("Prüfling", p.Pruefling), ("Matrikelnummer", p.Matrikelnummer), ("Studiengang", p.Studiengang),
                     ("Beginn", a.Begonnen), ("Erstprüfer/in", a.Erstpruefer), ("Zweitprüfer/in", a.Zweitpruefer),
                 }.Concat(a.Protokollfuehrung is { } pf ? [("Protokoll geführt von", pf)] : []))
        {
            var r = angaben.AddRow();
            r.Height = Unit.FromCentimeter(0.75);
            r.VerticalAlignment = VerticalAlignment.Center;
            r.Cells[0].Shading.Color = Hell;
            r.Cells[0].AddParagraph(feld).Format.Font.Bold = true;
            r.Cells[1].AddParagraph(wert ?? "");
        }

        Ueberschrift(s, "Gestellte Fragen");
        var t = s.AddTable();
        t.Borders.Width = 0.4;
        t.Borders.Color = Colors.LightGray;
        foreach (var b in new[] { 0.8, 1.9, 1.5, 1.6, 11.2 })
        {
            t.AddColumn(Unit.FromCentimeter(b));
        }

        var k = t.AddRow();
        k.HeadingFormat = true;
        k.Format.Font.Bold = true;
        k.Shading.Color = Hell;
        Zellen(k, "#", "Frage", "Max.", "Punkte", "Bemerkung");
        RechtsBuendig(k, 2, 3);
        var nr = 0;
        foreach (var f in p.FragenSortiert)
        {
            var vermerk = f.Stand switch
            {
                FrageStand.Zurueckgestellt => "zurückgestellt, nicht beantwortet (0 Punkte)",
                FrageStand.Offen => "ohne Punkte (0 Punkte)",
                _ when f.Zurueckgestellt => "zurückgestellt und nachgeholt",
                _ => null,
            };
            var bemerkung = string.Join(" · ", new[] { vermerk, f.Bemerkung }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var r = t.AddRow();
            r.TopPadding = Unit.FromPoint(1.5);
            r.BottomPadding = Unit.FromPoint(1.5);
            Zellen(r, (++nr).ToString(Deutsch), f.Nummer, Zahl(f.MaxPunkte), f.Punkte is { } pk ? Zahl(pk) : "–", bemerkung);
            RechtsBuendig(r, 2, 3);
            if (vermerk is not null && f.Punkte is null)
            {
                r.Cells[4].Format.Font.Color = Colors.DimGray;
            }
        }

        var stand = p.OhneOffene;
        var summe = t.AddRow();
        summe.Format.Font.Bold = true;
        summe.Shading.Color = Hell;
        Zellen(summe, "", "Summe", Zahl(stand.Moeglich), Zahl(stand.Erreicht), stand.Prozent is { } pz ? $"{pz.ToString("0.#", Deutsch)} %" : "");
        RechtsBuendig(summe, 2, 3);

        // Ergebnis
        Ueberschrift(s, "Ergebnis");
        var vorschlag = p.Notenvorschlag;
        var v = s.AddParagraph($"Notenvorschlag nach dem Standard-Notenschlüssel (ab 49,5 % 4,0 … ab 94,5 % 1,0; nicht beantwortete Fragen mit 0 Punkten): "
                               + (vorschlag is { } vn ? $"{Note(vn)} ({Notenschluessel.Wort(vn)})" : "–"));
        v.Format.SpaceAfter = Unit.FromPoint(6);
        var ergebnis = s.AddTable();
        ergebnis.Borders.Width = 1;
        ergebnis.Borders.Color = Blau;
        ergebnis.AddColumn(Unit.FromCentimeter(17));
        var er = ergebnis.AddRow();
        er.TopPadding = Unit.FromPoint(6);
        er.BottomPadding = Unit.FromPoint(6);
        var note = er.Cells[0].AddParagraph();
        note.Format.Font.Size = 14;
        note.AddFormattedText("Festgesetzte Note: ", TextFormat.Bold);
        note.AddFormattedText($"{Note(p.Endnote!.Value)} ({Notenschluessel.Wort(p.Endnote.Value)})", TextFormat.Bold).Color = Blau;
        if (p.WeichtAb)
        {
            var ab = er.Cells[0].AddParagraph(p.Abweichungsbegruendung is { } b
                ? $"Abweichend vom Vorschlag. Begründung: {b}"
                : "Abweichend vom Vorschlag festgesetzt.");
            ab.Format.SpaceBefore = Unit.FromPoint(4);
        }

        // Unterschriften
        Ueberschrift(s, "Unterschriften");
        var u = s.AddTable();
        u.KeepTogether = true;
        foreach (var b in new[] { 4.6, 0.6, 5.6, 0.6, 5.6 })
        {
            u.AddColumn(Unit.FromCentimeter(b));
        }

        var platz = u.AddRow();
        platz.Height = Unit.FromCentimeter(1.8);
        var linie = u.AddRow();
        foreach (var (i, text) in new[] { (0, "Ort, Datum"), (2, $"Erstprüfer/in: {a.Erstpruefer}"), (4, a.Zweitpruefer is { } zw ? $"Zweitprüfer/in: {zw}" : "Zweitprüfer/in") })
        {
            linie.Cells[i].Borders.Top.Width = 0.8;
            linie.Cells[i].Borders.Top.Color = Colors.Black;
            var c = linie.Cells[i].AddParagraph(text);
            c.Format.Font.Size = 8.5;
            c.Format.Font.Color = Colors.DimGray;
        }

        var renderer = new PdfDocumentRenderer { Document = dok };
        renderer.RenderDocument();
        using var strom = new MemoryStream();
        renderer.PdfDocument.Save(strom, false);
        return strom.ToArray();
    }

    private static void Ueberschrift(Section s, string text)
    {
        var p = s.AddParagraph(text);
        p.Format.Font.Size = 12;
        p.Format.Font.Bold = true;
        p.Format.SpaceBefore = Unit.FromPoint(14);
        p.Format.SpaceAfter = Unit.FromPoint(5);
        p.Format.KeepWithNext = true;
    }

    private static void Zellen(Row r, params string[] werte)
    {
        for (var i = 0; i < werte.Length; i++)
        {
            r.Cells[i].AddParagraph(werte[i]);
        }
    }

    private static void RechtsBuendig(Row r, params int[] spalten)
    {
        foreach (var i in spalten)
        {
            r.Cells[i].Format.Alignment = ParagraphAlignment.Right;
        }
    }

    private static string Zahl(decimal d) => MuendlichePruefung.Zahl(d);

    private static string Note(decimal n) => n.ToString("0.0", Deutsch);
}
