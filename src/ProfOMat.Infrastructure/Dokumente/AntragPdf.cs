using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using ProfOMat.Application;
using ProfOMat.Application.Drittpruefung;
using ProfOMat.Application.Signaturen;
using ProfOMat.Domain.Drittpruefung;
using QRCoder;

namespace ProfOMat.Infrastructure.Dokumente;

/// <summary>
/// PDF des abgeschlossenen Antrags: Angaben, Festlegung, Bestellung, alle Unterschriften (Bild bzw. Schlüssel-Fingerabdruck,
/// Zeitpunkt, Prüfergebnis) und im Anhang der zuletzt unterschriebene Text. Der QR-Code führt zur Prüfansicht im ProfOMat.
/// Das PDF ist eine Ausfertigung; maßgeblich sind die im System gespeicherten und versiegelten Unterschriften.
/// </summary>
internal sealed class AntragPdf(Ortszeit ortszeit) : IAntragPdf
{
    private static readonly byte[] Schwarz = [0, 0, 0];
    private static readonly byte[] Weiss = [255, 255, 255];

    static AntragPdf() => Schriften.Einrichten();

    public byte[] Erzeugen(AntragAnsicht a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var antrag = a.Antrag;
        var dok = new Document();
        dok.Info.Title = $"Antrag auf mündliche Prüfung im dritten Versuch {antrag.Nummer}";
        dok.Info.Author = "ProfOMat";
        dok.Info.Subject = $"Prüfwert SHA-256 {a.Pruefwert}";
        var normal = dok.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 10;

        var s = dok.AddSection();
        s.PageSetup.PageFormat = PageFormat.A4;
        s.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
        s.PageSetup.LeftMargin = Unit.FromCentimeter(2.2);
        s.PageSetup.RightMargin = Unit.FromCentimeter(2);
        s.PageSetup.BottomMargin = Unit.FromCentimeter(3.4);
        s.PageSetup.FooterDistance = Unit.FromCentimeter(0.8);

        Fusszeile(s, a);

        var kopf = s.AddParagraph(a.Hochschule);
        kopf.Format.Font.Size = 9;
        kopf.Format.Font.Color = Colors.DimGray;
        var titel = s.AddParagraph("Antrag auf mündliche Prüfung im dritten Versuch");
        titel.Format.Font.Size = 16;
        titel.Format.Font.Bold = true;
        titel.Format.SpaceBefore = Unit.FromPoint(6);
        var unter = s.AddParagraph($"zweite Wiederholung einer schriftlichen Prüfungsleistung als mündliche Prüfung (ATPO § 18 Abs. 1 Satz 3) · {antrag.Nummer}");
        unter.Format.Font.Size = 9;
        unter.Format.SpaceAfter = Unit.FromPoint(12);

        Abschnitt(s, "Antrag der/des Studierenden");
        Tabelle(s,
            ("Name", antrag.Name),
            ("Matrikelnummer", antrag.Matrikelnummer),
            ("E-Mail", antrag.Email),
            ("Studiengang", a.Studiengang.Anzeige),
            ("Prüfung", a.Pruefung.Anzeige),
            ("Gewünschte/r Erstprüfende/r", a.Erstpruefende.Anzeigename),
            ("Terminliche Einschränkungen/Wünsche", antrag.Terminwuensche ?? "–"),
            ("Wunsch Zweitprüfende/r", antrag.Zweitpruefungswunsch ?? "–"),
            ("Eingereicht", ortszeit.Kurz(antrag.Erstellt)),
            ("Mailadresse bestätigt", antrag.EmailBestaetigt is { } b ? ortszeit.Kurz(b) : "–"));

        Abschnitt(s, "Festlegung durch die/den Erstprüfende/n");
        Tabelle(s,
            ("Termin", a.Termin ?? "–"),
            ("Ort", antrag.Ort ?? "–"),
            ("Erstprüfende/r", a.Erstpruefende.Anzeigename),
            ("Zweitprüfende/r", antrag.ZweitpruefendeName ?? "–"),
            ("Bemerkung", antrag.Bemerkung ?? "–"));

        Abschnitt(s, "Bestellung der Prüfenden durch das Studiendekanat (ATPO § 24 Abs. 2)");
        Tabelle(s,
            ("Studiendekan/in", a.Studiendekanat?.Anzeigename ?? "–"),
            ("Abgeschlossen", antrag.Abgeschlossen is { } z ? ortszeit.Kurz(z) : "–"));

        Abschnitt(s, "Unterschriften");
        foreach (var befund in a.Signaturen.Where(b => b.Signatur.Ueberholt is null))
        {
            Unterschrift(s, befund);
        }

        var hinweis = s.AddParagraph(
            "Hinweis: Dieses Dokument ist die Ausfertigung eines elektronisch geführten Vorgangs. Jede Unterschrift bezieht sich auf den "
            + "vollständigen Antragsstand einschließlich der vorherigen Unterschrift (Hash-Kette) und ist mit dem Systemsiegel des ProfOMat "
            + "versehen. Maßgeblich und jederzeit prüfbar sind die im ProfOMat gespeicherten Unterschriften (QR-Code bzw. Link in der Fußzeile).");
        hinweis.Format.Font.Size = 8;
        hinweis.Format.Font.Color = Colors.DimGray;
        hinweis.Format.SpaceBefore = Unit.FromPoint(10);

        // Anhang: zuletzt unterschriebener Text (enthält alle Angaben und die Kette)
        if (antrag.LetzteGueltigeSignatur is { } letzte)
        {
            var anhang = s.AddParagraph("Anhang: zuletzt unterschriebener Text");
            anhang.Format.PageBreakBefore = true;
            anhang.Format.Font.Bold = true;
            anhang.Format.Font.Size = 11;
            anhang.Format.SpaceAfter = Unit.FromPoint(6);
            foreach (var zeile in letzte.Inhalt.Split('\n'))
            {
                var p = s.AddParagraph(zeile.Length == 0 ? " " : zeile);
                p.Format.Font.Name = "Courier New";
                p.Format.Font.Size = 7.5;
            }
        }

        var renderer = new PdfDocumentRenderer { Document = dok };
        renderer.RenderDocument();
        using var strom = new MemoryStream();
        renderer.PdfDocument.Save(strom, false);
        return strom.ToArray();
    }

    private static void Fusszeile(Section s, AntragAnsicht a)
    {
        var fuss = s.Footers.Primary.AddTable();
        fuss.AddColumn(Unit.FromCentimeter(2.4));
        fuss.AddColumn(Unit.FromCentimeter(14.4));
        var z = fuss.AddRow();
        var bild = z.Cells[0].AddImage("base64:" + Convert.ToBase64String(Qr(a.Link)));
        bild.Width = Unit.FromCentimeter(2.1);
        bild.LockAspectRatio = true;
        var t = z.Cells[1].AddParagraph();
        t.Format.Font.Size = 7.5;
        t.AddFormattedText($"{a.Antrag.Nummer} · Prüfwert (SHA-256 der letzten Unterschrift):", TextFormat.Bold);
        t.AddLineBreak();
        t.AddText(a.Pruefwert ?? "–");
        t.AddLineBreak();
        t.AddText($"Prüfen: {a.Link}");
        t.AddLineBreak();
        t.AddText("Seite ");
        t.AddPageField();
        t.AddText(" von ");
        t.AddNumPagesField();
    }

    private static void Abschnitt(Section s, string titel)
    {
        var p = s.AddParagraph(titel);
        p.Format.Font.Bold = true;
        p.Format.Font.Size = 11;
        p.Format.SpaceBefore = Unit.FromPoint(10);
        p.Format.SpaceAfter = Unit.FromPoint(4);
        p.Format.Borders.Bottom.Width = 0.5;
        p.Format.Borders.Bottom.Color = Colors.Gray;
    }

    private static void Tabelle(Section s, params (string Feld, string Wert)[] zeilen)
    {
        var t = s.AddTable();
        t.AddColumn(Unit.FromCentimeter(5.2));
        t.AddColumn(Unit.FromCentimeter(11.6));
        foreach (var (feld, wert) in zeilen)
        {
            var r = t.AddRow();
            r.TopPadding = Unit.FromPoint(1.5);
            r.BottomPadding = Unit.FromPoint(1.5);
            var f = r.Cells[0].AddParagraph(feld);
            f.Format.Font.Color = Colors.DimGray;
            r.Cells[1].AddParagraph(wert);
        }
    }

    private void Unterschrift(Section s, SignaturBefund befund)
    {
        var sig = befund.Signatur;
        var t = s.AddTable();
        t.Borders.Width = 0.5;
        t.Borders.Color = Colors.LightGray;
        t.AddColumn(Unit.FromCentimeter(6.2));
        t.AddColumn(Unit.FromCentimeter(10.6));
        var r = t.AddRow();
        r.TopPadding = Unit.FromPoint(4);
        r.BottomPadding = Unit.FromPoint(4);
        r.VerticalAlignment = VerticalAlignment.Center;
        if (sig.Art == Signaturart.Handschrift && sig.BildPng is { } png)
        {
            var bild = r.Cells[0].AddImage("base64:" + Convert.ToBase64String(png));
            bild.Width = Unit.FromCentimeter(5.6);
            bild.LockAspectRatio = true;
        }
        else
        {
            var p = r.Cells[0].AddParagraph();
            p.AddFormattedText("Digital signiert", TextFormat.Bold);
            p.AddLineBreak();
            p.AddText(sig.Algorithmus ?? "");
            p.AddLineBreak();
            p.AddText("Schlüssel " + SignaturPruefung.OeffentlicherSchluessel.Kurz(sig.Fingerabdruck ?? new string('0', 16)));
        }

        var rechts = r.Cells[1].AddParagraph();
        rechts.AddFormattedText(sig.Schritt.Anzeigename(), TextFormat.Bold);
        rechts.AddLineBreak();
        rechts.AddText($"{sig.Unterzeichner}, {ortszeit.Kurz(sig.Zeitpunkt)}");
        rechts.AddLineBreak();
        rechts.AddText(sig.Art.Anzeigename());
        rechts.AddLineBreak();
        var status = rechts.AddFormattedText(befund.Gueltig ? "Prüfung: in Ordnung" : "Prüfung: FEHLER – " + string.Join(" ", befund.Hinweise));
        status.Font.Color = befund.Gueltig ? Colors.DarkGreen : Colors.DarkRed;
        rechts.AddLineBreak();
        var hash = rechts.AddFormattedText($"SHA-256 {sig.InhaltHash}");
        hash.Font.Size = 7;
        hash.Font.Color = Colors.DimGray;
        s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
    }

    private static byte[] Qr(string inhalt)
    {
        using var daten = QRCodeGenerator.GenerateQrCode(inhalt, QRCodeGenerator.ECCLevel.M);
        // RGB-Farben: das 1-Bit-PNG von QRCoder erkennt PDFsharp nicht (aus VibeVHS).
        return new PngByteQRCode(daten).GetGraphic(10, Schwarz, Weiss, drawQuietZones: true);
    }
}
