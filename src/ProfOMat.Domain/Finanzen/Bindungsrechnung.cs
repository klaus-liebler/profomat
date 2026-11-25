namespace ProfOMat.Domain.Finanzen;

/// <summary>Stundensätze der Fakultät IuI („Anzusetzende Haushaltsbelastungen“, Stand 3/2026) und Auswahl des geltenden Satzes.</summary>
public static class Stundensaetze
{
    public static IReadOnlyDictionary<Hilfskraftart, decimal> Fakultaet2026 { get; } = new Dictionary<Hilfskraftart, decimal>
    {
        [Hilfskraftart.StudentischeHilfskraft] = 19.50m,
        [Hilfskraftart.WissHilfskraftBachelor] = 20.28m,
        [Hilfskraftart.WissHilfskraftMaster] = 26.98m,
        [Hilfskraftart.TutorOhneAbschluss] = 15.20m,
        [Hilfskraftart.TutorBachelor] = 15.81m,
        [Hilfskraftart.TutorMaster] = 21.03m,
    };

    public static string Anzeige(Hilfskraftart art) => art switch
    {
        Hilfskraftart.StudentischeHilfskraft => "Studentische Hilfskraft",
        Hilfskraftart.WissHilfskraftBachelor => "Wiss. Hilfskraft (Bachelor)",
        Hilfskraftart.WissHilfskraftMaster => "Wiss. Hilfskraft (Master)",
        Hilfskraftart.TutorOhneAbschluss => "Tutor/in (ohne Abschluss)",
        Hilfskraftart.TutorBachelor => "Tutor/in (Bachelor)",
        _ => "Tutor/in (Master)",
    };

    /// <summary>Satz des Teams für das Jahr, sonst der letzte frühere des Teams, sonst der Fakultätswert 2026.</summary>
    public static decimal Satz(IEnumerable<Stundensatz> team, Hilfskraftart art, int jahr)
    {
        ArgumentNullException.ThrowIfNull(team);
        return team.Where(s => s.Art == art && s.Jahr <= jahr).OrderByDescending(s => s.Jahr).FirstOrDefault()?.EuroProStunde
               ?? Fakultaet2026[art];
    }
}

/// <summary>Ein Abschnitt eines Hilfskraft-Vorschlags: aus diesem Topf von … bis ….</summary>
public sealed record Hilfskraftabschnitt(Topf Topf, DateOnly Von, DateOnly Bis, int Monate, decimal Kosten);

/// <summary>Was ein Topf für Hilfskräfte noch hergibt (FA-709).</summary>
/// <param name="StundenProMonat">Je Art: Stunden je Monat, die der freie Betrag von Beginn bis Ende trägt (auf halbe Stunden abgerundet).</param>
public sealed record Einstellungsmoeglichkeit(Topf Topf, decimal Frei, DateOnly Beginn, DateOnly Ende, int Monate, IReadOnlyDictionary<Hilfskraftart, decimal> StundenProMonat);

/// <summary>Bindungen aus geplanten Ausgaben, Auslagen und Hilfskräften; Einstellungsmöglichkeiten für Hilfskräfte.</summary>
public static class Bindungsrechnung
{
    /// <summary>Tage nach der Einreichung, bis eine Auslage voraussichtlich erstattet (und gebucht) ist.</summary>
    public const int ErstattungTage = 30;

    public static DateOnly Monatsanfang(DateOnly d) => new(d.Year, d.Month, 1);

    public static DateOnly Monatsende(DateOnly d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    public static int MonateZwischen(DateOnly von, DateOnly bis) => ((bis.Year - von.Year) * 12) + bis.Month - von.Month + 1;

    /// <summary>Kosten eines Monats für einen Topf (Anteil in Prozent eingerechnet).</summary>
    public static decimal Monatskosten(Hilfskraft h, decimal prozent, Func<Hilfskraftart, int, decimal> satz, DateOnly monat)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(satz);
        return decimal.Round(h.StundenProMonat * satz(h.Art, monat.Year) * prozent / 100m, 2);
    }

    /// <param name="bezahltBis">Bis zu diesem Tag gilt der Ist-Stand (letzte Buchung bzw. Stichtag des Stands); Hilfskraft-Monate bis
    /// einschließlich dieses Monats gelten als ausgegeben und binden nichts mehr.</param>
    public static IReadOnlyList<Bindung> FuerTopf(Guid topfId, IEnumerable<GeplanteAusgabe> ausgaben, IEnumerable<Auslagenabrechnung> auslagen,
        IEnumerable<Hilfskraft> hilfskraefte, Func<Hilfskraftart, int, decimal> satz, DateOnly? bezahltBis, DateOnly heute)
    {
        ArgumentNullException.ThrowIfNull(ausgaben);
        ArgumentNullException.ThrowIfNull(auslagen);
        ArgumentNullException.ThrowIfNull(hilfskraefte);
        var liste = new List<Bindung>();
        foreach (var a in ausgaben.Where(a => a.Offen))
        {
            foreach (var anteil in a.Anteile.Where(x => x.TopfId == topfId))
            {
                liste.Add(new(anteil.Betrag, Monatsende(a.Monat), $"Geplant: {a.Bezeichnung}"));
            }
        }

        foreach (var ab in auslagen.Where(x => x.Offen))
        {
            var faellig = (ab.Eingereicht ?? heute).AddDays(ErstattungTage);
            foreach (var p in ab.Positionen.Where(p => p.TopfId == topfId))
            {
                liste.Add(new(p.Betrag, faellig, $"Auslage: {p.Artikel}"));
            }
        }

        var ab1 = bezahltBis is { } bb ? Monatsanfang(bb).AddMonths(1) : DateOnly.MinValue;
        foreach (var h in hilfskraefte)
        {
            foreach (var anteil in h.Anteile.Where(x => x.TopfId == topfId))
            {
                for (var m = h.Beginn; m <= h.Ende; m = m.AddMonths(1))
                {
                    if (m >= ab1)
                    {
                        liste.Add(new(Monatskosten(h, anteil.Prozent, satz, m), Monatsende(m), $"Hilfskraft {h.Person} {m:MM.yyyy}"));
                    }
                }
            }
        }

        return liste;
    }

    /// <summary>Darf der Topf Hilfskräfte bezahlen? Kostenstelle ja; SQM mit Personalmitteln; Investitionen nein.</summary>
    public static bool ErlaubtHilfskraefte(Topf t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return t.Art == Topfart.Kostenstelle || (t.Art == Topfart.Sqm && t.BewilligtPersonal > 0);
    }

    /// <summary>Letzter Monat, in dem der Topf Hilfskräfte bezahlen kann (Kostenstelle: Ende des Beginnjahres, falls nichts gewählt).</summary>
    public static DateOnly? LetzterMonat(Topf t, DateOnly beginn, DateOnly? gewuenscht)
    {
        ArgumentNullException.ThrowIfNull(t);
        var grenze = t.Art == Topfart.Kostenstelle ? (DateOnly?)null : t.LaufzeitBis ?? t.Stichtag;
        var ende = gewuenscht ?? grenze ?? new DateOnly(beginn.Year, 12, 1);
        if (grenze is { } g && ende > g)
        {
            ende = g;
        }

        return Monatsanfang(ende) < Monatsanfang(beginn) ? null : Monatsanfang(ende);
    }

    public static Einstellungsmoeglichkeit? Moeglichkeit(Topf t, decimal frei, DateOnly beginn, DateOnly? ende, Func<Hilfskraftart, int, decimal> satz)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(satz);
        if (!ErlaubtHilfskraefte(t) || t.Archiviert)
        {
            return null;
        }

        var b = Monatsanfang(beginn);
        var e = LetzterMonat(t, b, ende);
        var monate = e is { } x ? MonateZwischen(b, x) : 0;
        var stunden = Enum.GetValues<Hilfskraftart>().ToDictionary(a => a, a => monate <= 0 || frei <= 0 ? 0m
            : Math.Floor(frei / (monate * satz(a, b.Year)) * 2m) / 2m);
        return new(t, frei, b, e ?? b, monate, stunden);
    }

    /// <summary>
    /// Rechner (FA-709): Eine Hilfskraft mit <paramref name="stunden"/> je Monat ab <paramref name="beginn"/> – aus welchen Töpfen und bis wann
    /// reicht es? Zuerst Töpfe, die früher auslaufen (sonst verfällt ihr Geld), die Kostenstelle zuletzt. Je Abschnitt ganze Monate.
    /// </summary>
    public static IReadOnlyList<Hilfskraftabschnitt> Vorschlagen(IEnumerable<(Topf Topf, decimal Frei)> toepfe, Hilfskraftart art, decimal stunden,
        DateOnly beginn, DateOnly? ende, Func<Hilfskraftart, int, decimal> satz)
    {
        ArgumentNullException.ThrowIfNull(toepfe);
        ArgumentNullException.ThrowIfNull(satz);
        var abschnitte = new List<Hilfskraftabschnitt>();
        if (stunden <= 0)
        {
            return abschnitte;
        }

        var monat = Monatsanfang(beginn);
        var ziel = ende is { } z ? Monatsanfang(z) : (DateOnly?)null;
        var reihenfolge = toepfe.Where(x => ErlaubtHilfskraefte(x.Topf) && !x.Topf.Archiviert && x.Frei > 0)
            .OrderBy(x => x.Topf.Art == Topfart.Kostenstelle ? 1 : 0)
            .ThenBy(x => x.Topf.LaufzeitBis ?? DateOnly.MaxValue)
            .ToList();
        foreach (var (t, frei) in reihenfolge)
        {
            if (ziel is { } zz && monat > zz)
            {
                break;
            }

            var letzter = LetzterMonat(t, monat, ziel);
            if (letzter is null || (t.LaufzeitVon is { } von && Monatsanfang(von) > monat))
            {
                continue;
            }

            var rest = frei;
            var von1 = monat;
            var anzahl = 0;
            var kosten = 0m;
            while (monat <= letzter)
            {
                var k = decimal.Round(stunden * satz(art, monat.Year), 2);
                if (k > rest)
                {
                    break;
                }

                rest -= k;
                kosten += k;
                anzahl++;
                monat = monat.AddMonths(1);
            }

            if (anzahl > 0)
            {
                abschnitte.Add(new(t, von1, von1.AddMonths(anzahl - 1), anzahl, kosten));
            }
        }

        return abschnitte;
    }
}
