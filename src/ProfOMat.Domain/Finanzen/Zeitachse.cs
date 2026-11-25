namespace ProfOMat.Domain.Finanzen;

/// <summary>Ein Monat eines Topfs auf der Zeitachse (Werte zum Monatsende).</summary>
/// <param name="Frei">Freier Betrag am Monatsende (nach Bindungen, mit erwarteten Zugängen bis dahin).</param>
/// <param name="Erwartet">Der Wert enthält erwartete, noch nicht gebuchte Zugänge.</param>
/// <param name="Abfluss">In diesem Monat fällige Bindungen.</param>
/// <param name="Zugang">In diesem Monat erwartete Zugänge.</param>
/// <param name="Verfaellt">Am Stichtag in diesem Monat noch freier Betrag, der danach nicht mehr einsetzbar ist.</param>
/// <param name="Grenze">Kostenstelle, Dezember: sicher übertragbarer Betrag.</param>
/// <param name="EinzugMoeglich">Kostenstelle, Dezember: Überhang über der Grenze (wird nicht abgezogen).</param>
/// <param name="Aktiv">Der Topf ist in diesem Monat verfügbar (nach dem Stichtag nicht mehr).</param>
public sealed record Zeitpunkt(DateOnly Monat, decimal Frei, bool Erwartet, decimal Abfluss, decimal Zugang, decimal? Verfaellt, decimal? Grenze, decimal? EinzugMoeglich, bool Aktiv);

public sealed record Zeitreihe(Topf Topf, IReadOnlyList<Zeitpunkt> Punkte);

/// <summary>Freie Mittel aller Töpfe im Monatsraster (FA-716).</summary>
public sealed record Zeitachse(IReadOnlyList<DateOnly> Monate, IReadOnlyList<Zeitreihe> Reihen)
{
    /// <summary>Summe frei je Monat über alle Töpfe, die in dem Monat aktiv sind.</summary>
    public IReadOnlyList<decimal> Summe => [.. Monate.Select((_, i) => Reihen.Sum(r => r.Punkte[i].Aktiv ? r.Punkte[i].Frei : 0))];

    /// <summary>
    /// Rechnet die Zeitachse. Startwert eines Topfs ist frei heute plus seine Bindungen; Bindungen fließen im Monat ihrer Fälligkeit ab
    /// (überfällige im ersten Monat). Erwartet: das Sachmittelbudget jedes folgenden Januars (Wert des laufenden Jahres) und noch nicht
    /// freigegebene Maßnahmen zum Laufzeitbeginn. Nach dem Stichtag einer Maßnahme ist ihr Rest verfallen (0).
    /// </summary>
    /// <param name="zusaetzlich">Zusätzliche, nur angenommene Bindungen je Topf („Was wäre, wenn“).</param>
    /// <param name="budgetFolgejahr">Erwartetes Sachmittelbudget des Folgejahres je Kostenstelle (sonst das des laufenden Jahres).</param>
    public static Zeitachse Berechnen(IEnumerable<(Topf Topf, Topfkennzahlen Zahlen, IReadOnlyList<Bindung> Bindungen)> toepfe, DateOnly heute,
        IReadOnlyDictionary<Guid, IReadOnlyList<Bindung>>? zusaetzlich = null, IReadOnlyDictionary<Guid, decimal>? budgetFolgejahr = null)
    {
        ArgumentNullException.ThrowIfNull(toepfe);
        var liste = toepfe.ToList();
        var start = Bindungsrechnung.Monatsanfang(heute);
        var ende = new DateOnly(heute.Year + 1, 12, 1);
        foreach (var (t, _, _) in liste)
        {
            if (t.Stichtag is { } st && Bindungsrechnung.Monatsanfang(st) > ende)
            {
                ende = Bindungsrechnung.Monatsanfang(st);
            }
        }

        if (Bindungsrechnung.MonateZwischen(start, ende) > 36)
        {
            ende = start.AddMonths(35);
        }

        var monate = new List<DateOnly>();
        for (var m = start; m <= ende; m = m.AddMonths(1))
        {
            monate.Add(m);
        }

        var reihen = new List<Zeitreihe>();
        foreach (var (t, z, b) in liste)
        {
            var bindungen = b.Concat(zusaetzlich?.GetValueOrDefault(t.Id) ?? []).ToList();
            var zugaenge = new List<(DateOnly Datum, decimal Betrag)>();
            var stand = z.Frei + z.Gebunden;
            if (t.Art == Topfart.Kostenstelle)
            {
                var budget = budgetFolgejahr?.GetValueOrDefault(t.Id) is { } bf && bf > 0 ? bf : z.Budget ?? 0;
                for (var j = heute.Year + 1; budget > 0 && j <= ende.Year; j++)
                {
                    zugaenge.Add((new DateOnly(j, 1, 1), budget));
                }
            }
            else if (t.LaufzeitVon is { } von && von > heute)
            {
                stand -= t.Bewilligt;
                zugaenge.Add((von, t.Bewilligt));
            }

            var stichtag = t.Art == Topfart.Kostenstelle ? (DateOnly?)null : t.Stichtag;
            var punkte = new List<Zeitpunkt>();
            var erwartet = z.VortragErwartet is not null;
            var vorher = stand;
            foreach (var m in monate)
            {
                var monatsende = Bindungsrechnung.Monatsende(m);
                var aktiv = stichtag is null || m <= Bindungsrechnung.Monatsanfang(stichtag.Value);
                var abfluss = bindungen.Where(x => (m == start ? x.Faellig <= monatsende : x.Faellig >= m && x.Faellig <= monatsende)).Sum(x => x.Betrag);
                var zugang = zugaenge.Where(x => x.Datum >= m && x.Datum <= monatsende).Sum(x => x.Betrag);
                erwartet |= zugang > 0;
                var frei = vorher + zugang - abfluss;
                decimal? verfaellt = null, grenze = null, einzug = null;
                if (!aktiv)
                {
                    frei = 0;
                }
                else if (stichtag is { } st && Bindungsrechnung.Monatsanfang(st) == m && frei > 0)
                {
                    verfaellt = frei;
                }

                if (t.Art == Topfart.Kostenstelle && m.Month == 12)
                {
                    var budget = m.Year == heute.Year ? z.Budget : budgetFolgejahr?.GetValueOrDefault(t.Id) is { } bf && bf > 0 ? bf : z.Budget;
                    if (budget is > 0)
                    {
                        grenze = decimal.Round(budget.Value * t.UebertragsgrenzeProzent / 100m, 2);
                        einzug = frei > grenze ? frei - grenze : null;
                    }
                }

                punkte.Add(new(m, frei, erwartet, abfluss, zugang, verfaellt, grenze, einzug, aktiv));
                vorher = aktiv ? frei : 0;
            }

            reihen.Add(new(t, punkte));
        }

        return new(monate, reihen);
    }
}

/// <summary>Wofür eine neue Ausgabe ist (bestimmt die passenden Töpfe).</summary>
public enum Ausgabenzweck
{
    /// <summary>Verbrauchsmaterial, Kleingeräte, Bauteile: Kostenstelle und SQM mit Sachmitteln.</summary>
    Sachmittel,

    /// <summary>Gerät bzw. Ausstattung im Sinne eines Investitionsantrags: zusätzlich Investitionsmaßnahmen.</summary>
    Geraet,
}

/// <summary>Ein Anteil des Aufteilungsvorschlags mit Begründung.</summary>
public sealed record Aufteilungsanteil(Topf Topf, decimal Betrag, string Grund);

/// <param name="Fehlbetrag">Was im gewünschten Monat nicht gedeckt ist.</param>
/// <param name="FruehesterMonat">Erster Monat, ab dem die passenden Töpfe (mit erwarteten Zugängen) den Betrag decken – oder null.</param>
public sealed record Aufteilung(IReadOnlyList<Aufteilungsanteil> Anteile, decimal Fehlbetrag, DateOnly? FruehesterMonat, IReadOnlyList<string> Hinweise);

/// <summary>Vorschlag, eine neue Ausgabe auf Töpfe aufzuteilen (FA-717).</summary>
public static class Aufteilungsrechnung
{
    public const decimal Bagatellgrenze = 2000m;

    /// <summary>Monate vor dem Stichtag, in denen Mittel als „verfallen sonst“ zuerst eingesetzt werden.</summary>
    public const int VerfallVorlaufMonate = 2;

    public static bool Passt(Topf t, Ausgabenzweck zweck, DateOnly monat)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.Archiviert)
        {
            return false;
        }

        if (t.Art == Topfart.Investition && zweck != Ausgabenzweck.Geraet)
        {
            return false;
        }

        if (t.Art == Topfart.Sqm && t.BewilligtSach <= 0)
        {
            return false;
        }

        return t.Art == Topfart.Kostenstelle
               || ((t.LaufzeitVon is null || Bindungsrechnung.Monatsanfang(t.LaufzeitVon.Value) <= monat)
                   && (t.Stichtag is null || Bindungsrechnung.Monatsanfang(t.Stichtag.Value) >= monat));
    }

    /// <summary>Höchstens verplanbar ab <paramref name="monat"/>: kleinster freier Wert ab diesem Monat bis zum Ende (sonst würde Späteres überzogen).</summary>
    public static decimal Verfuegbar(Zeitreihe r, DateOnly monat)
    {
        ArgumentNullException.ThrowIfNull(r);
        var ab = r.Punkte.Where(p => p.Monat >= monat && p.Aktiv).ToList();
        return ab.Count == 0 ? 0 : Math.Max(0, ab.Min(p => p.Frei));
    }

    public static Aufteilung Vorschlagen(decimal betrag, Ausgabenzweck zweck, DateOnly monat, Zeitachse achse)
    {
        ArgumentNullException.ThrowIfNull(achse);
        var m = Bindungsrechnung.Monatsanfang(monat);
        var hinweise = new List<string>();
        if (betrag <= 0)
        {
            return new([], 0, null, ["Bitte einen Betrag angeben."]);
        }

        if (betrag >= Bagatellgrenze)
        {
            hinweise.Add($"Ab {Finanzrechnung.Euro(Bagatellgrenze)} (Bagatellgrenze) lässt sich dafür auch ein eigener Investitions- oder SQM-Antrag stellen "
                         + "(Investitionen im Januar/Februar, SQM im Oktober).");
        }

        foreach (var r in achse.Reihen.Where(r => r.Topf.Art == Topfart.Investition && !r.Topf.Archiviert && zweck == Ausgabenzweck.Geraet
                                                  && r.Topf.Stichtag is { } st && Bindungsrechnung.Monatsanfang(st) < m))
        {
            hinweise.Add($"{r.Topf.Kennung}: Bestellschluss {r.Topf.Stichtag:dd.MM.yyyy} liegt vor dem gewünschten Monat.");
        }

        // Kandidaten mit Rang: 0 = verfällt sonst bzw. Überhang über der Übertragsgrenze, 1 = zweckgebunden, 2 = Kostenstelle
        var stufen = new List<(Zeitreihe Reihe, int Rang, decimal Hoechstens, string Grund)>();
        foreach (var r in achse.Reihen.Where(r => Passt(r.Topf, zweck, m)))
        {
            var frei = Verfuegbar(r, m);
            if (frei <= 0)
            {
                continue;
            }

            var t = r.Topf;
            if (t.Art == Topfart.Kostenstelle)
            {
                var dez = r.Punkte.FirstOrDefault(p => p.Monat == new DateOnly(m.Year, 12, 1));
                var ueberhang = Math.Min(frei, dez?.EinzugMoeglich ?? 0);
                if (ueberhang > 0)
                {
                    stufen.Add((r, 0, ueberhang, $"Überhang über der Übertragsgrenze {m.Year}"));
                }

                if (frei - ueberhang > 0)
                {
                    stufen.Add((r, 2, frei - ueberhang, "frei verwendbare Kostenstelle"));
                }
            }
            else
            {
                var verfaelltBald = t.Stichtag is { } st && Bindungsrechnung.MonateZwischen(m, Bindungsrechnung.Monatsanfang(st)) <= VerfallVorlaufMonate + 1;
                stufen.Add((r, verfaelltBald ? 0 : 1, frei, verfaelltBald ? $"verfällt sonst am {t.Stichtag:dd.MM.yyyy}"
                    : t.Art == Topfart.Sqm ? "SQM (zweckgebunden, Laborausstattung)" : "Investitionsmaßnahme (zweckgebunden)"));
            }
        }

        var anteile = new Dictionary<Guid, (Topf Topf, decimal Betrag, List<string> Gruende)>();
        var rest = betrag;
        foreach (var s in stufen.OrderBy(s => s.Rang).ThenBy(s => s.Reihe.Topf.Stichtag ?? DateOnly.MaxValue).ThenBy(s => s.Reihe.Topf.Kennung, StringComparer.Ordinal))
        {
            if (rest <= 0)
            {
                break;
            }

            var nimm = Math.Min(rest, s.Hoechstens);
            rest -= nimm;
            if (anteile.TryGetValue(s.Reihe.Topf.Id, out var vorhanden))
            {
                vorhanden.Gruende.Add(s.Grund);
                anteile[s.Reihe.Topf.Id] = (vorhanden.Topf, vorhanden.Betrag + nimm, vorhanden.Gruende);
            }
            else
            {
                anteile[s.Reihe.Topf.Id] = (s.Reihe.Topf, nimm, [s.Grund]);
            }
        }

        DateOnly? fruehester = rest <= 0 ? m : null;
        if (rest > 0)
        {
            foreach (var spaeter in achse.Monate.Where(x => x > m))
            {
                var summe = achse.Reihen.Where(r => Passt(r.Topf, zweck, spaeter)).Sum(r => Verfuegbar(r, spaeter));
                if (summe >= betrag)
                {
                    fruehester = spaeter;
                    break;
                }
            }

            hinweise.Insert(0, $"Im gewünschten Monat fehlen {Finanzrechnung.Euro(rest)}."
                               + (fruehester is { } f ? $" Mit den erwarteten Zugängen reicht es ab {f:MM.yyyy}." : " Auch mit den erwarteten Zugängen reicht es in den nächsten Monaten nicht."));
        }

        return new([.. anteile.Values.Select(a => new Aufteilungsanteil(a.Topf, a.Betrag, string.Join("; ", a.Gruende)))], Math.Max(0, rest), fruehester, hinweise);
    }
}
