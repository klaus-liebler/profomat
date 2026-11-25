using System.Globalization;
using System.Text.RegularExpressions;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Hausarbeiten;

/// <summary>Platzhalter der Terminmail (FA-610): <c>{Name}</c> usw.; unbekannte werden beim Speichern abgelehnt.</summary>
public static partial class Terminplatzhalter
{
    public static IReadOnlyDictionary<string, string> Beschreibung { get; } = new Dictionary<string, string>
    {
        ["Name"] = "Vor- und Nachname der Person",
        ["Vorname"] = "Vorname",
        ["Nachname"] = "Nachname",
        ["Team"] = "alle Mitglieder des Teams",
        ["Teamnummer"] = "Nummer des Teams",
        ["Thema"] = "Nummer und Titel des Themas",
        ["Wochentag"] = "z. B. Montag",
        ["Datum"] = "z. B. 01.03.2027",
        ["Uhrzeit"] = "Beginn, z. B. 09:15",
        ["Ende"] = "Ende, z. B. 09:45",
        ["Dauer"] = "Dauer in Minuten",
        ["Ort"] = "Ort des Gesprächs",
        ["Modul"] = "Titel der Hausarbeit",
        ["Absender"] = "Ihr Name",
    };

    public static IReadOnlyList<string> Unbekannte(string? text)
        => [.. Platzhalter().Matches(text ?? "").Select(m => m.Groups[1].Value).Where(n => !Beschreibung.ContainsKey(n)).Distinct()];

    public static string Ersetzen(string text, IReadOnlyDictionary<string, string> werte)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(werte);
        return Platzhalter().Replace(text, m => werte.TryGetValue(m.Groups[1].Value, out var w) ? w : m.Value);
    }

    [GeneratedRegex(@"\{([A-Za-zÄÖÜäöüß]+)\}")]
    private static partial Regex Platzhalter();
}

/// <summary>Planung der Erörterungsgespräche (FA-609): Dauer, Tage, Zeitfenster im 15-Minuten-Raster, Termin je Team.</summary>
public sealed partial class Hausarbeit
{
    public const int Raster = 15;
    public const int OrtMaxLaenge = 200;

    public const string StandardTerminBetreff = "Hausarbeit {Modul}: Ihr Erörterungsgespräch am {Datum} um {Uhrzeit} Uhr";

    public const string StandardTerminVorlage = """
        Guten Tag {Name},

        Ihr Erörterungsgespräch zur Hausarbeit im Modul „{Modul}“ findet statt am

        {Wochentag}, {Datum}, {Uhrzeit}–{Ende} Uhr
        Ort: {Ort}

        Thema: {Thema}
        Team: {Team}

        Bitte kommen Sie pünktlich. Sollte der Termin nicht passen, melden Sie sich bitte umgehend.

        Viele Grüße
        {Absender}
        """;

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Dauer eines Gesprächs in Minuten (Vielfaches von 15).</summary>
    public int GespraechMinuten { get; private set; } = 30;

    /// <summary>Gesprächstage als „2027-03-01,2027-03-02“ (gespeichert); siehe <see cref="Gespraechstage"/>.</summary>
    public string? GespraechTageText { get; private set; }

    /// <summary>Tagesbeginn in Minuten nach Mitternacht.</summary>
    public int TagBeginnMinuten { get; private set; } = 8 * 60;

    public int TagEndeMinuten { get; private set; } = 18 * 60;

    public string? GespraechOrt { get; private set; }

    public string? TerminBetreff { get; private set; }

    public string? TerminVorlage { get; private set; }

    /// <summary>SHA-256 des geheimen Kalender-Abo-Links (der Link selbst wird nicht gespeichert).</summary>
    public string? KalenderTokenHash { get; private set; }

    public IReadOnlyList<DateOnly> Gespraechstage => [.. (GespraechTageText ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(t => DateOnly.ParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture)).Order()];

    public string BetreffVorlage => TerminBetreff ?? StandardTerminBetreff;

    public string TextVorlage => TerminVorlage ?? StandardTerminVorlage;

    public IEnumerable<Team> MitTermin => TeamsSortiert.Where(t => t.Termin is not null);

    public IEnumerable<Team> OhneTermin => TeamsSortiert.Where(t => t.ThemaId is not null && t.Termin is null);

    public DateTime? Ende(Team team) => team?.Termin?.AddMinutes(GespraechMinuten);

    public void GespraecheEinstellen(int minuten, IReadOnlyCollection<DateOnly> tage, TimeOnly beginn, TimeOnly ende, string? ort)
    {
        ArgumentNullException.ThrowIfNull(tage);
        if (minuten < Raster || minuten > 240 || minuten % Raster != 0)
        {
            throw new FachlicherFehler("Die Gesprächsdauer ist ein Vielfaches von 15 Minuten (15 bis 240).");
        }

        if (tage.Count > 30)
        {
            throw new FachlicherFehler("Höchstens 30 Gesprächstage.");
        }

        var b = (beginn.Hour * 60) + beginn.Minute;
        var e = (ende.Hour * 60) + ende.Minute;
        if (b % Raster != 0 || e % Raster != 0 || e - b < minuten)
        {
            throw new FachlicherFehler("Beginn und Ende liegen im 15-Minuten-Raster, und das Zeitfenster reicht mindestens für ein Gespräch.");
        }

        GespraechMinuten = minuten;
        GespraechTageText = tage.Count == 0 ? null : string.Join(',', tage.Distinct().Order().Select(t => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        (TagBeginnMinuten, TagEndeMinuten) = (b, e);
        GespraechOrt = string.IsNullOrWhiteSpace(ort) ? null : ort.Trim()[..Math.Min(ort.Trim().Length, OrtMaxLaenge)];
    }

    /// <summary>Betreff und Text mit Platzhaltern; leer = Standard.</summary>
    public void TerminvorlageSetzen(string? betreff, string? text)
    {
        var unbekannt = Terminplatzhalter.Unbekannte(betreff).Concat(Terminplatzhalter.Unbekannte(text)).Distinct().ToList();
        if (unbekannt.Count > 0)
        {
            throw new FachlicherFehler($"Unbekannte Platzhalter: {string.Join(", ", unbekannt.Select(u => "{" + u + "}"))}. Erlaubt sind {string.Join(", ", Terminplatzhalter.Beschreibung.Keys.Select(k => "{" + k + "}"))}.");
        }

        TerminBetreff = string.IsNullOrWhiteSpace(betreff) || betreff.Trim() == StandardTerminBetreff ? null : betreff.Trim()[..Math.Min(betreff.Trim().Length, 300)];
        var t = text?.ReplaceLineEndings("\n").Trim();
        TerminVorlage = string.IsNullOrWhiteSpace(t) || t == StandardTerminVorlage.ReplaceLineEndings("\n").Trim() ? null : t[..Math.Min(t.Length, 10_000)];
    }

    public void KalenderTokenSetzen(string? hash) => KalenderTokenHash = hash;

    /// <summary>Darf das Team zu <paramref name="beginn"/> eingeplant werden? (Thema, Gesprächstag, Zeitfenster, Raster)</summary>
    public Freigabe TerminSetzbar(Guid teamId, DateTime beginn)
    {
        var t = teams.FirstOrDefault(x => x.Id == teamId);
        var tag = DateOnly.FromDateTime(beginn);
        var minuten = (beginn.Hour * 60) + beginn.Minute;
        return t is null ? Freigabe.Nein("Team nicht gefunden.")
            : t.ThemaId is null ? Freigabe.Nein($"Team {t.Nr} hat noch kein Thema.")
            : !Gespraechstage.Contains(tag) ? Freigabe.Nein($"Der {tag:dd.MM.yyyy} ist kein Gesprächstag.")
            : beginn.Second != 0 || minuten % Raster != 0 ? Freigabe.Nein("Termine beginnen im 15-Minuten-Raster.")
            : minuten < TagBeginnMinuten || minuten + GespraechMinuten > TagEndeMinuten
                ? Freigabe.Nein($"Das Gespräch muss zwischen {Uhr(TagBeginnMinuten)} und {Uhr(TagEndeMinuten)} Uhr liegen.")
            : Freigabe.Ja;
    }

    /// <summary>Termin setzen (null = entfernen).</summary>
    public void TerminSetzen(Guid teamId, DateTime? beginn)
    {
        if (beginn is { } b)
        {
            TerminSetzbar(teamId, b).Sicherstellen();
        }

        TeamMit(teamId).TerminSetzen(beginn);
    }

    /// <summary>Teams, deren Gespräche sich zeitlich überschneiden.</summary>
    public IReadOnlyList<(Team A, Team B)> Ueberschneidungen()
    {
        var liste = MitTermin.OrderBy(t => t.Termin).ToList();
        var ergebnis = new List<(Team, Team)>();
        for (var i = 0; i < liste.Count; i++)
        {
            for (var j = i + 1; j < liste.Count && liste[j].Termin < Ende(liste[i]); j++)
            {
                ergebnis.Add((liste[i], liste[j]));
            }
        }

        return ergebnis;
    }

    /// <summary>Teams mit Thema, aber ohne Termin, der Reihe nach in die frühesten freien Zeiten legen; gibt die Anzahl zurück.</summary>
    public int OhneTerminVerteilen()
    {
        var anzahl = 0;
        foreach (var team in OhneTermin.ToList())
        {
            var frei = FreieTermine().FirstOrDefault();
            if (frei == default)
            {
                break;
            }

            team.TerminSetzen(frei);
            anzahl++;
        }

        return anzahl;
    }

    /// <summary>Freie Anfangszeiten im Raster, an denen ein Gespräch ohne Überschneidung passt.</summary>
    public IEnumerable<DateTime> FreieTermine()
    {
        var belegt = MitTermin.Select(t => (Von: t.Termin!.Value, Bis: Ende(t)!.Value)).ToList();
        foreach (var tag in Gespraechstage)
        {
            for (var m = TagBeginnMinuten; m + GespraechMinuten <= TagEndeMinuten; m += Raster)
            {
                var von = tag.ToDateTime(TimeOnly.MinValue).AddMinutes(m);
                var bis = von.AddMinutes(GespraechMinuten);
                if (!belegt.Any(x => x.Von < bis && von < x.Bis))
                {
                    yield return von;
                }
            }
        }
    }

    /// <summary>Wer eine (neue) Terminmail braucht: Team mit Termin, Mailadresse vorhanden, der mitgeteilte Termin ist ein anderer.</summary>
    public IReadOnlyList<(Team Team, Teammitglied Mitglied)> TerminZuBenachrichtigen()
        => [.. MitTermin.SelectMany(t => t.Mitglieder.Where(m => m.TerminMitgeteilt != t.Termin && Person(m.PersonId).Email is not null).Select(m => (t, m)))];

    public void TerminMitgeteilt(Guid mitgliedId)
    {
        var (team, m) = Mitglied(mitgliedId);
        m.TerminMitteilen(team.Termin ?? throw new FachlicherFehler($"Team {team.Nr} hat keinen Termin."));
    }

    /// <summary>Werte der Platzhalter für eine Person.</summary>
    public IReadOnlyDictionary<string, string> Terminwerte(Team team, Teammitglied m, string absender)
    {
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(m);
        var p = Person(m.PersonId);
        var beginn = team.Termin ?? throw new FachlicherFehler($"Team {team.Nr} hat keinen Termin.");
        var thema = Thema(team.ThemaId);
        return new Dictionary<string, string>
        {
            ["Name"] = p.VollerName,
            ["Vorname"] = p.Vorname,
            ["Nachname"] = p.Nachname,
            ["Team"] = string.Join(", ", team.Mitglieder.Select(x => Person(x.PersonId).VollerName)),
            ["Teamnummer"] = team.Nr.ToString(CultureInfo.InvariantCulture),
            ["Thema"] = thema?.Anzeige ?? "–",
            ["Wochentag"] = beginn.ToString("dddd", De),
            ["Datum"] = beginn.ToString("dd.MM.yyyy", De),
            ["Uhrzeit"] = beginn.ToString("HH:mm", De),
            ["Ende"] = beginn.AddMinutes(GespraechMinuten).ToString("HH:mm", De),
            ["Dauer"] = GespraechMinuten.ToString(CultureInfo.InvariantCulture),
            ["Ort"] = GespraechOrt ?? "wird noch bekannt gegeben",
            ["Modul"] = Titel,
            ["Absender"] = absender,
        };
    }

    public static string Uhr(int minuten) => $"{minuten / 60:00}:{minuten % 60:00}";
}
