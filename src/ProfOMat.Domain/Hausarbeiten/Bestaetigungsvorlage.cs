using System.Text.RegularExpressions;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Hausarbeiten;

/// <summary>Platzhalter der Mail zur Themenbestätigung (FA-613); <c>{Link}</c> ist Pflicht.</summary>
public static partial class Bestaetigungsplatzhalter
{
    public static IReadOnlyDictionary<string, string> Beschreibung { get; } = new Dictionary<string, string>
    {
        ["Name"] = "Vor- und Nachname der Person",
        ["Vorname"] = "Vorname",
        ["Nachname"] = "Nachname",
        ["Zuteilung"] = "„Ihnen“ bzw. „Ihrem Team (mit …)“",
        ["Team"] = "alle Mitglieder des Teams",
        ["Teamnummer"] = "Nummer des Teams",
        ["Thema"] = "Nummer, Titel und Art des Themas",
        ["Beschreibung"] = "Beschreibung des Themas (leer, wenn keine)",
        ["Link"] = "persönlicher Bestätigungslink (Pflicht)",
        ["Frist"] = "Datum der Bestätigungsfrist (oder „–“)",
        ["BisFrist"] = "„ bis zum TT.MM.JJJJ“ bzw. leer ohne Frist",
        ["Modul"] = "Titel der Hausarbeit",
        ["Absender"] = "Ihr Name",
    };

    public static IReadOnlyList<string> Unbekannte(string? text)
        => [.. Platzhalter().Matches(text ?? "").Select(m => m.Groups[1].Value).Where(n => !Beschreibung.ContainsKey(n)).Distinct()];

    /// <summary>Ersetzt die Platzhalter; mehr als eine Leerzeile (z. B. bei leerer {Beschreibung}) wird zu einer.</summary>
    public static string Ersetzen(string text, IReadOnlyDictionary<string, string> werte)
        => MehrfacheLeerzeilen().Replace(Terminplatzhalter.Ersetzen(text.ReplaceLineEndings("\n"), werte), "\n\n");

    [GeneratedRegex(@"\{([A-Za-zÄÖÜäöüß]+)\}")]
    private static partial Regex Platzhalter();

    [GeneratedRegex(@"\n[ \t]*\n([ \t]*\n)+")]
    private static partial Regex MehrfacheLeerzeilen();
}

/// <summary>Vorlage der Mail zur Themenbestätigung (FA-613).</summary>
public sealed partial class Hausarbeit
{
    public const string StandardBestaetigungBetreff = "Hausarbeit {Modul}: Ihr Thema – bitte{BisFrist} bestätigen";

    public const string StandardBestaetigungVorlage = """
        Guten Tag {Name},

        für die Hausarbeit im Modul „{Modul}“ habe ich {Zuteilung} das folgende Thema zugeteilt:

        {Thema}

        {Beschreibung}

        Bitte bestätigen Sie die Annahme des Themas{BisFrist} über Ihren persönlichen Link:
        {Link}

        Bei der Bestätigung können Sie auch Ihre terminlichen Einschränkungen für das Erörterungsgespräch angeben (zum Beispiel Tage oder Zeiten, an denen Sie nicht können). Das hilft bei der Terminplanung; die Angabe lässt sich später über denselben Link ändern.

        Jedes Teammitglied bestätigt einzeln; der Link gilt nur für Sie. Wird das Thema nicht rechtzeitig bestätigt, kann dies zum Ausschluss von der Prüfung führen.

        Viele Grüße
        {Absender}
        """;

    public string? BestaetigungBetreff { get; private set; }

    public string? BestaetigungVorlage { get; private set; }

    public string BestaetigungBetreffVorlage => BestaetigungBetreff ?? StandardBestaetigungBetreff;

    public string BestaetigungTextVorlage => BestaetigungVorlage ?? StandardBestaetigungVorlage;

    /// <summary>Betreff und Text mit Platzhaltern; leer bzw. Standard = Standard. Der Text muss {Link} enthalten.</summary>
    public void BestaetigungsvorlageSetzen(string? betreff, string? text)
    {
        var unbekannt = Bestaetigungsplatzhalter.Unbekannte(betreff).Concat(Bestaetigungsplatzhalter.Unbekannte(text)).Distinct().ToList();
        if (unbekannt.Count > 0)
        {
            throw new FachlicherFehler($"Unbekannte Platzhalter: {string.Join(", ", unbekannt.Select(u => "{" + u + "}"))}. Erlaubt sind {string.Join(", ", Bestaetigungsplatzhalter.Beschreibung.Keys.Select(k => "{" + k + "}"))}.");
        }

        var t = text?.ReplaceLineEndings("\n").Trim();
        if (!string.IsNullOrWhiteSpace(t) && !t.Contains("{Link}", StringComparison.Ordinal))
        {
            throw new FachlicherFehler("Der Text braucht den Platzhalter {Link} – ohne den persönlichen Link kann niemand bestätigen.");
        }

        BestaetigungBetreff = string.IsNullOrWhiteSpace(betreff) || betreff.Trim() == StandardBestaetigungBetreff ? null : betreff.Trim()[..Math.Min(betreff.Trim().Length, 300)];
        BestaetigungVorlage = string.IsNullOrWhiteSpace(t) || t == StandardBestaetigungVorlage.ReplaceLineEndings("\n").Trim() ? null : t[..Math.Min(t.Length, 10_000)];
    }

    /// <summary>Werte der Platzhalter für eine Person.</summary>
    /// <param name="frist">Bestätigungsfrist als Text (TT.MM.JJJJ) oder null.</param>
    public IReadOnlyDictionary<string, string> Bestaetigungswerte(Team team, Teammitglied m, string link, string? frist, string absender)
    {
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(m);
        var p = Person(m.PersonId);
        var thema = Thema(team.ThemaId) ?? throw new FachlicherFehler($"Team {team.Nr} hat noch kein Thema.");
        var partner = team.Mitglieder.Where(x => x.Id != m.Id).Select(x => Person(x.PersonId).VollerName).ToList();
        return new Dictionary<string, string>
        {
            ["Name"] = p.VollerName,
            ["Vorname"] = p.Vorname,
            ["Nachname"] = p.Nachname,
            ["Zuteilung"] = partner.Count == 0 ? "Ihnen" : $"Ihrem Team (mit {string.Join(" und ", partner)})",
            ["Team"] = string.Join(", ", team.Mitglieder.Select(x => Person(x.PersonId).VollerName)),
            ["Teamnummer"] = team.Nr.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Thema"] = $"{thema.Nr}. {thema.Titel} ({thema.Art})",
            ["Beschreibung"] = thema.Beschreibung ?? "",
            ["Link"] = link,
            ["Frist"] = frist ?? "–",
            ["BisFrist"] = frist is null ? "" : $" bis zum {frist}",
            ["Modul"] = Titel,
            ["Absender"] = absender,
        };
    }

    public (string Betreff, string Text) Bestaetigungsmail(Team team, Teammitglied m, string link, string? frist, string absender, string? betreff = null, string? text = null)
    {
        var werte = Bestaetigungswerte(team, m, link, frist, absender);
        return (Bestaetigungsplatzhalter.Ersetzen(betreff ?? BestaetigungBetreffVorlage, werte).ReplaceLineEndings(" ").Trim(),
            Bestaetigungsplatzhalter.Ersetzen(text ?? BestaetigungTextVorlage, werte).Trim());
    }
}
