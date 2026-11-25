using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Application.Finanzen;

/// <summary>Eine Zeile der SQM-Liste der Fakultät (ein Antrag).</summary>
public sealed record SqmZeile(string Blatt, string Antragsnummer, string Antragsteller, string Bezeichnung, string? Laufzeit, decimal Personal, decimal Sach,
    string? Kategorie, decimal Gesamt, decimal? Verausgabt);

/// <param name="Stand">Änderungsdatum der Datei (falls in den Dateieigenschaften).</param>
public sealed record SqmListe(IReadOnlyList<SqmZeile> Zeilen, DateOnly? Stand);

/// <summary>Liest „SQM-…-bewilligt-aktuell.xlsx“ (Infrastruktur: ClosedXML).</summary>
public interface ISqmListenLeser
{
    SqmListe Lesen(byte[] inhalt);
}

/// <summary>Was die Übernahme einer Maßnahme ändern würde.</summary>
/// <param name="TopfId">Vorhandener Topf des Teams; null = neu (nur mit Auswahl angelegt).</param>
/// <param name="Bewilligt">Bewilligt laut Liste.</param>
/// <param name="BewilligtBisher">Bewilligt im Topf (null bei neuen).</param>
/// <param name="Ausgegeben">Verausgabt laut Liste (null = keine Angabe).</param>
/// <param name="AusgegebenBisher">Letzter Stand im Topf.</param>
public sealed record Massnahmenvorschau(string Antragsnummer, string Bezeichnung, string? Antragsteller, Guid? TopfId, decimal Bewilligt, decimal? BewilligtBisher,
    decimal? Ausgegeben, decimal? AusgegebenBisher, IReadOnlyList<string> Hinweise);

/// <param name="Uebrige">Anzahl Anträge anderer Personen bzw. Laborbereiche (nicht angezeigt, nicht gespeichert).</param>
public sealed record Massnahmenimport(DateOnly Stichtag, IReadOnlyList<Massnahmenvorschau> Massnahmen, int Uebrige, IReadOnlyList<string> Fehler);

/// <summary>Stand der Investitionsmaßnahmen als JSON aus einem KI-Chat (FA-703, Format <c>profomat-investitionen-v1</c>).</summary>
public static partial class InvestitionenJson
{
    public const string Format = "profomat-investitionen-v1";

    public const int MaxZeichen = 500_000;

    /// <summary>Prompt für den KI-Chat; wird zusammen mit dem PDF „Stand Investitionen“ der Fakultät eingegeben.</summary>
    public static string Prompt(string? laborbereich) => $$"""
        Im angehängten PDF steht der Stand der Investitionsmaßnahmen der Fakultät (SAP-Auszug je Maßnahme, z. B. „AUT-07-26“).
        Erstelle daraus ausschließlich ein JSON-Objekt (ohne Erklärungen, ohne Markdown) mit genau dieser Struktur:

        {
          "format": "{{Format}}",
          "stichtag": "JJJJ-MM-TT",
          "massnahmen": [
            {
              "antragsnummer": "AUT-07-26",
              "laborbereich": "Automatisierungstechnik",
              "budget": 3200.00,
              "ausgegeben": 0.00,
              "rest": 3200.00,
              "buchungen": [ { "datum": "JJJJ-MM-TT", "kostenart": "980800", "betrag": 7482.36, "text": "…" } ]
            }
          ]
        }

        Regeln:
        - "stichtag": das Datum des Stands (aus Titel oder Dateiname, z. B. „271026“ = 2026-10-27).
        - Je Maßnahme (Antragsnummer wie im PDF) ein Eintrag{{(string.IsNullOrWhiteSpace(laborbereich) ? "" : $"; nur der Laborbereich „{laborbereich}“")}}.
        - "budget": der bewilligte Betrag aus der Budgetzeile (Kostenart 930300 „Budget Sachmitte Fak“), einmal, ohne Vorzeichen. Die Budgetzeile steht
          im Auszug oft mehrfach mit wechselndem Vorzeichen – sie ist keine Ausgabe.
        - "buchungen": alle übrigen Zeilen der Maßnahme mit ihrem Betrag wie im PDF (positiv = Ausgabe, z. B. Kostenart 980800 oder 930100).
        - "ausgegeben": Summe der Buchungen. "rest": budget − ausgegeben. Im PDF steht am Ende jeder Maßnahme der Saldo mit umgekehrtem Vorzeichen
          (negativ = noch verfügbar), z. B. „-3.200,00“ → "rest": 3200.00.
        - Beträge als Zahl mit Punkt als Dezimaltrennzeichen, ohne Tausenderpunkte und ohne „€“.
        """;

    public sealed record Buchungszeile(DateOnly? Datum, string? Kostenart, decimal Betrag, string? Text);

    public sealed record Eintrag(string Antragsnummer, string? Laborbereich, decimal Budget, decimal Ausgegeben, decimal Rest, IReadOnlyList<Buchungszeile> Buchungen);

    public sealed record Inhalt(DateOnly Stichtag, IReadOnlyList<Eintrag> Massnahmen);

    /// <summary>Liest das JSON (auch in ```-Blöcken oder mit Text drumherum); wirft mit verständlicher Meldung.</summary>
    public static Inhalt Lesen(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FachlicherFehler("Bitte das JSON aus dem KI-Chat einfügen.");
        }

        if (json.Length > MaxZeichen)
        {
            throw new FachlicherFehler("Das eingefügte JSON ist zu lang.");
        }

        var start = json.IndexOf('{', StringComparison.Ordinal);
        var ende = json.LastIndexOf('}');
        if (start < 0 || ende <= start)
        {
            throw new FachlicherFehler("Im eingefügten Text steht kein JSON-Objekt ({ … }).");
        }

        JsonDocument dok;
        try
        {
            dok = JsonDocument.Parse(json[start..(ende + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new FachlicherFehler($"Das JSON ist nicht lesbar: {ex.Message}", ex);
        }

        using (dok)
        {
            var w = dok.RootElement;
            if (Text(w, "format") != Format)
            {
                throw new FachlicherFehler($"Falsches Format – erwartet \"format\": \"{Format}\". Bitte den Prompt aus ProfOMat verwenden.");
            }

            var stichtag = Datum(Text(w, "stichtag")) ?? throw new FachlicherFehler("\"stichtag\" fehlt oder ist kein Datum (JJJJ-MM-TT).");
            if (!w.TryGetProperty("massnahmen", out var liste) || liste.ValueKind != JsonValueKind.Array)
            {
                throw new FachlicherFehler("\"massnahmen\" fehlt.");
            }

            var eintraege = new List<Eintrag>();
            foreach (var m in liste.EnumerateArray())
            {
                var nr = Text(m, "antragsnummer")?.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(nr) || !Antragsnummer().IsMatch(nr))
                {
                    throw new FachlicherFehler($"Eine Maßnahme hat keine gültige Antragsnummer (z. B. AUT-07-26): „{nr}“.");
                }

                var buchungen = new List<Buchungszeile>();
                if (m.TryGetProperty("buchungen", out var b) && b.ValueKind == JsonValueKind.Array)
                {
                    buchungen.AddRange(b.EnumerateArray().Select(x => new Buchungszeile(Datum(Text(x, "datum")), Text(x, "kostenart"), Zahl(x, "betrag", nr), Text(x, "text"))));
                }

                eintraege.Add(new(nr, Text(m, "laborbereich"), Zahl(m, "budget", nr), Zahl(m, "ausgegeben", nr), Zahl(m, "rest", nr), buchungen));
            }

            if (eintraege.GroupBy(e => e.Antragsnummer).FirstOrDefault(g => g.Count() > 1) is { } doppelt)
            {
                throw new FachlicherFehler($"{doppelt.Key} steht mehrfach im JSON.");
            }

            return new(stichtag, eintraege);
        }
    }

    /// <summary>Prüft die Summen einer Maßnahme (auf 1 ct); Abweichungen werden gezeigt, nicht still korrigiert.</summary>
    public static IReadOnlyList<string> Pruefen(Eintrag e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var liste = new List<string>();
        if (Math.Abs(e.Budget - e.Ausgegeben - e.Rest) > 0.01m)
        {
            liste.Add($"budget − ausgegeben ({Geld.Text(e.Budget - e.Ausgegeben)}) ≠ rest ({Geld.Text(e.Rest)})");
        }

        if (e.Buchungen.Count > 0 && Math.Abs(e.Buchungen.Sum(x => x.Betrag) - e.Ausgegeben) > 0.01m)
        {
            liste.Add($"Summe der Buchungen ({Geld.Text(e.Buchungen.Sum(x => x.Betrag))}) ≠ ausgegeben ({Geld.Text(e.Ausgegeben)})");
        }

        if (e.Budget < 0)
        {
            liste.Add("Das Budget ist negativ – im PDF ist es positiv anzugeben.");
        }

        return liste;
    }

    private static string? Text(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateOnly? Datum(string? s)
        => DateOnly.TryParseExact(s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static decimal Zahl(JsonElement e, string name, string nr)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return name == "betrag" ? throw new FachlicherFehler($"{nr}: eine Buchung ohne \"betrag\".") : throw new FachlicherFehler($"{nr}: \"{name}\" fehlt.");
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => throw new FachlicherFehler($"{nr}: \"{name}\" ist keine Zahl."),
        };
    }

    [GeneratedRegex("^[A-Z]{1,6}-[0-9]{1,3}[A-Z]?-[0-9]{2}$")]
    private static partial Regex Antragsnummer();
}
