using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Finanzen;

/// <summary>Markierungen einer Buchung (mehrere gleichzeitig möglich).</summary>
[Flags]
public enum Buchungsmarkierung
{
    Keine = 0,

    /// <summary>Buchung ist falsch (falsche Kostenstelle, falscher Betrag, nicht von uns).</summary>
    NichtOk = 1,

    /// <summary>Im Blick behalten (z. B. Gutschrift steht aus).</summary>
    Nachverfolgen = 2,

    /// <summary>Rückfrage an die Verwaltung gestellt bzw. nötig.</summary>
    Rueckfrage = 4,

    /// <summary>Erledigt – die Markierung bleibt als Verlauf sichtbar, zählt aber nicht mehr als offen.</summary>
    Geklaert = 8,
}

/// <summary>
/// Kommentar und Markierungen zu einer Buchung (FA-718), für alle im Laborteam sichtbar. Die Buchungen eines Jahres werden bei jedem
/// Import ersetzt; die Notiz hängt deshalb am Fingerabdruck der Buchung (<see cref="Buchung.Fingerabdruck"/>) und überlebt den Import.
/// </summary>
public sealed class Buchungsnotiz
{
    public const int KommentarMaxLaenge = 2000;

    private Buchungsnotiz()
    {
    }

    public Buchungsnotiz(Guid topfId, int jahr, string fingerabdruck, string beschreibung)
    {
        Id = Ids.Neu();
        TopfId = topfId;
        Jahr = jahr;
        Fingerabdruck = fingerabdruck;
        Beschreibung = Kuerzen(beschreibung, 300) ?? "";
    }

    public Guid Id { get; private set; }

    public Guid TopfId { get; private set; }

    public int Jahr { get; private set; }

    public string Fingerabdruck { get; private set; } = string.Empty;

    /// <summary>Kurzbeschreibung der Buchung (Datum, Betrag, Text), damit eine Notiz auch ohne passende Buchung verständlich bleibt.</summary>
    public string Beschreibung { get; private set; } = string.Empty;

    public string? Kommentar { get; private set; }

    public Buchungsmarkierung Markierung { get; private set; }

    public Guid GeaendertVon { get; private set; }

    public DateTimeOffset Geaendert { get; private set; }

    /// <summary>Offen = nicht ok, nachverfolgen oder Rückfrage, und noch nicht geklärt.</summary>
    public bool Offen => (Markierung & (Buchungsmarkierung.NichtOk | Buchungsmarkierung.Nachverfolgen | Buchungsmarkierung.Rueckfrage)) != 0
                         && !Markierung.HasFlag(Buchungsmarkierung.Geklaert);

    public bool Leer => Kommentar is null && Markierung == Buchungsmarkierung.Keine;

    public void Setzen(string? kommentar, Buchungsmarkierung markierung, Guid kontoId, DateTimeOffset jetzt)
    {
        Kommentar = Kuerzen(kommentar, KommentarMaxLaenge);
        Markierung = markierung & (Buchungsmarkierung.NichtOk | Buchungsmarkierung.Nachverfolgen | Buchungsmarkierung.Rueckfrage | Buchungsmarkierung.Geklaert);
        GeaendertVon = kontoId;
        Geaendert = jetzt;
    }

    public static string Anzeige(Buchungsmarkierung m) => m switch
    {
        Buchungsmarkierung.NichtOk => "nicht ok",
        Buchungsmarkierung.Nachverfolgen => "zur Nachverfolgung",
        Buchungsmarkierung.Rueckfrage => "Rückfrage Verwaltung",
        Buchungsmarkierung.Geklaert => "geklärt",
        _ => "",
    };

    public static IEnumerable<Buchungsmarkierung> Einzeln(Buchungsmarkierung m)
        => new[] { Buchungsmarkierung.NichtOk, Buchungsmarkierung.Nachverfolgen, Buchungsmarkierung.Rueckfrage, Buchungsmarkierung.Geklaert }.Where(x => m.HasFlag(x));

    private static string? Kuerzen(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var t = s.Trim();
        return t.Length > max ? t[..max] : t;
    }
}

/// <summary>Von Hand erfasste Ausgabe einer Investitions- oder SQM-Maßnahme (FA-705), wenn keine Liste vorliegt.</summary>
public sealed class Einzelbuchung
{
    private Einzelbuchung()
    {
    }

    public Einzelbuchung(Guid topfId, DateOnly datum, decimal betrag, string text, DateTimeOffset erfasst)
    {
        if (betrag == 0)
        {
            throw new FachlicherFehler("Bitte einen Betrag angeben (Gutschriften negativ).");
        }

        Id = Ids.Neu();
        TopfId = topfId;
        Datum = datum;
        Betrag = decimal.Round(betrag, 2);
        Text = string.IsNullOrWhiteSpace(text) ? throw new FachlicherFehler("Bitte beschreiben, wofür.") : text.Trim()[..Math.Min(text.Trim().Length, 200)];
        Erfasst = erfasst;
    }

    public Guid Id { get; private set; }

    public Guid TopfId { get; private set; }

    public DateOnly Datum { get; private set; }

    /// <summary>Positiv = Ausgabe.</summary>
    public decimal Betrag { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public DateTimeOffset Erfasst { get; private set; }
}

/// <summary>Art einer budgetierungsfähigen Lehrveranstaltung.</summary>
public enum Veranstaltungsart
{
    Praktikum,
    Projekt,
    Studienarbeit,
}

/// <summary>
/// Prognose des Sachmittelbudgets der Laborkostenstelle für ein Jahr (FA-713) nach den Regeln der Fakultät IuI: Vorjahresdaten der
/// budgetierungsfähigen Veranstaltungen (Teilnehmende × anrechenbare SWS × 12,50 €), verfallene SWS (200 € je SWS, höchstens 800 € je
/// Semester), nicht anrechenbare SWS aus Abschlussarbeiten (200 € je SWS) und personenbezogene Zuweisungen (1.250 € je Prof./LfbA,
/// 800 € je WM/MTV/Doktorand/in). Die Zahlen erfassen die Lehrenden selbst; die Sätze sind änderbar.
/// </summary>
public sealed class Budgetprognose
{
    private readonly List<Prognoseveranstaltung> veranstaltungen = [];

    private Budgetprognose()
    {
    }

    public Budgetprognose(Guid topfId, int jahr)
    {
        Id = Ids.Neu();
        TopfId = topfId;
        Jahr = jahr;
    }

    public Guid Id { get; private set; }

    public Guid TopfId { get; private set; }

    /// <summary>Budgetjahr (die Daten stammen aus dem Vorjahr).</summary>
    public int Jahr { get; private set; }

    public decimal Professuren { get; private set; }

    public decimal Mitarbeitende { get; private set; }

    public decimal VerfalleneSwsSoSe { get; private set; }

    public decimal VerfalleneSwsWiSe { get; private set; }

    public decimal NichtAnrechenbareSws { get; private set; }

    public decimal SatzStudierendeSws { get; private set; } = 12.50m;

    public decimal SatzSws { get; private set; } = 200m;

    public decimal HoechstVerfallenJeSemester { get; private set; } = 800m;

    public decimal SatzProfessur { get; private set; } = 1250m;

    public decimal SatzMitarbeitende { get; private set; } = 800m;

    public string? Notiz { get; private set; }

    public IReadOnlyList<Prognoseveranstaltung> Veranstaltungen => veranstaltungen;

    public decimal Lehre => decimal.Round(veranstaltungen.Sum(v => v.Teilnehmende * v.Sws) * SatzStudierendeSws, 2);

    public decimal Verfallen => Math.Min(VerfalleneSwsSoSe * SatzSws, HoechstVerfallenJeSemester) + Math.Min(VerfalleneSwsWiSe * SatzSws, HoechstVerfallenJeSemester);

    public decimal Abschlussarbeiten => NichtAnrechenbareSws * SatzSws;

    public decimal Personen => (Professuren * SatzProfessur) + (Mitarbeitende * SatzMitarbeitende);

    public decimal Summe => Lehre + Verfallen + Abschlussarbeiten + Personen;

    public void Setzen(decimal professuren, decimal mitarbeitende, decimal verfallenSoSe, decimal verfallenWiSe, decimal nichtAnrechenbar,
        IReadOnlyCollection<(string Semester, string Bezeichnung, Veranstaltungsart Art, int Teilnehmende, decimal Sws)> liste, string? notiz)
    {
        ArgumentNullException.ThrowIfNull(liste);
        if (new[] { professuren, mitarbeitende, verfallenSoSe, verfallenWiSe, nichtAnrechenbar }.Any(x => x is < 0 or > 1000))
        {
            throw new FachlicherFehler("Anzahlen und SWS liegen zwischen 0 und 1000.");
        }

        Professuren = professuren;
        Mitarbeitende = mitarbeitende;
        VerfalleneSwsSoSe = verfallenSoSe;
        VerfalleneSwsWiSe = verfallenWiSe;
        NichtAnrechenbareSws = nichtAnrechenbar;
        Notiz = string.IsNullOrWhiteSpace(notiz) ? null : notiz.Trim()[..Math.Min(notiz.Trim().Length, 2000)];
        veranstaltungen.Clear();
        var nr = 0;
        foreach (var (semester, bezeichnung, art, teilnehmende, sws) in liste.Where(v => !string.IsNullOrWhiteSpace(v.Bezeichnung)))
        {
            veranstaltungen.Add(new Prognoseveranstaltung(Id, ++nr, semester, bezeichnung, art, teilnehmende, sws));
        }
    }

    public void SaetzeSetzen(decimal studierendeSws, decimal sws, decimal hoechstVerfallen, decimal professur, decimal mitarbeitende)
    {
        if (new[] { studierendeSws, sws, hoechstVerfallen, professur, mitarbeitende }.Any(x => x is < 0 or > 100000))
        {
            throw new FachlicherFehler("Die Sätze liegen zwischen 0 und 100.000 €.");
        }

        (SatzStudierendeSws, SatzSws, HoechstVerfallenJeSemester, SatzProfessur, SatzMitarbeitende) = (studierendeSws, sws, hoechstVerfallen, professur, mitarbeitende);
    }
}

public sealed class Prognoseveranstaltung
{
    private Prognoseveranstaltung()
    {
    }

    internal Prognoseveranstaltung(Guid prognoseId, int nr, string semester, string bezeichnung, Veranstaltungsart art, int teilnehmende, decimal sws)
    {
        if (teilnehmende is < 0 or > 5000 || sws is < 0 or > 40)
        {
            throw new FachlicherFehler($"„{bezeichnung}“: Teilnehmende 0–5000, SWS 0–40.");
        }

        Id = Ids.Neu();
        PrognoseId = prognoseId;
        Nr = nr;
        Semester = (semester ?? "").Trim() is { Length: > 0 } s ? s[..Math.Min(s.Length, 20)] : "–";
        Bezeichnung = bezeichnung.Trim()[..Math.Min(bezeichnung.Trim().Length, 200)];
        Art = art;
        Teilnehmende = teilnehmende;
        Sws = sws;
    }

    public Guid Id { get; private set; }

    public Guid PrognoseId { get; private set; }

    public int Nr { get; private set; }

    /// <summary>z. B. „SoSe 2026“, „WiSe 2026/27“.</summary>
    public string Semester { get; private set; } = string.Empty;

    public string Bezeichnung { get; private set; } = string.Empty;

    public Veranstaltungsart Art { get; private set; }

    public int Teilnehmende { get; private set; }

    /// <summary>Anrechenbare SWS laut FR-Liste „Budgetierungsfähige Lehrveranstaltungen“.</summary>
    public decimal Sws { get; private set; }
}

/// <summary>Gesendete Fristerinnerung (FA-712) – damit jede Frist nur einmal erinnert wird.</summary>
public sealed class Finanzerinnerung
{
    private Finanzerinnerung()
    {
    }

    public Finanzerinnerung(Guid teamId, string schluessel, DateTimeOffset gesendet)
    {
        Id = Ids.Neu();
        TeamId = teamId;
        Schluessel = schluessel;
        Gesendet = gesendet;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public string Schluessel { get; private set; } = string.Empty;

    public DateTimeOffset Gesendet { get; private set; }
}

/// <summary>Monatswerte einer Kostenstelle (FA-711).</summary>
public sealed record Monatswert(DateOnly Monat, decimal Ausgaben, decimal Zugaenge, decimal Geplant, decimal RestAmEnde);

/// <summary>Auswertungen der Kostenstelle: Verlauf je Monat und Jahresvergleich je Kostenartengruppe.</summary>
public static class Auswertung
{
    /// <param name="bindungen">Bindungen der Kostenstelle (nur fürs laufende Jahr sinnvoll); fällige zählen im Monat ihrer Fälligkeit als „geplant“.</param>
    public static IReadOnlyList<Monatswert> Verlauf(int jahr, IReadOnlyCollection<Buchung> buchungen, IReadOnlyCollection<Bindung> bindungen)
    {
        ArgumentNullException.ThrowIfNull(buchungen);
        ArgumentNullException.ThrowIfNull(bindungen);
        var rest = 0m;
        var liste = new List<Monatswert>();
        for (var m = 1; m <= 12; m++)
        {
            var im = buchungen.Where(b => b.Buchungsdatum.Month == m).ToList();
            var ausgaben = im.Where(b => !b.IstZugang).Sum(b => b.Betrag);
            var zugaenge = -im.Where(b => b.IstZugang).Sum(b => b.Betrag);
            var geplant = bindungen.Where(b => b.Faellig.Year == jahr && b.Faellig.Month == m).Sum(b => b.Betrag);
            rest += zugaenge - ausgaben;
            liste.Add(new(new DateOnly(jahr, m, 1), ausgaben, zugaenge, geplant, rest));
        }

        return liste;
    }

    /// <summary>Ausgaben je Kostenartengruppe und Jahr (Zugänge ausgenommen), Gruppen nach Summe absteigend.</summary>
    public static (IReadOnlyList<int> Jahre, IReadOnlyList<(string Gruppe, IReadOnlyDictionary<int, decimal> Werte)> Zeilen) Jahresvergleich(IEnumerable<Buchung> buchungen)
    {
        ArgumentNullException.ThrowIfNull(buchungen);
        var liste = buchungen.Where(b => !b.IstZugang).ToList();
        var jahre = liste.Select(b => b.Jahr).Distinct().Order().ToList();
        var zeilen = liste.GroupBy(b => Kostenartgruppe.Von(b.Kostenart))
            .Select(g => (g.Key, (IReadOnlyDictionary<int, decimal>)g.GroupBy(b => b.Jahr).ToDictionary(x => x.Key, x => x.Sum(b => b.Betrag))))
            .OrderByDescending(z => z.Item2.Values.Sum())
            .ToList();
        return (jahre, zeilen);
    }
}
