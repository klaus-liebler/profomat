using System.Globalization;
using System.Text.RegularExpressions;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Klausuren;

namespace ProfOMat.Domain.Hausarbeiten;

/// <summary>Art eines Themas (wie auf der Themenliste: Software oder Hardware).</summary>
public enum Themenart
{
    Software,
    Hardware,
}

/// <summary>Stand der Themenbestätigung einer Person.</summary>
public enum Bestaetigungsstand
{
    /// <summary>Kein Team oder dem Team ist noch kein Thema zugeteilt.</summary>
    OhneThema,

    /// <summary>Thema zugeteilt, die Mail mit dem persönlichen Link ist noch nicht erstellt.</summary>
    NichtBenachrichtigt,

    /// <summary>Mail erstellt, Bestätigung steht aus.</summary>
    Wartet,

    /// <summary>Bestätigung steht nach Ablauf der Frist noch aus.</summary>
    Ueberfaellig,

    Bestaetigt,
}

/// <summary>Ein Thema der Themenliste (oder ein eigenes Thema eines Teams).</summary>
public sealed class HausarbeitThema
{
    public const int TitelMaxLaenge = 200;
    public const int BeschreibungMaxLaenge = 4000;

    private HausarbeitThema()
    {
    }

    internal HausarbeitThema(Guid hausarbeitId, int nr, string titel, string? beschreibung, Themenart art, bool eigen)
    {
        Id = Ids.Neu();
        HausarbeitId = hausarbeitId;
        Setzen(nr, titel, beschreibung, art, eigen);
    }

    public Guid Id { get; private set; }

    public Guid HausarbeitId { get; private set; }

    public int Nr { get; private set; }

    public string Titel { get; private set; } = string.Empty;

    public string? Beschreibung { get; private set; }

    public Themenart Art { get; private set; }

    /// <summary>Eigener Vorschlag eines Teams (nicht Teil der ausgehängten Themenliste).</summary>
    public bool Eigen { get; private set; }

    public string Anzeige => $"{Nr}. {Titel}";

    internal void Setzen(int nr, string titel, string? beschreibung, Themenart art, bool eigen)
    {
        if (nr < 1)
        {
            throw new FachlicherFehler("Die Themennummer beginnt bei 1.");
        }

        if (string.IsNullOrWhiteSpace(titel))
        {
            throw new FachlicherFehler("Bitte einen Titel für das Thema angeben.");
        }

        Nr = nr;
        Titel = Kuerzen(titel.Trim(), TitelMaxLaenge);
        Beschreibung = string.IsNullOrWhiteSpace(beschreibung) ? null : Kuerzen(beschreibung.Trim(), BeschreibungMaxLaenge);
        Art = art;
        Eigen = eigen;
    }

    private static string Kuerzen(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Eine Person aus der Prüfungsliste (oder von Hand ergänzt).</summary>
public sealed class Bearbeitende
{
    private Bearbeitende()
    {
    }

    internal Bearbeitende(Guid hausarbeitId, string matrikelnummer, string nachname, string vorname, string? email, bool vonHand)
    {
        Id = Ids.Neu();
        HausarbeitId = hausarbeitId;
        Matrikelnummer = matrikelnummer;
        VonHand = vonHand;
        Angemeldet = !vonHand;
        Uebernehmen(nachname, vorname, email);
    }

    public Guid Id { get; private set; }

    public Guid HausarbeitId { get; private set; }

    public string Matrikelnummer { get; private set; } = string.Empty;

    public string Nachname { get; private set; } = string.Empty;

    public string Vorname { get; private set; } = string.Empty;

    public string? Email { get; private set; }

    /// <summary>In der letzten Prüfungsliste enthalten.</summary>
    public bool Angemeldet { get; private set; }

    /// <summary>Von Hand ergänzt (z. B. noch nicht zur Prüfung angemeldet).</summary>
    public bool VonHand { get; private set; }

    public string Name => $"{Nachname}, {Vorname}";

    public string VollerName => $"{Vorname} {Nachname}";

    internal bool Uebernehmen(string nachname, string vorname, string? email)
    {
        var neu = (nachname.Trim(), vorname.Trim(), string.IsNullOrWhiteSpace(email) ? null : email.Trim());
        var geaendert = neu != (Nachname, Vorname, Email);
        (Nachname, Vorname, Email) = neu;
        return geaendert;
    }

    internal void AngemeldetSetzen(bool angemeldet, bool vonHand) => (Angemeldet, VonHand) = (angemeldet, vonHand);
}

/// <summary>Mitglied eines Teams mit dem Stand seiner persönlichen Themenbestätigung.</summary>
public sealed class Teammitglied
{
    private Teammitglied()
    {
    }

    internal Teammitglied(Guid teamId, Guid personId)
    {
        Id = Ids.Neu();
        TeamId = teamId;
        PersonId = personId;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public Guid PersonId { get; private set; }

    /// <summary>Wann die Mail mit dem persönlichen Link erstellt wurde.</summary>
    public DateTimeOffset? Benachrichtigt { get; private set; }

    /// <summary>SHA-256 des persönlichen Bestätigungslinks (der Link selbst wird nicht gespeichert).</summary>
    public string? TokenHash { get; private set; }

    public DateTimeOffset? Bestaetigt { get; private set; }

    internal void Benachrichtigen(string tokenHash, DateTimeOffset jetzt) => (TokenHash, Benachrichtigt, Bestaetigt) = (tokenHash, jetzt, null);

    internal void Zuruecksetzen() => (TokenHash, Benachrichtigt, Bestaetigt) = (null, null, null);

    internal void Bestaetigen(DateTimeOffset jetzt) => Bestaetigt ??= jetzt;
}

/// <summary>Team (in der Regel zwei Personen) mit Sprecher/in, bis zu drei Themenwünschen und zugeteiltem Thema.</summary>
public sealed class Team
{
    public const int NotizMaxLaenge = 1000;

    private readonly List<Teammitglied> mitglieder = [];

    private Team()
    {
    }

    internal Team(Guid hausarbeitId, int nr)
    {
        Id = Ids.Neu();
        HausarbeitId = hausarbeitId;
        Nr = nr;
    }

    public Guid Id { get; private set; }

    public Guid HausarbeitId { get; private set; }

    public int Nr { get; private set; }

    public Guid? SprecherId { get; private set; }

    public Guid? Wunsch1 { get; private set; }

    public Guid? Wunsch2 { get; private set; }

    public Guid? Wunsch3 { get; private set; }

    public Guid? ThemaId { get; private set; }

    public string? Notiz { get; private set; }

    public IReadOnlyList<Teammitglied> Mitglieder => mitglieder;

    public IReadOnlyList<Guid> Wuensche => new[] { Wunsch1, Wunsch2, Wunsch3 }.OfType<Guid>().ToList();

    internal void Setzen(IReadOnlyList<Guid> personen, Guid sprecherId, IReadOnlyList<Guid> wuensche, string? notiz)
    {
        foreach (var weg in mitglieder.Where(m => !personen.Contains(m.PersonId)).ToList())
        {
            mitglieder.Remove(weg);
        }

        foreach (var p in personen.Where(p => mitglieder.All(m => m.PersonId != p)))
        {
            mitglieder.Add(new Teammitglied(Id, p));
        }

        SprecherId = sprecherId;
        (Wunsch1, Wunsch2, Wunsch3) = (wuensche.ElementAtOrDefault(0) is var a && a != Guid.Empty ? a : null,
            wuensche.ElementAtOrDefault(1) is var b && b != Guid.Empty ? b : null,
            wuensche.ElementAtOrDefault(2) is var c && c != Guid.Empty ? c : null);
        Notiz = string.IsNullOrWhiteSpace(notiz) ? null : notiz.Trim()[..Math.Min(notiz.Trim().Length, NotizMaxLaenge)];
    }

    /// <summary>Neues Thema: bisherige Benachrichtigungen und Bestätigungen gelten nicht mehr.</summary>
    internal bool Zuteilen(Guid? themaId)
    {
        if (ThemaId == themaId)
        {
            return false;
        }

        ThemaId = themaId;
        foreach (var m in mitglieder)
        {
            m.Zuruecksetzen();
        }

        return true;
    }
}

/// <summary>Ergebnis des Einlesens einer Themenliste.</summary>
public sealed record ThemenImport(int Neu, int Geaendert);

/// <summary>
/// Hausarbeit eines Moduls (Prüfungsform „Hausarbeit mit Erörterungsgespräch“): Personen aus der Prüfungsliste, Themenliste,
/// Teams mit Wünschen, Zuteilung und persönliche Themenbestätigung bis zu einer Frist.
/// </summary>
public sealed partial class Hausarbeit
{
    public const int TitelMaxLaenge = 200;

    /// <summary>Teamgröße: in der Regel zwei, Ausnahmen nach Absprache.</summary>
    public const int MaxTeamgroesse = 3;

    private readonly List<HausarbeitThema> themen = [];
    private readonly List<Bearbeitende> personen = [];
    private readonly List<Team> teams = [];

    private Hausarbeit()
    {
    }

    public Hausarbeit(Guid kontoId, string titel, string? pruefungsnummer, string? pruefer, DateTimeOffset erstellt)
    {
        Id = Ids.Neu();
        KontoId = kontoId;
        Erstellt = erstellt;
        Pruefer = pruefer;
        Aendern(titel, pruefungsnummer, null);
    }

    public Guid Id { get; private set; }

    /// <summary>Wer die Hausarbeit betreut (Prüfende/r).</summary>
    public Guid KontoId { get; private set; }

    public string Titel { get; private set; } = string.Empty;

    public string? Pruefungsnummer { get; private set; }

    public string? Pruefer { get; private set; }

    /// <summary>Bis wann jedes Teammitglied sein Thema bestätigt.</summary>
    public DateTimeOffset? Bestaetigungsfrist { get; private set; }

    public DateTimeOffset Erstellt { get; private set; }

    public IReadOnlyList<HausarbeitThema> Themen => themen;

    public IReadOnlyList<Bearbeitende> Personen => personen;

    public IReadOnlyList<Team> Teams => teams;

    public IEnumerable<HausarbeitThema> ThemenSortiert => themen.OrderBy(t => t.Nr);

    public IEnumerable<Team> TeamsSortiert => teams.OrderBy(t => t.Nr);

    public IEnumerable<Bearbeitende> PersonenSortiert => personen.OrderBy(p => p.Nachname, StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), true))
        .ThenBy(p => p.Vorname, StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), true));

    public void Aendern(string titel, string? pruefungsnummer, DateTimeOffset? bestaetigungsfrist)
    {
        if (string.IsNullOrWhiteSpace(titel))
        {
            throw new FachlicherFehler("Bitte einen Titel angeben.");
        }

        Titel = titel.Trim()[..Math.Min(titel.Trim().Length, TitelMaxLaenge)];
        Pruefungsnummer = string.IsNullOrWhiteSpace(pruefungsnummer) ? null : pruefungsnummer.Trim();
        Bestaetigungsfrist = bestaetigungsfrist;
    }

    // ------------------------------------------------------------------ Personen
    /// <summary>Gleicht die Personen mit der Prüfungsliste ab (Schlüssel: Matrikelnummer). Nichts wird gelöscht.</summary>
    public ImportErgebnis Abgleichen(IReadOnlyList<Listenzeile> zeilen)
    {
        ArgumentNullException.ThrowIfNull(zeilen);
        var doppelt = zeilen.GroupBy(z => z.Matrikelnummer).FirstOrDefault(g => g.Count() > 1);
        if (doppelt is not null)
        {
            throw new FachlicherFehler($"Die Matrikelnummer {doppelt.Key} steht mehrfach in der Liste.");
        }

        int neu = 0, geaendert = 0, wieder = 0;
        foreach (var z in zeilen)
        {
            var p = personen.FirstOrDefault(x => x.Matrikelnummer == z.Matrikelnummer);
            if (p is null)
            {
                personen.Add(new Bearbeitende(Id, z.Matrikelnummer, z.Nachname, z.Vorname, z.Email, vonHand: false));
                neu++;
                continue;
            }

            if (p.Uebernehmen(z.Nachname, z.Vorname, z.Email))
            {
                geaendert++;
            }

            if (!p.Angemeldet)
            {
                wieder++;
            }

            p.AngemeldetSetzen(true, vonHand: false);
        }

        var gesehen = zeilen.Select(z => z.Matrikelnummer).ToHashSet(StringComparer.Ordinal);
        var abgemeldet = 0;
        foreach (var p in personen.Where(p => p.Angemeldet && !gesehen.Contains(p.Matrikelnummer)))
        {
            p.AngemeldetSetzen(false, p.VonHand);
            abgemeldet++;
        }

        return new ImportErgebnis(neu, geaendert, abgemeldet, wieder);
    }

    /// <summary>Person von Hand ergänzen, z. B. wenn sie (noch) nicht in der Prüfungsliste steht.</summary>
    public Bearbeitende PersonHinzufuegen(string matrikelnummer, string nachname, string vorname, string? email)
    {
        var m = (matrikelnummer ?? string.Empty).Trim();
        if (m.Length is < 3 or > 20 || !m.All(char.IsAsciiLetterOrDigit))
        {
            throw new FachlicherFehler("Bitte eine gültige Matrikelnummer angeben.");
        }

        if (string.IsNullOrWhiteSpace(nachname) || string.IsNullOrWhiteSpace(vorname))
        {
            throw new FachlicherFehler("Bitte Vor- und Nachnamen angeben.");
        }

        if (personen.Any(p => p.Matrikelnummer == m))
        {
            throw new FachlicherFehler($"Die Matrikelnummer {m} gibt es hier schon.");
        }

        var p = new Bearbeitende(Id, m, nachname, vorname, email, vonHand: true);
        personen.Add(p);
        return p;
    }

    public Team? TeamVon(Guid personId) => teams.FirstOrDefault(t => t.Mitglieder.Any(m => m.PersonId == personId));

    public Bearbeitende Person(Guid id) => personen.FirstOrDefault(p => p.Id == id) ?? throw new FachlicherFehler("Person nicht gefunden.");

    public IEnumerable<Bearbeitende> OhneTeam => PersonenSortiert.Where(p => p.Angemeldet && TeamVon(p.Id) is null);

    // ------------------------------------------------------------------ Themen
    public HausarbeitThema? Thema(Guid? id) => id is { } x ? themen.FirstOrDefault(t => t.Id == x) : null;

    public HausarbeitThema ThemaSpeichern(Guid? id, int nr, string titel, string? beschreibung, Themenart art, bool eigen)
    {
        if (themen.Any(t => t.Nr == nr && t.Id != id))
        {
            throw new FachlicherFehler($"Die Themennummer {nr} ist schon vergeben.");
        }

        if (id is { } vorhanden)
        {
            var t = Thema(vorhanden) ?? throw new FachlicherFehler("Thema nicht gefunden.");
            t.Setzen(nr, titel, beschreibung, art, eigen);
            return t;
        }

        var neu = new HausarbeitThema(Id, nr, titel, beschreibung, art, eigen);
        themen.Add(neu);
        return neu;
    }

    public int NaechsteThemennummer => themen.Count == 0 ? 1 : themen.Max(t => t.Nr) + 1;

    public Freigabe KannThemaEntfernen(Guid themaId)
        => teams.FirstOrDefault(t => t.ThemaId == themaId) is { } zugeteilt
            ? Freigabe.Nein($"Das Thema ist Team {zugeteilt.Nr} zugeteilt.")
            : teams.FirstOrDefault(t => t.Wuensche.Contains(themaId)) is { } gewuenscht
                ? Freigabe.Nein($"Team {gewuenscht.Nr} wünscht sich dieses Thema.")
                : Freigabe.Ja;

    public void ThemaEntfernen(Guid themaId)
    {
        KannThemaEntfernen(themaId).Sicherstellen();
        themen.Remove(Thema(themaId) ?? throw new FachlicherFehler("Thema nicht gefunden."));
    }

    /// <summary>
    /// Liest eine Themenliste als Text ein, wie sie auf der Webseite steht: je Thema eine Kopfzeile
    /// „12. Titel Software“ bzw. „… Hardware“, darunter die Beschreibung. Vorhandene Nummern werden aktualisiert.
    /// </summary>
    public ThemenImport ThemenEinlesen(string text)
    {
        var bloecke = new List<(int Nr, string Titel, Themenart Art, List<string> Zeilen)>();
        foreach (var roh in (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var zeile = roh.Trim();
            var kopf = Themenkopf().Match(zeile);
            if (kopf.Success)
            {
                var art = kopf.Groups["art"].Value.Equals("Hardware", StringComparison.OrdinalIgnoreCase) ? Themenart.Hardware : Themenart.Software;
                bloecke.Add((int.Parse(kopf.Groups["nr"].Value, CultureInfo.InvariantCulture), kopf.Groups["titel"].Value.Trim(), art, []));
            }
            else if (bloecke.Count > 0 && zeile.Length > 0)
            {
                bloecke[^1].Zeilen.Add(zeile);
            }
        }

        if (bloecke.Count == 0)
        {
            throw new FachlicherFehler("Keine Themen erkannt. Erwartet wird je Thema eine Zeile wie „12. Titel Software“ (oder „Hardware“), darunter die Beschreibung.");
        }

        var doppelt = bloecke.GroupBy(b => b.Nr).FirstOrDefault(g => g.Count() > 1);
        if (doppelt is not null)
        {
            throw new FachlicherFehler($"Die Themennummer {doppelt.Key} steht mehrfach im Text.");
        }

        int neu = 0, geaendert = 0;
        foreach (var b in bloecke)
        {
            var vorhanden = themen.FirstOrDefault(t => t.Nr == b.Nr);
            var beschreibung = string.Join(" ", b.Zeilen);
            if (vorhanden is null)
            {
                themen.Add(new HausarbeitThema(Id, b.Nr, b.Titel, beschreibung, b.Art, eigen: false));
                neu++;
            }
            else if ((vorhanden.Titel, vorhanden.Beschreibung ?? string.Empty, vorhanden.Art) != (b.Titel, beschreibung, b.Art))
            {
                vorhanden.Setzen(b.Nr, b.Titel, beschreibung, b.Art, vorhanden.Eigen);
                geaendert++;
            }
        }

        return new ThemenImport(neu, geaendert);
    }

    [GeneratedRegex(@"^(?<nr>\d{1,3})\.\s+(?<titel>.+?)\s+(?<art>Software|Hardware)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Themenkopf();

    // ------------------------------------------------------------------ Teams
    public Team TeamMit(Guid id) => teams.FirstOrDefault(t => t.Id == id) ?? throw new FachlicherFehler("Team nicht gefunden.");

    /// <summary>Legt ein Team an (<paramref name="id"/> = <c>null</c>) oder ändert es.</summary>
    public Team TeamSpeichern(Guid? id, IReadOnlyList<Guid> personenIds, Guid? sprecherId, IReadOnlyList<Guid> wuensche, string? notiz)
    {
        ArgumentNullException.ThrowIfNull(personenIds);
        ArgumentNullException.ThrowIfNull(wuensche);
        var mitglieder = personenIds.Where(p => p != Guid.Empty).Distinct().ToList();
        if (mitglieder.Count == 0)
        {
            throw new FachlicherFehler("Ein Team braucht mindestens eine Person.");
        }

        if (mitglieder.Count > MaxTeamgroesse)
        {
            throw new FachlicherFehler($"Ein Team hat höchstens {MaxTeamgroesse} Personen.");
        }

        foreach (var p in mitglieder)
        {
            var person = Person(p);
            if (TeamVon(p) is { } anderes && anderes.Id != id)
            {
                throw new FachlicherFehler($"{person.VollerName} ist schon in Team {anderes.Nr}.");
            }
        }

        var sprecher = sprecherId is { } s && mitglieder.Contains(s) ? s : mitglieder[0];
        var w = wuensche.Where(x => x != Guid.Empty).ToList();
        if (w.Count > 3)
        {
            throw new FachlicherFehler("Höchstens drei Themenwünsche.");
        }

        if (w.Distinct().Count() != w.Count)
        {
            throw new FachlicherFehler("Jedes Thema bitte nur einmal wünschen.");
        }

        if (w.Any(x => Thema(x) is null))
        {
            throw new FachlicherFehler("Ein gewünschtes Thema gibt es nicht (mehr).");
        }

        var team = id is { } vorhanden ? TeamMit(vorhanden) : new Team(Id, teams.Count == 0 ? 1 : teams.Max(t => t.Nr) + 1);
        if (id is null)
        {
            teams.Add(team);
        }

        team.Setzen(mitglieder, sprecher, w, notiz);
        return team;
    }

    public void TeamEntfernen(Guid teamId) => teams.Remove(TeamMit(teamId));

    /// <summary>Thema zuteilen (<c>null</c> = Zuteilung aufheben). Ein neues Thema muss erneut bestätigt werden.</summary>
    public bool ThemaZuteilen(Guid teamId, Guid? themaId)
    {
        if (themaId is { } t && Thema(t) is null)
        {
            throw new FachlicherFehler("Thema nicht gefunden.");
        }

        return TeamMit(teamId).Zuteilen(themaId);
    }

    /// <summary>
    /// Vorschlag nach Wünschen für Teams ohne Thema: Teams der Reihe nach, jeweils der erste Wunsch, der noch frei ist.
    /// Bereits zugeteilte Themen bleiben unverändert. Liefert die Zahl der neu zugeteilten Teams.
    /// </summary>
    public int NachWuenschenZuteilen()
    {
        var vergeben = teams.Where(t => t.ThemaId is not null).Select(t => t.ThemaId!.Value).ToHashSet();
        var anzahl = 0;
        // Wer weniger Wünsche hat, kommt zuerst: so bekommen möglichst viele Teams einen ihrer Wünsche.
        foreach (var team in teams.Where(t => t.ThemaId is null && t.Wuensche.Count > 0).OrderBy(t => t.Wuensche.Count).ThenBy(t => t.Nr))
        {
            if (team.Wuensche.FirstOrDefault(w => !vergeben.Contains(w)) is var frei && frei != Guid.Empty)
            {
                team.Zuteilen(frei);
                vergeben.Add(frei);
                anzahl++;
            }
        }

        return anzahl;
    }

    public IReadOnlyList<Team> TeamsMitThema(Guid themaId) => teams.Where(t => t.ThemaId == themaId).OrderBy(t => t.Nr).ToList();

    // ------------------------------------------------------------------ Bestätigung
    public (Team Team, Teammitglied Mitglied) Mitglied(Guid mitgliedId)
        => teams.SelectMany(t => t.Mitglieder.Select(m => (t, m))).FirstOrDefault(x => x.m.Id == mitgliedId) is { t: not null } treffer
            ? treffer
            : throw new FachlicherFehler("Teammitglied nicht gefunden.");

    public Bestaetigungsstand Stand(Team team, Teammitglied m, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(m);
        return team.ThemaId is null ? Bestaetigungsstand.OhneThema
            : m.Bestaetigt is not null ? Bestaetigungsstand.Bestaetigt
            : m.Benachrichtigt is null ? Bestaetigungsstand.NichtBenachrichtigt
            : Bestaetigungsfrist is { } frist && jetzt > frist ? Bestaetigungsstand.Ueberfaellig
            : Bestaetigungsstand.Wartet;
    }

    /// <summary>Wer eine Mail mit persönlichem Link bekommen kann: Thema zugeteilt, noch nicht benachrichtigt, Mailadresse vorhanden.</summary>
    public IReadOnlyList<(Team Team, Teammitglied Mitglied)> ZuBenachrichtigen()
        => TeamsSortiert.Where(t => t.ThemaId is not null)
            .SelectMany(t => t.Mitglieder.Where(m => m.Benachrichtigt is null && Person(m.PersonId).Email is not null).Select(m => (t, m)))
            .ToList();

    public void Benachrichtigen(Guid mitgliedId, string tokenHash, DateTimeOffset jetzt)
    {
        var (team, m) = Mitglied(mitgliedId);
        if (team.ThemaId is null)
        {
            throw new FachlicherFehler($"Team {team.Nr} hat noch kein Thema.");
        }

        m.Benachrichtigen(tokenHash, jetzt);
    }

    /// <summary>Persönliche Bestätigung (über den Link); wiederholtes Bestätigen ändert nichts.</summary>
    public void Bestaetigen(Guid mitgliedId, DateTimeOffset jetzt)
    {
        var (team, m) = Mitglied(mitgliedId);
        if (team.ThemaId is null || m.Benachrichtigt is null)
        {
            throw new FachlicherFehler("Für Sie liegt gerade kein Thema zur Bestätigung vor.");
        }

        m.Bestaetigen(jetzt);
    }
}
