using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using ProfOMat.Application.Drittpruefung;
using ProfOMat.Application.Signaturen;
using ProfOMat.Domain.Drittpruefung;

namespace ProfOMat.Infrastructure.Dokumente;

/// <summary>
/// Einladungsverfahren: Das Formular „Antrag auf mündliche Wiederholungsprüfung“ der Fakultät IuI (eingebettete Vorlage) wird mit
/// den Angaben und den Unterschriften (gezeichnetes Bild bzw. Vermerk „digital signiert“) gefüllt; dahinter folgt ein Beiblatt.
/// Die Koordinaten (Punkte von oben links) stammen aus der Vorlage (Linien und Kästchen); bei einer neuen Vorlage hier anpassen.
/// </summary>
internal sealed partial class AntragPdf
{
    private const string Vorlage = "ProfOMat.Vorlagen.Antrag_muendliche_Wiederholungspruefung_IuI.pdf";

    /// <summary>Unterschriftszeilen: Linie (y), Spalten Datum, Name, Unterschrift (x) und verfügbare Höhe über der Linie.</summary>
    private static readonly Dictionary<Signaturschritt, (double Linie, double Datum, double? Name, double Unterschrift, double Hoehe)> Zeilen = new()
    {
        [Signaturschritt.Antragstellung] = (612.5, 202, null, 322, 25),
        [Signaturschritt.Erstpruefung] = (649.0, 202, 300, 432, 22),
        [Signaturschritt.Zweitpruefung] = (714.8, 202, 300, 432, 24),
        [Signaturschritt.Studiendekanat] = (747.7, 202, 300, 432, 18),
    };

    private static readonly XColor Tinte = XColor.FromArgb(20, 40, 110);

    private byte[] Formular(AntragAnsicht a)
    {
        var antrag = a.Antrag;
        var renderer = new PdfDocumentRenderer { Document = Beiblatt(a) };
        renderer.RenderDocument();
        var pdf = renderer.PdfDocument;
        pdf.Info.Title = $"Antrag auf mündliche Wiederholungsprüfung {antrag.Nummer}";
        pdf.Info.Author = "ProfOMat";
        pdf.Info.Subject = $"Prüfwert SHA-256 {a.Pruefwert}";

        using var quelle = typeof(AntragPdf).Assembly.GetManifestResourceStream(Vorlage)
                           ?? throw new InvalidOperationException($"Formularvorlage {Vorlage} fehlt.");
        using var vorlage = PdfReader.Open(quelle, PdfDocumentOpenMode.Import);
        var seite = pdf.InsertPage(0, vorlage.Pages[0]);
        var bilder = new List<XImage>();
        try
        {
            using (var g = XGraphics.FromPdfPage(seite, XGraphicsPdfPageOptions.Append))
            {
                Ausfuellen(g, a, bilder);
            }

            using var strom = new MemoryStream();
            pdf.Save(strom, false);
            return strom.ToArray();
        }
        finally
        {
            foreach (var b in bilder)
            {
                b.Dispose();
            }
        }
    }

    private void Ausfuellen(XGraphics g, AntragAnsicht a, List<XImage> bilder)
    {
        var antrag = a.Antrag;
        var basis = antrag.GueltigeSignaturen.FirstOrDefault(s => s.Schritt == Signaturschritt.Antragstellung);

        // 2. Wiederholung (3. Versuch) ankreuzen
        var stift = new XPen(Tinte, 1.3);
        g.DrawLine(stift, 71.7, 284.0, 83.7, 296.0);
        g.DrawLine(stift, 83.7, 284.0, 71.7, 296.0);

        Eintrag(g, antrag.Nachname, 93, 456.3, 208);
        Eintrag(g, antrag.Vorname, 354, 456.3, 164);
        Eintrag(g, antrag.Matrikelnummer, 121, 482.6, 91);
        Eintrag(g, Wert(basis, "Studiengang", a.Studiengang.Anzeige), 278, 482.6, 240);
        Eintrag(g, Wert(basis, "Modul", a.Pruefung.Anzeige), 130, 545.6, 388);
        if (antrag.Termin is { } termin)
        {
            Eintrag(g, ortszeit.Datum(termin), 147, 578.4, 88);
            Eintrag(g, ortszeit.Lokal(termin).ToString("HH:mm 'Uhr'", System.Globalization.CultureInfo.InvariantCulture), 274, 578.4, 100);
        }

        Eintrag(g, antrag.Ort, 412, 578.4, 106);

        // „Beisitzer/in“ streichen: die letzte Wiederholung findet mit zwei Prüfenden statt
        g.DrawLine(new XPen(Tinte, 0.9), 56, 689.8, 106, 689.8);

        foreach (var s in antrag.GueltigeSignaturen)
        {
            if (!Zeilen.TryGetValue(s.Schritt, out var z))
            {
                continue;
            }

            Eintrag(g, ortszeit.Datum(s.Zeitpunkt), z.Datum, z.Linie - 2.5, 90, 9);
            if (z.Name is { } nameX)
            {
                Eintrag(g, s.Unterzeichner, nameX, z.Linie - 2.5, 126, 8.5);
            }

            if (s.Art == Signaturart.Handschrift && s.BildPng is { } png)
            {
                var bild = XImage.FromStream(new MemoryStream(png, writable: false));
                bilder.Add(bild);
                var hoehe = z.Hoehe;
                var breite = Math.Min(hoehe * bild.PixelWidth / Math.Max(1, bild.PixelHeight), 519 - z.Unterschrift);
                hoehe = breite * bild.PixelHeight / Math.Max(1, bild.PixelWidth);
                g.DrawImage(bild, z.Unterschrift, z.Linie - 0.8 - hoehe, breite, hoehe);
            }
            else
            {
                Eintrag(g, $"Digital signiert ({s.Algorithmus?.Split('-')[0]})", z.Unterschrift, z.Linie - 10.5, 519 - z.Unterschrift, 7, fett: true);
                Eintrag(g, "Schlüssel " + SignaturPruefung.OeffentlicherSchluessel.Kurz(s.Fingerabdruck ?? new string('0', 16)), z.Unterschrift, z.Linie - 3,
                    519 - z.Unterschrift, 6);
            }
        }

        // QR-Code zur Prüfansicht und Vermerk am rechten Rand
        using (var qr = XImage.FromStream(new MemoryStream(Qr(a.Link), writable: false)))
        {
            g.DrawImage(qr, 524, 22, 50, 50);
        }

        var grau = new XSolidBrush(XColor.FromArgb(90, 90, 90));
        g.DrawString("Prüfen im ProfOMat", new XFont("Arial", 5.5), grau, 549, 78, XStringFormats.BaseLineCenter);
        var zustand = $"{antrag.Nummer} · elektronisch geführt im ProfOMat, Stand {ortszeit.Kurz(antrag.LetzteGueltigeSignatur?.Zeitpunkt ?? antrag.Erstellt)}"
                      + $" ({antrag.Status.Anzeigename()}) · Prüfwert {(a.Pruefwert is { } p ? p[..16] + "…" : "–")} · Unterschriften und Prüfprotokoll: Beiblatt";
        var zustand2 = g.Save();
        g.RotateAtTransform(-90, new XPoint(582, 790));
        g.DrawString(zustand, new XFont("Arial", 6), grau, 582, 790, XStringFormats.BaseLineLeft);
        g.Restore(zustand2);
    }

    /// <summary>Text in eine Formularzeile; zu lange Texte werden kleiner gesetzt (bis 6 pt) und notfalls gekürzt.</summary>
    private static void Eintrag(XGraphics g, string? text, double x, double grundlinie, double breite, double groesse = 10, bool fett = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        text = text.ReplaceLineEndings(" ").Trim();
        var stil = fett ? XFontStyleEx.Bold : XFontStyleEx.Regular;
        var schrift = new XFont("Arial", groesse, stil);
        while (g.MeasureString(text, schrift).Width > breite && groesse > 5)
        {
            groesse -= 0.5;
            schrift = new XFont("Arial", groesse, stil);
        }

        while (g.MeasureString(text, schrift).Width > breite && text.Length > 2)
        {
            text = text[..^2].TrimEnd() + "…";
        }

        g.DrawString(text, schrift, new XSolidBrush(Tinte), x, grundlinie, XStringFormats.BaseLineLeft);
    }

    private static string Wert(Signatur? basis, string feld, string ersatz) => basis is null ? ersatz : Signaturinhalt.Feld(basis.Inhalt, feld) ?? ersatz;

    private Document Beiblatt(AntragAnsicht a)
    {
        var antrag = a.Antrag;
        var dok = new Document();
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
        Fusszeile(s, a, "Beiblatt Seite ");

        var kopf = s.AddParagraph(a.Hochschule);
        kopf.Format.Font.Size = 9;
        kopf.Format.Font.Color = Colors.DimGray;
        var titel = s.AddParagraph($"Beiblatt zum Antrag {antrag.Nummer}");
        titel.Format.Font.Size = 16;
        titel.Format.Font.Bold = true;
        titel.Format.SpaceBefore = Unit.FromPoint(6);
        var unter = s.AddParagraph($"Antrag auf mündliche Wiederholungsprüfung (2. Wiederholung, 3. Versuch) · {antrag.Name} · {a.Pruefung.Anzeige}");
        unter.Format.Font.Size = 9;
        unter.Format.SpaceAfter = Unit.FromPoint(12);

        Abschnitt(s, "Ergänzende Angaben");
        Tabelle(s,
            ("Terminliche Einschränkungen und Wünsche der/des Studierenden", antrag.Terminwuensche ?? "–"),
            ("Termin", a.Termin ?? "noch offen"),
            ("Ort", antrag.Ort ?? "noch offen"),
            ("Erstprüfende/r", a.Erstpruefende.Anzeigename),
            ("Zweitprüfende/r", antrag.ZweitpruefendeName ?? "noch offen"),
            ("Bemerkung", antrag.Bemerkung ?? "–"),
            ("Stand", antrag.Status.Anzeigename()));

        if (antrag.Status is AntragStatus.ExternWeiterleiten or AntragStatus.Weitergeleitet)
        {
            var extern_ = s.AddParagraph("Das Studiendekanat unterschreibt außerhalb des ProfOMat: bitte auf Seite 1 unterschreiben und das Formular an "
                                         + $"das Studierendensekretariat ({a.SekretariatEmail}) geben.");
            extern_.Format.SpaceBefore = Unit.FromPoint(8);
            extern_.Format.Font.Bold = true;
        }

        Abschnitt(s, "Unterschriften und Prüfprotokoll");
        foreach (var befund in a.Signaturen.Where(b => b.Signatur.Ueberholt is null))
        {
            Unterschrift(s, befund);
        }

        var hinweis = s.AddParagraph(
            "Hinweis: Dieses Dokument ist die Ausfertigung eines elektronisch geführten Vorgangs. Die Unterschriften auf Seite 1 wurden im "
            + "ProfOMat geleistet (gezeichnet oder mit eigenem Schlüssel digital signiert). Jede bezieht sich auf den vollständigen Stand "
            + "einschließlich der vorherigen Unterschrift (Hash-Kette) und ist mit dem Systemsiegel des ProfOMat versehen. Maßgeblich und "
            + "jederzeit prüfbar sind die im ProfOMat gespeicherten Unterschriften (QR-Code bzw. Link in der Fußzeile).");
        hinweis.Format.Font.Size = 8;
        hinweis.Format.Font.Color = Colors.DimGray;
        hinweis.Format.SpaceBefore = Unit.FromPoint(10);

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

        return dok;
    }
}
