using System.Globalization;

namespace ProfOMat.Domain.Finanzen;

public enum Ampel
{
    Neutral,
    Gut,
    Warn,
    Krit,
}

/// <summary>Mittel, die schon verplant sind (geplante Ausgabe, Auslage, Hilfskraft-Monat), mit Fälligkeit.</summary>
public sealed record Bindung(decimal Betrag, DateOnly Faellig, string Text);

/// <summary>Kennzahlen eines Topfs (Rechenregeln siehe architektur.md „Finanzen“).</summary>
/// <param name="Verfuegbar">Zugänge (bei fehlender Vortragsbuchung inkl. <paramref name="VortragErwartet"/>) bzw. bewilligter Betrag.</param>
/// <param name="Datenstand">Spätestes Buchungsdatum bzw. Stichtag des letzten Stands.</param>
/// <param name="Budget">Nur Kostenstelle: „Budget Sachmittel Fak“ des Jahres.</param>
/// <param name="Grenze">Nur Kostenstelle, laufendes Jahr: höchster Übertrag ohne Einzugsrisiko.</param>
/// <param name="Prognose31Dez">Nur Kostenstelle, laufendes Jahr: voraussichtlicher Rest zum Jahresende.</param>
/// <param name="UeberGrenze">Betrag, der bis zum Jahresende noch auszugeben oder zu binden ist, um unter der Grenze zu bleiben.</param>
public sealed record Topfkennzahlen(
    decimal Verfuegbar,
    decimal? VortragErwartet,
    decimal Ausgegeben,
    decimal Gebunden,
    decimal Frei,
    DateOnly? Datenstand,
    decimal? Budget,
    decimal? Grenze,
    decimal? Prognose31Dez,
    decimal? UeberGrenze,
    bool GrenzeHervorheben,
    Ampel Ampel,
    IReadOnlyList<string> Hinweise);

/// <summary>Eine Frist im Cockpit.</summary>
/// <param name="Frei">Freier Betrag des betroffenen Topfs (falls die Frist einen Topf betrifft).</param>
public sealed record Frist(DateOnly Datum, string Text, Guid? TopfId, decimal? Frei);

/// <summary>Reine Rechenregeln für das Finanzcockpit.</summary>
public static class Finanzrechnung
{
    /// <summary>Ab diesem Abstand zum Stichtag warnt das Cockpit, wenn noch Mittel frei sind.</summary>
    public const int WarnTageVorStichtag = 60;

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Rest laut Auszug = −Σ Beträge.</summary>
    public static decimal Rest(IEnumerable<Buchung> buchungen) => -buchungen.Sum(b => b.Betrag);

    public static string Euro(decimal betrag) => betrag.ToString("#,##0.00 €", De);

    /// <param name="buchungen">Buchungen des Jahres <paramref name="jahr"/>.</param>
    /// <param name="restVorjahr">Rest des Vorjahres (falls importiert), für „Vortrag erwartet“.</param>
    public static Topfkennzahlen Kostenstelle(Topf topf, int jahr, IReadOnlyCollection<Buchung> buchungen, decimal? restVorjahr,
        IReadOnlyCollection<Bindung> bindungen, DateOnly heute)
    {
        ArgumentNullException.ThrowIfNull(topf);
        ArgumentNullException.ThrowIfNull(buchungen);
        ArgumentNullException.ThrowIfNull(bindungen);
        var hinweise = new List<string>();
        var zugaenge = -buchungen.Where(b => b.IstZugang).Sum(b => b.Betrag);
        var ausgegeben = buchungen.Where(b => !b.IstZugang).Sum(b => b.Betrag);
        decimal? vortragErwartet = null;
        if (!buchungen.Any(b => Kostenartgruppe.IstVortrag(b.Kostenart)) && restVorjahr is { } rv && rv != 0)
        {
            vortragErwartet = rv;
            hinweise.Add($"Der Vortrag aus {jahr - 1} ist noch nicht gebucht – erwartet wird der Endstand {Euro(rv)} (gebucht wird er meist zwischen Februar und Juni).");
        }

        var verfuegbar = zugaenge + (vortragErwartet ?? 0);
        var gebunden = bindungen.Sum(b => b.Betrag);
        var frei = verfuegbar - ausgegeben - gebunden;
        var budget = -buchungen.Where(b => Kostenartgruppe.IstBudget(b.Kostenart)).Sum(b => b.Betrag);
        if (buchungen.Count > 0 && budget == 0)
        {
            hinweise.Add($"Das Sachmittelbudget {jahr} ist noch nicht gebucht (Kostenart 930300, meist im Februar).");
        }

        decimal? grenze = null, prognose = null, ueber = null;
        var hervorheben = false;
        if (jahr == heute.Year && budget > 0)
        {
            var jahresende = new DateOnly(jahr, 12, 31);
            grenze = decimal.Round(budget * topf.UebertragsgrenzeProzent / 100m, 2);
            prognose = verfuegbar - ausgegeben - bindungen.Where(b => b.Faellig <= jahresende).Sum(b => b.Betrag);
            if (prognose > grenze)
            {
                ueber = prognose - grenze;
                hervorheben = heute.Month >= 7;
                hinweise.Add($"Übertragsgrenze: Zum 31.12. bleiben voraussichtlich {Euro(prognose.Value)} übrig, übertragen werden sicher nur "
                             + $"{Euro(grenze.Value)} ({topf.UebertragsgrenzeProzent.ToString("0.##", De)} % des Budgets). {Euro(ueber.Value)} bis zum Jahresende "
                             + "ausgeben oder fest einplanen – sonst kann der Überhang zentral eingezogen werden.");
            }
        }

        var ampel = frei < 0 ? Ampel.Krit : ueber > 0 ? Ampel.Warn : buchungen.Count == 0 ? Ampel.Neutral : Ampel.Gut;
        if (frei < 0)
        {
            hinweise.Insert(0, $"Überplant: {Euro(-frei)} mehr gebunden oder ausgegeben als verfügbar.");
        }

        var stand = buchungen.Count == 0 ? (DateOnly?)null : buchungen.Max(b => b.Buchungsdatum);
        return new(verfuegbar, vortragErwartet, ausgegeben, gebunden, frei, stand, budget == 0 ? null : budget, grenze, prognose, ueber,
            hervorheben, ampel, hinweise);
    }

    /// <summary>Investitions- oder SQM-Maßnahme: verfügbar = bewilligt, ausgegeben laut letztem Stand.</summary>
    /// <param name="einzelbuchungen">Von Hand erfasste Ausgaben; sie zählen zusätzlich, soweit sie nach dem Stichtag des Stands liegen.</param>
    public static Topfkennzahlen Massnahme(Topf topf, Massnahmenstand? stand, IReadOnlyCollection<Bindung> bindungen, DateOnly heute,
        IReadOnlyCollection<Einzelbuchung>? einzelbuchungen = null)
    {
        ArgumentNullException.ThrowIfNull(topf);
        ArgumentNullException.ThrowIfNull(bindungen);
        var hinweise = new List<string>();
        var danach = (einzelbuchungen ?? []).Where(e => stand is null || e.Datum > stand.Stichtag).ToList();
        var ausgegeben = (stand?.Verausgabt ?? 0) + danach.Sum(e => e.Betrag);
        var gebunden = bindungen.Sum(b => b.Betrag);
        var frei = topf.Bewilligt - ausgegeben - gebunden;
        var ampel = Ampel.Gut;
        if (stand is null && danach.Count == 0)
        {
            hinweise.Add("Noch kein Stand erfasst – „verausgabt“ gilt als 0 €.");
            ampel = Ampel.Neutral;
        }

        if (topf.LaufzeitVon is { } von && heute < von)
        {
            hinweise.Add($"Die Mittel werden zum {von:dd.MM.yyyy} freigegeben.");
            ampel = Ampel.Neutral;
        }

        if (topf.Stichtag is { } st)
        {
            var tage = st.DayNumber - heute.DayNumber;
            var wort = topf.Art == Topfart.Sqm ? "Rechnungsabgabe" : topf.Art == Topfart.Investition ? "Bestellschluss" : "Laufzeitende";
            if (tage < 0 && frei > 0)
            {
                hinweise.Add($"{wort} am {st:dd.MM.yyyy} ist vorbei – {Euro(frei)} sind nicht mehr einsetzbar.");
                ampel = Ampel.Krit;
            }
            else if (tage >= 0 && tage <= WarnTageVorStichtag && frei > 0)
            {
                hinweise.Add($"{wort} in {tage} Tagen ({st:dd.MM.yyyy}) – noch {Euro(frei)} frei.");
                ampel = Ampel.Warn;
            }
        }

        if (frei < 0)
        {
            hinweise.Insert(0, $"Überplant: {Euro(-frei)} mehr gebunden oder ausgegeben als bewilligt.");
            ampel = Ampel.Krit;
        }

        DateOnly? datenstand = danach.Count > 0 ? danach.Max(e => e.Datum) : stand?.Stichtag;
        if (stand is not null && danach.Count > 0 && datenstand < stand.Stichtag)
        {
            datenstand = stand.Stichtag;
        }

        return new(topf.Bewilligt, null, ausgegeben, gebunden, frei, datenstand, null, null, null, null, false, ampel, hinweise);
    }

    /// <summary>Fristen der nächsten 12 Monate: Stichtage der Töpfe, Jahresende der Kostenstellen und das Zeitraster der Fakultät.</summary>
    public static IReadOnlyList<Frist> Fristen(IEnumerable<(Topf Topf, Topfkennzahlen Zahlen)> toepfe, DateOnly heute)
    {
        ArgumentNullException.ThrowIfNull(toepfe);
        var bis = heute.AddMonths(12);
        var liste = new List<Frist>();
        foreach (var (t, z) in toepfe.Where(x => !x.Topf.Archiviert))
        {
            if (t.Art == Topfart.Kostenstelle)
            {
                liste.Add(new(new DateOnly(heute.Year, 12, 31), $"Jahresende {t.Anzeige}: Rest wird vorgetragen"
                    + (z.Grenze is { } g ? $" (sicher bis {Euro(g)})" : ""), t.Id, z.Frei));
            }
            else if (t.Stichtag is { } st && st >= heute)
            {
                liste.Add(new(st, t.Art == Topfart.Sqm ? $"Rechnungsabgabe {t.Kennung} (SQM)" : $"Bestellschluss {t.Kennung} (Investition)", t.Id, z.Frei));
            }

            if (t.Art != Topfart.Kostenstelle && t.LaufzeitVon is { } von && von > heute)
            {
                liste.Add(new(von, $"Mittelfreigabe {t.Kennung}", t.Id, null));
            }
        }

        foreach (var jahr in new[] { heute.Year, heute.Year + 1 })
        {
            liste.Add(new(new DateOnly(jahr, 10, 1), $"SQM-Anträge für {jahr + 1} (Sommer- und Wintersemester) im Oktober einreichen", null, null));
            liste.Add(new(new DateOnly(jahr, 1, 15), $"Investitionsanträge {jahr} erarbeiten (Januar/Februar, Bagatellgrenze 2.000 €)", null, null));
        }

        return [.. liste.Where(f => f.Datum >= heute && f.Datum <= bis).OrderBy(f => f.Datum).ThenBy(f => f.Text, StringComparer.Ordinal)];
    }
}
