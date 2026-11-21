using System.Text;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Klausuren;

namespace ProfOMat.Application.Klausuren;

/// <summary>Eingelesene Prüfungsliste (CSV-Export aus dem Hochschulsystem).</summary>
public sealed record Pruefungsliste(string? Pruefungsnummer, string? Pruefungsname, string? Pruefer, IReadOnlyList<Listenzeile> Zeilen, IReadOnlyList<string> Hinweise);

/// <summary>
/// Liest die CSV-Prüfungsliste des Hochschulsystems. Spalten über die Kopfzeile (Reihenfolge egal):
/// <c>pruefer, exam_name, pruefungsname, pruefungsnummer, row, matriculation_number1, first_name1, first_name, hochschulmail</c>.
/// Achtung: <c>first_name1</c> ist der Nachname, <c>first_name</c> der Vorname. Deutsche Spaltennamen werden ebenfalls erkannt.
/// Trennzeichen (Komma, Semikolon, Tab) und Kodierung (UTF-8, sonst Windows-1252) werden erkannt.
/// </summary>
public static class PruefungslisteLeser
{
    public const int MaxBytes = 2 * 1024 * 1024;

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

    public static Pruefungsliste Lesen(byte[] inhalt)
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
        int Spalte(string schluessel) => Spalten[schluessel].Select(n => kopf.IndexOf(n)).FirstOrDefault(i => i >= 0, -1);
        var (iMatrikel, iNach, iVor) = (Spalte("matrikel"), Spalte("nachname"), Spalte("vorname"));
        if (iMatrikel < 0 || iNach < 0 || iVor < 0)
        {
            throw new FachlicherFehler($"Spalten nicht erkannt. Erwartet werden matriculation_number1, first_name1 (Nachname) und first_name (Vorname); gefunden: {string.Join(", ", kopf)}.");
        }

        var (iMail, iZeile, iNummer, iName, iPruefer) = (Spalte("mail"), Spalte("zeile"), Spalte("nummer"), Spalte("pname"), Spalte("pruefer"));
        string Feld(string[] z, int i) => i >= 0 && i < z.Length ? z[i].Trim() : string.Empty;

        var hinweise = new List<string>();
        var ergebnis = new List<Listenzeile>();
        var nummern = new HashSet<string>(StringComparer.Ordinal);
        for (var n = 1; n < zeilen.Count; n++)
        {
            var z = zeilen[n];
            var matrikel = Feld(z, iMatrikel);
            if (matrikel.Length is < 5 or > 10 || !matrikel.All(char.IsAsciiDigit))
            {
                hinweise.Add($"Zeile {n + 1} übersprungen: Matrikelnummer „{matrikel}“ ungültig.");
                continue;
            }

            if (Feld(z, iNummer) is { Length: > 0 } nr)
            {
                nummern.Add(nr);
            }

            var zeile = int.TryParse(Feld(z, iZeile), out var r) ? r : n;
            ergebnis.Add(new Listenzeile(zeile, matrikel, Feld(z, iNach), Feld(z, iVor), Feld(z, iMail) is { Length: > 0 } m ? m : null));
        }

        if (nummern.Count > 1)
        {
            throw new FachlicherFehler($"Die Datei enthält mehrere Prüfungen ({string.Join(", ", nummern)}). Bitte je Prüfung eine Datei hochladen.");
        }

        var erste = zeilen[1];
        return new Pruefungsliste(nummern.FirstOrDefault(), Feld(erste, iName) is { Length: > 0 } pn ? pn : null,
            Feld(erste, iPruefer) is { Length: > 0 } pr ? pr : null, ergebnis, hinweise);
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
