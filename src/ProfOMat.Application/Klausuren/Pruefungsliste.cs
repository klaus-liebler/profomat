using System.Text;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Klausuren;

namespace ProfOMat.Application.Klausuren;

/// <summary>
/// Eingelesene Prüfungsliste (CSV-Export aus dem Hochschulsystem). <see cref="Angebot"/> nennt alle Prüfungen der Datei; enthält sie
/// mehrere und wurde keine gewählt, ist <see cref="AuswahlNoetig"/> gesetzt und <see cref="Zeilen"/> leer.
/// </summary>
public sealed record Pruefungsliste(string? Pruefungsnummer, string? Pruefungsname, string? Pruefer, IReadOnlyList<Listenzeile> Zeilen, IReadOnlyList<string> Hinweise,
    string Format = PruefungslisteLeser.FormatOsnabrueck, IReadOnlyList<Pruefungsangebot>? Angebot = null)
{
    public IReadOnlyList<Pruefungsangebot> Angebot { get; init; } = Angebot ?? [];

    public bool AuswahlNoetig => Pruefungsnummer is null && Angebot.Count > 1;
}

/// <summary>Eine Prüfung in einer Prüfungsliste mit mehreren Prüfungen (Format „Hochschule Osnabrück“).</summary>
public sealed record Pruefungsangebot(string Nummer, string? Name, string? Pruefer, int Teilnehmende)
{
    public string Anzeige => $"{Nummer} – {Name ?? "ohne Namen"} ({Teilnehmende} Teilnehmende)";
}

/// <summary>
/// Liest die CSV-Prüfungsliste des Hochschulsystems. Spalten über die Kopfzeile (Reihenfolge egal):
/// <c>pruefer, exam_name, pruefungsname, pruefungsnummer, row, matriculation_number1, first_name1, first_name, hochschulmail</c>.
/// Achtung: <c>first_name1</c> ist der Nachname, <c>first_name</c> der Vorname. Deutsche Spaltennamen werden ebenfalls erkannt.
/// Trennzeichen (Komma, Semikolon, Tab) und Kodierung (UTF-8, sonst Windows-1252) werden erkannt.
/// <para>Zweites Format „GA-Master“ (erkannt an der Spalte <c>Kohorte</c> bzw. <c>FH Münster E-Mail Adresse</c>):
/// <c>Kohorte;Vorname;Name;FH Münster E-Mail Adresse;Pers_email1;Pers_email2</c>. Es enthält keine Matrikelnummer; als solche dient
/// der Teil der FH-Mailadresse vor dem @ (z. B. <c>eb123456</c>). Private Mailadressen werden nicht übernommen.</para>
/// </summary>
public static class PruefungslisteLeser
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public const string FormatOsnabrueck = "Hochschule Osnabrück";

    public const string FormatGaMaster = "GA-Master";

    /// <summary>Download der Kandidatenliste im Format „Hochschule Osnabrück“ (Berichtsserver, CSV; Anmeldung mit Hochschulkonto).</summary>
    public const string DownloadOsnabrueck =
        "https://reporting.hs-osnabrueck.de/ReportServer?/Reports/Lehrende/KandidatenlisteMail&rs:Command=Render&rs:Format=CSV&rc:OutputFormat=CSV&rv:ParamMode=Collapsed";

    private static readonly Dictionary<string, string[]> Spalten = new()
    {
        ["matrikel"] = ["matriculation_number1", "matriculation_number", "matrikelnummer", "matrnr", "matnr"],
        ["nachname"] = ["first_name1", "nachname", "last_name", "name"],
        ["vorname"] = ["first_name", "vorname"],
        ["mail"] = ["hochschulmail", "email", "e-mail", "mail"],
        ["zeile"] = ["row", "nr", "lfd"],
        ["nummer"] = ["pruefungsnummer", "prüfungsnummer", "exam_number"],
        ["pname"] = ["pruefungsname", "prüfungsname"],
        ["pruefer"] = ["pruefer", "prüfer"],
    };

    /// <param name="inhalt">CSV-Datei.</param>
    /// <param name="pruefungsnummer">Gewählte Prüfung, wenn die Datei mehrere enthält; nur deren Zeilen werden gelesen.</param>
    public static Pruefungsliste Lesen(byte[] inhalt, string? pruefungsnummer = null)
    {
        ArgumentNullException.ThrowIfNull(inhalt);
        if (inhalt.Length == 0)
        {
            throw new FachlicherFehler("Die Datei ist leer.");
        }

        if (inhalt.Length > MaxBytes)
        {
            throw new FachlicherFehler("Die Datei ist zu groß (höchstens 2 MB).");
        }

        var text = Dekodieren(inhalt);
        var zeilen = CsvZeilen(text).Where(z => z.Any(f => f.Length > 0)).ToList();
        if (zeilen.Count < 2)
        {
            throw new FachlicherFehler("Die Datei enthält keine Teilnehmenden.");
        }

        var kopf = zeilen[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        if (kopf.Contains("kohorte") || kopf.Any(h => h.Contains("münster", StringComparison.Ordinal)))
        {
            return GaMasterLesen(kopf, zeilen);
        }

        int Spalte(string schluessel) => Spalten[schluessel].Select(n => kopf.IndexOf(n)).FirstOrDefault(i => i >= 0, -1);
        var (iMatrikel, iNach, iVor) = (Spalte("matrikel"), Spalte("nachname"), Spalte("vorname"));
        if (iMatrikel < 0 || iNach < 0 || iVor < 0)
        {
            throw new FachlicherFehler($"Spalten nicht erkannt. Erwartet werden matriculation_number1, first_name1 (Nachname) und first_name (Vorname); gefunden: {string.Join(", ", kopf)}.");
        }

        var (iMail, iZeile, iNummer, iName, iPruefer) = (Spalte("mail"), Spalte("zeile"), Spalte("nummer"), Spalte("pname"), Spalte("pruefer"));
        string Feld(string[] z, int i) => i >= 0 && i < z.Length ? z[i].Trim() : string.Empty;

        string? Wert(string[] z, int i) => Feld(z, i) is { Length: > 0 } w ? w : null;

        // Prüfungen der Datei in der Reihenfolge ihres ersten Auftretens (Name und Prüfer aus deren erster Zeile)
        var datenzeilen = Enumerable.Range(1, zeilen.Count - 1).Select(n => (N: n, Z: zeilen[n], Nummer: Feld(zeilen[n], iNummer))).ToList();
        var angebot = datenzeilen.Where(d => d.Nummer.Length > 0).GroupBy(d => d.Nummer, StringComparer.Ordinal)
            .Select(g => new Pruefungsangebot(g.Key, Wert(g.First().Z, iName), Wert(g.First().Z, iPruefer), g.Count())).ToList();

        if (pruefungsnummer is not null)
        {
            if (angebot.All(a => a.Nummer != pruefungsnummer))
            {
                var enthalten = angebot.Count == 0 ? "keine Prüfungsnummer" : string.Join(", ", angebot.Select(a => a.Nummer));
                throw new FachlicherFehler($"Die Datei enthält die Prüfung {pruefungsnummer} nicht (enthalten: {enthalten}).");
            }

            datenzeilen = datenzeilen.Where(d => d.Nummer == pruefungsnummer).ToList();
        }
        else if (angebot.Count > 1)
        {
            return new Pruefungsliste(null, null, null, [], [], FormatOsnabrueck, angebot);
        }

        var hinweise = new List<string>();
        var ergebnis = new List<Listenzeile>();
        foreach (var (n, z, _) in datenzeilen)
        {
            var matrikel = Feld(z, iMatrikel);
            if (matrikel.Length is < 5 or > 10 || !matrikel.All(char.IsAsciiDigit))
            {
                hinweise.Add($"Zeile {n + 1} übersprungen: Matrikelnummer „{matrikel}“ ungültig.");
                continue;
            }

            var zeile = int.TryParse(Feld(z, iZeile), out var r) ? r : n;
            ergebnis.Add(new Listenzeile(zeile, matrikel, Feld(z, iNach), Feld(z, iVor), Wert(z, iMail)));
        }

        var erste = datenzeilen[0].Z;
        return new Pruefungsliste(pruefungsnummer ?? angebot.SingleOrDefault()?.Nummer, Wert(erste, iName), Wert(erste, iPruefer), ergebnis, hinweise,
            FormatOsnabrueck, angebot);
    }

    private static Pruefungsliste GaMasterLesen(List<string> kopf, List<string[]> zeilen)
    {
        var iVor = kopf.IndexOf("vorname");
        var iNach = new[] { "name", "nachname" }.Select(h => kopf.IndexOf(h)).FirstOrDefault(i => i >= 0, -1);
        var iMail = kopf.FindIndex(h => h.StartsWith("fh", StringComparison.Ordinal) && h.Contains("mail", StringComparison.Ordinal));
        if (iVor < 0 || iNach < 0 || iMail < 0)
        {
            throw new FachlicherFehler($"Spalten nicht erkannt (Format {FormatGaMaster}). Erwartet werden Vorname, Name und FH Münster E-Mail Adresse; gefunden: {string.Join(", ", kopf)}.");
        }

        string Feld(string[] z, int i) => i < z.Length ? z[i].Trim() : string.Empty;
        var hinweise = new List<string>();
        var ergebnis = new List<Listenzeile>();
        for (var n = 1; n < zeilen.Count; n++)
        {
            var z = zeilen[n];
            var mail = Feld(z, iMail);
            var at = mail.IndexOf('@', StringComparison.Ordinal);
            var kennung = at > 0 ? mail[..at].ToLowerInvariant() : string.Empty;
            if (kennung.Length is < 3 or > 20 || !kennung.All(char.IsAsciiLetterOrDigit))
            {
                hinweise.Add($"Zeile {n + 1} übersprungen: FH-Mailadresse „{mail}“ ergibt keine gültige Kennung.");
                continue;
            }

            ergebnis.Add(new Listenzeile(n, kennung, Feld(z, iNach), Feld(z, iVor), mail));
        }

        return new Pruefungsliste(null, null, null, ergebnis, hinweise, FormatGaMaster);
    }

    private static string Dekodieren(byte[] inhalt)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(inhalt).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(inhalt);
        }
    }

    /// <summary>CSV nach RFC 4180 (Anführungszeichen, Zeilenumbrüche in Feldern); Trennzeichen aus der Kopfzeile.</summary>
    internal static IEnumerable<string[]> CsvZeilen(string text)
    {
        var ersteZeile = text.Split('\n')[0];
        var trenner = new[] { ';', ',', '\t' }.OrderByDescending(c => ersteZeile.Count(x => x == c)).First();
        var felder = new List<string>();
        var feld = new StringBuilder();
        var inZitat = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inZitat)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    feld.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inZitat = false;
                }
                else
                {
                    feld.Append(c);
                }
            }
            else if (c == '"')
            {
                inZitat = true;
            }
            else if (c == trenner)
            {
                felder.Add(feld.ToString());
                feld.Clear();
            }
            else if (c == '\n')
            {
                felder.Add(feld.ToString().TrimEnd('\r'));
                feld.Clear();
                yield return felder.ToArray();
                felder.Clear();
            }
            else
            {
                feld.Append(c);
            }
        }

        if (feld.Length > 0 || felder.Count > 0)
        {
            felder.Add(feld.ToString().TrimEnd('\r'));
            yield return felder.ToArray();
        }
    }
}

/// <summary>CSV-Ausgaben für Word-Serienbrief und Notenmeldung (Semikolon, UTF-8 mit BOM, CRLF – so öffnen Word und Excel sie ohne Rückfrage).</summary>
public static class CsvSchreiber
{
    public static byte[] Schreiben(IEnumerable<string> kopf, IEnumerable<IEnumerable<string?>> zeilen)
    {
        ArgumentNullException.ThrowIfNull(kopf);
        ArgumentNullException.ThrowIfNull(zeilen);
        var sb = new StringBuilder();
        sb.Append(string.Join(';', kopf.Select(Feld))).Append("\r\n");
        foreach (var z in zeilen)
        {
            sb.Append(string.Join(';', z.Select(Feld))).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Feld(string? wert)
    {
        var w = wert ?? string.Empty;
        // Formel-Injektion in Excel verhindern
        if (w.Length > 0 && "=+-@".Contains(w[0], StringComparison.Ordinal) && !decimal.TryParse(w, out _))
        {
            w = "'" + w;
        }

        return w.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{w.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : w;
    }
}
