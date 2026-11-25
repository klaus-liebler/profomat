using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProfOMat.Domain.Finanzen;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Application.Finanzen;

/// <summary>Ein Antrag aus der Beschlussliste der Investitionsanträge, wie er übernommen würde.</summary>
/// <param name="TopfId">Vorhandener Topf des Teams; null = neu (nur mit Auswahl angelegt).</param>
/// <param name="Aenderungen">Was sich am vorhandenen Topf ändert.</param>
public sealed record Antragsvorschau(string Antragsnummer, string? Innenauftrag, string? Antragsteller, string Investition, decimal Sachkosten,
    Anschaffungsart? Anschaffung, string? Laborbereich, Guid? TopfId, IReadOnlyList<string> Aenderungen);

/// <param name="Uebrige">Anträge anderer Personen (nur gezählt, weder angezeigt noch gespeichert).</param>
public sealed record Antragsimport(DateOnly? Beschluss, IReadOnlyList<Antragsvorschau> Antraege, int Uebrige);

/// <summary>
/// Beschlussliste der Investitionsanträge als JSON aus einem KI-Chat (FA-719, Format <c>profomat-investitionsantraege-v1</c>): Antragsnummer,
/// Innenauftrag, Antragsteller, Verwendungszweck, Sachkosten, Neu/Ersatz und Begründung je Antrag.
/// </summary>
public static partial class InvestitionsantraegeJson
{
    public const string Format = "profomat-investitionsantraege-v1";

    public static string Prompt(string? laborbereich) => $$"""
        Im angehängten PDF steht die Liste der Investitionsanträge der Fakultät (Spalten: Lfd. Nr., Innenauftrag, Antragsteller/verantwortlich,
        Investition (Kurzbeschreibung), Sachkosten, Neu, Ersatz, Begründung). Erstelle daraus ausschließlich ein JSON-Objekt (ohne Erklärungen,
        ohne Markdown) mit genau dieser Struktur:

        {
          "format": "{{Format}}",
          "beschluss": "JJJJ-MM-TT",
          "antraege": [
            {
              "antragsnummer": "AUT-07-26",
              "innenauftrag": "71211637",
              "laborbereich": "Automatisierungstechnik",
              "antragsteller": "Liebler",
              "investition": "Erstellung von Lehraufbauten für das Fach Embedded Systems: …",
              "sachkosten": 3200.00,
              "art": "Neu",
              "begruendung": "…"
            }
          ]
        }

        Regeln:
        - "beschluss": Datum des Fakultätsratsbeschlusses aus dem Kopf („genehmigt per Fakultätsratsbeschluss vom …“).
        - Je Antrag (Lfd. Nr. wie im PDF, z. B. AUT-07-26) ein Eintrag{{(string.IsNullOrWhiteSpace(laborbereich) ? "" : $"; nur der Laborbereich „{laborbereich}“")}}.
        - Lfd. Nr. und Innenauftrag stehen in derselben Tabellenzeile (z. B. „AUT-07-26 71211637“); die Texte der übrigen Spalten sind oft über mehrere
          Zeilen umbrochen – Zeilen derselben Zelle zusammenfügen, Silbentrennungen am Zeilenende auflösen.
        - "antragsteller": alle Namen wie im PDF, mehrere mit „ / “ getrennt (z. B. „Liebler / Niemeyer / Rehm“).
        - "laborbereich": aus der Zwischenüberschrift bzw. dem Kürzel der Lfd. Nr. (AUT = Automatisierungstechnik, AI = Allgemeine Informatik usw.).
        - "investition": die Kurzbeschreibung vollständig; "begruendung": die Spalte „Begründung und Was passiert, wenn die Investition nicht getätigt
          werden kann?“ vollständig.
        - "sachkosten": Betrag als Zahl mit Punkt als Dezimaltrennzeichen, ohne Tausenderpunkte und ohne „€“ (Summenzeilen weglassen).
        - "art": „Neu“ oder „Ersatz“ (je nachdem, welche Spalte angekreuzt ist), sonst weglassen.
        """;

    public sealed record Eintrag(string Antragsnummer, string? Innenauftrag, string? Laborbereich, string? Antragsteller, string Investition, decimal Sachkosten,
        Anschaffungsart? Anschaffung, string? Begruendung);

    public sealed record Inhalt(DateOnly? Beschluss, IReadOnlyList<Eintrag> Antraege);

    public static Inhalt Lesen(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FachlicherFehler("Bitte das JSON aus dem KI-Chat einfügen.");
        }

        if (json.Length > InvestitionenJson.MaxZeichen)
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
                throw new FachlicherFehler($"Falsches Format – erwartet \"format\": \"{Format}\". Bitte den Prompt für die Investitionsanträge verwenden.");
            }

            if (!w.TryGetProperty("antraege", out var liste) || liste.ValueKind != JsonValueKind.Array)
            {
                throw new FachlicherFehler("\"antraege\" fehlt.");
            }

            var eintraege = new List<Eintrag>();
            foreach (var a in liste.EnumerateArray())
            {
                eintraege.Add(EintragLesen(a));
            }

            if (eintraege.GroupBy(e => e.Antragsnummer).FirstOrDefault(g => g.Count() > 1) is { } doppelt)
            {
                throw new FachlicherFehler($"{doppelt.Key} steht mehrfach im JSON.");
            }

            var beschluss = DateOnly.TryParseExact(Text(w, "beschluss") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bd) ? bd : (DateOnly?)null;
            return new(beschluss, eintraege);
        }
    }

    private static Eintrag EintragLesen(JsonElement a)
    {
        var nr = Text(a, "antragsnummer")?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(nr) || !Antragsnummer().IsMatch(nr))
        {
            throw new FachlicherFehler($"Ein Antrag hat keine gültige Antragsnummer (z. B. AUT-07-26): „{nr}“.");
        }

        var investition = Text(a, "investition")?.Trim();
        if (string.IsNullOrEmpty(investition))
        {
            throw new FachlicherFehler($"{nr}: \"investition\" (Kurzbeschreibung) fehlt.");
        }

        var ia = Text(a, "innenauftrag")?.Trim();
        if (ia is { Length: > 0 } && !Innenauftrag().IsMatch(ia))
        {
            throw new FachlicherFehler($"{nr}: Innenauftrag „{ia}“ ist keine 8-stellige Nummer.");
        }

        var art = Text(a, "art")?.Trim().ToUpperInvariant() switch
        {
            "NEU" => Anschaffungsart.Neu,
            "ERSATZ" => Anschaffungsart.Ersatz,
            _ => (Anschaffungsart?)null,
        };

        decimal betrag;
        if (a.TryGetProperty("sachkosten", out var b) && b.ValueKind == JsonValueKind.Number)
        {
            betrag = b.GetDecimal();
        }
        else if (a.TryGetProperty("sachkosten", out var b2) && b2.ValueKind == JsonValueKind.String
                 && decimal.TryParse(b2.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
        {
            betrag = d;
        }
        else
        {
            throw new FachlicherFehler($"{nr}: \"sachkosten\" fehlt oder ist keine Zahl.");
        }

        if (betrag < 0)
        {
            throw new FachlicherFehler($"{nr}: Sachkosten sind negativ.");
        }

        return new(nr, ia is { Length: > 0 } ? ia : null, Text(a, "laborbereich")?.Trim(), Text(a, "antragsteller")?.Trim(), investition,
            decimal.Round(betrag, 2), art, Text(a, "begruendung")?.Trim());
    }

    private static string? Text(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex("^[A-Z]{1,6}-[0-9]{1,3}[A-Z]?-[0-9]{2}$")]
    private static partial Regex Antragsnummer();

    [GeneratedRegex("^[0-9]{8}$")]
    private static partial Regex Innenauftrag();
}
