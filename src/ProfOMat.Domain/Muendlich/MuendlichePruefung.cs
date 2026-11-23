using System.Globalization;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Klausuren;

namespace ProfOMat.Domain.Muendlich;

/// <summary>Stand einer gestellten Frage.</summary>
public enum FrageStand
{
    /// <summary>Gestellt, noch ohne Punkte.</summary>
    Offen,

    /// <summary>Die/der Studierende möchte die Frage später beantworten.</summary>
    Zurueckgestellt,

    Bewertet,
}

/// <summary>Eine Zeile des Live-Protokolls: Fragennummer, Höchstpunktzahl, erreichte Punkte.</summary>
public sealed class GestellteFrage
{
    public const int NummerMaxLaenge = 20;

    private GestellteFrage()
    {
    }

    internal GestellteFrage(Guid pruefungId, int position, string nummer, decimal maxPunkte)
    {
        Id = Ids.Neu();
        PruefungId = pruefungId;
        Position = position;
        Nummer = nummer;
        MaxPunkte = maxPunkte;
    }

    public Guid Id { get; private set; }

    public Guid PruefungId { get; private set; }

    /// <summary>Reihenfolge, in der die Fragen gestellt wurden.</summary>
    public int Position { get; private set; }

    /// <summary>Fragennummer aus dem Fragenkatalog (frei, z. B. „12“ oder „3b“).</summary>
    public string Nummer { get; private set; } = string.Empty;

    public decimal MaxPunkte { get; private set; }

    public decimal? Punkte { get; private set; }

    /// <summary>Wurde (irgendwann) zurückgestellt; bleibt nach dem Nachholen als Vermerk stehen.</summary>
    public bool Zurueckgestellt { get; private set; }

    public FrageStand Stand => Punkte is not null ? FrageStand.Bewertet : Zurueckgestellt ? FrageStand.Zurueckgestellt : FrageStand.Offen;

    internal void Aendern(string nummer, decimal maxPunkte)
    {
        if (Punkte is { } p && p > maxPunkte)
        {
            throw new FachlicherFehler($"Frage {nummer}: Es sind schon {MuendlichePruefung.Zahl(p)} Punkte vergeben, das Maximum kann nicht darunter liegen.");
        }

        (Nummer, MaxPunkte) = (nummer, maxPunkte);
    }

    internal void Bewerten(decimal? punkte, bool zurueckstellen)
    {
        if (zurueckstellen)
        {
            (Punkte, Zurueckgestellt) = (null, true);
            return;
        }

        if (punkte is { } p && (p < 0 || p > MaxPunkte))
        {
            throw new FachlicherFehler($"Frage {Nummer}: Punkte zwischen 0 und {MuendlichePruefung.Zahl(MaxPunkte)}.");
        }

        Punkte = punkte;
        if (punkte is null)
        {
            Zurueckgestellt = false;   // Feld geleert: wieder einfach „offen“
        }
    }
}

/// <summary>
/// Live-Protokoll einer mündlichen Prüfung: Die protokollierende Person trägt Zeile für Zeile die gestellten Fragen mit
/// Höchst- und erreichter Punktzahl ein; die Note nach dem Standard-Notenschlüssel wird laufend berechnet.
/// </summary>
public sealed class MuendlichePruefung
{
    public const int BezeichnungMaxLaenge = 200;
    public const decimal MaxPunkteGrenze = 1000m;

    private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

    private readonly List<GestellteFrage> fragen = [];

    private MuendlichePruefung()
    {
    }

    public MuendlichePruefung(Guid kontoId, string bezeichnung, DateTimeOffset begonnen)
    {
        Id = Ids.Neu();
        KontoId = kontoId;
        Begonnen = begonnen;
        Umbenennen(bezeichnung);
    }

    public Guid Id { get; private set; }

    /// <summary>Wer protokolliert (nur diese Person sieht das Protokoll).</summary>
    public Guid KontoId { get; private set; }

    /// <summary>Freitext, z. B. Prüfung und Kandidat/in.</summary>
    public string Bezeichnung { get; private set; } = string.Empty;

    public DateTimeOffset Begonnen { get; private set; }

    public IReadOnlyList<GestellteFrage> Fragen => fragen;

    public IEnumerable<GestellteFrage> FragenSortiert => fragen.OrderBy(f => f.Position);

    /// <summary>Summe der erreichten Punkte (bewertete Fragen).</summary>
    public decimal Erreicht => fragen.Sum(f => f.Punkte ?? 0m);

    /// <summary>Summe der Höchstpunkte der bewerteten Fragen – Bezug für die aktuelle Note.</summary>
    public decimal MoeglichBewertet => fragen.Where(f => f.Punkte is not null).Sum(f => f.MaxPunkte);

    /// <summary>Summe der Höchstpunkte aller gestellten Fragen (offene und zurückgestellte zählen mit).</summary>
    public decimal MoeglichGesamt => fragen.Sum(f => f.MaxPunkte);

    /// <summary>Note über die bewerteten Fragen.</summary>
    public Notenstand Aktuell => Notenstand.Berechnen(Erreicht, MoeglichBewertet);

    /// <summary>Note, falls offene und zurückgestellte Fragen ohne Punkte bleiben.</summary>
    public Notenstand OhneOffene => Notenstand.Berechnen(Erreicht, MoeglichGesamt);

    public void Umbenennen(string bezeichnung)
    {
        var b = bezeichnung?.Trim() ?? "";
        if (b.Length == 0)
        {
            throw new FachlicherFehler("Bitte eine Bezeichnung angeben (z. B. Prüfung und Datum).");
        }

        Bezeichnung = b.Length > BezeichnungMaxLaenge ? b[..BezeichnungMaxLaenge] : b;
    }

    public GestellteFrage FrageHinzufuegen(string nummer, decimal maxPunkte)
    {
        var f = new GestellteFrage(Id, fragen.Count == 0 ? 1 : fragen.Max(x => x.Position) + 1, NummerPruefen(nummer), MaxPruefen(maxPunkte));
        fragen.Add(f);
        return f;
    }

    public void FrageAendern(Guid frageId, string nummer, decimal maxPunkte) => Frage(frageId).Aendern(NummerPruefen(nummer), MaxPruefen(maxPunkte));

    /// <param name="punkte"><c>null</c> = noch keine Punkte.</param>
    /// <param name="zurueckstellen">Die/der Studierende stellt die Frage zurück (Punkte werden dabei geleert).</param>
    public void FrageBewerten(Guid frageId, decimal? punkte, bool zurueckstellen) => Frage(frageId).Bewerten(punkte, zurueckstellen);

    public void FrageEntfernen(Guid frageId) => fragen.Remove(Frage(frageId));

    /// <summary>Offene oder zurückgestellte Frage mit dieser Nummer (zum Wiederaufnehmen), sonst <c>null</c>.</summary>
    public GestellteFrage? Unbewertet(string nummer)
        => FragenSortiert.FirstOrDefault(f => f.Punkte is null && string.Equals(f.Nummer, nummer.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Zahl wie in der Eingabe: Komma oder Punkt, höchstens zwei Nachkommastellen; <c>null</c> = keine Zahl.</summary>
    public static decimal? ZahlLesen(string? eingabe)
        => decimal.TryParse(eingabe?.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && d == Math.Round(d, 2)
            ? d
            : null;

    public static string Zahl(decimal d) => d.ToString("0.##", Deutsch);

    private GestellteFrage Frage(Guid frageId) => fragen.FirstOrDefault(f => f.Id == frageId) ?? throw new FachlicherFehler("Frage nicht gefunden.");

    private static string NummerPruefen(string nummer)
    {
        var n = nummer?.Trim() ?? "";
        return n.Length switch
        {
            0 => throw new FachlicherFehler("Bitte die Fragennummer angeben."),
            > GestellteFrage.NummerMaxLaenge => throw new FachlicherFehler($"Die Fragennummer ist zu lang (höchstens {GestellteFrage.NummerMaxLaenge} Zeichen)."),
            _ => n,
        };
    }

    private static decimal MaxPruefen(decimal max) => max is <= 0 or > MaxPunkteGrenze
        ? throw new FachlicherFehler($"Die Höchstpunktzahl muss größer als 0 und höchstens {Zahl(MaxPunkteGrenze)} sein.")
        : max;
}

/// <summary>
/// Note nach dem Standard-Notenschlüssel (<see cref="Notenschluessel.Standard"/>, Grenzen in Prozent: 49,5 % → 4,0 … 94,5 % → 1,0).
/// Anders als bei Klausuren wird nicht auf halbe Punkte umgerechnet: Während der Prüfung ist die Summe oft klein (z. B. 5 Punkte),
/// dann fielen gerundete Grenzen zusammen.
/// </summary>
/// <param name="Note"><c>null</c>, solange nichts bewertet ist.</param>
/// <param name="NaechsteNote">Nächstbessere Note; <c>null</c> bei 1,0 oder ohne Bewertung.</param>
/// <param name="FehlendePunkte">Punkte bis zur nächstbesseren Note (bezogen auf <paramref name="Moeglich"/>, auf 0,01 aufgerundet).</param>
public sealed record Notenstand(decimal Erreicht, decimal Moeglich, decimal? Note, decimal? Prozent, decimal? NaechsteNote, decimal? FehlendePunkte)
{
    public static Notenstand Berechnen(decimal erreicht, decimal moeglich)
    {
        if (moeglich <= 0)
        {
            return new Notenstand(erreicht, moeglich, null, null, null, null);
        }

        var prozent = erreicht / moeglich * Notenschluessel.StandardBezug;
        var note = Notenschluessel.NoteFuer(Notenschluessel.Standard, prozent);
        var naechste = Notenschluessel.Standard.Where(s => s.Mindestpunkte > prozent).OrderBy(s => s.Mindestpunkte).FirstOrDefault();
        decimal? fehlt = naechste is null ? null : Math.Ceiling(((naechste.Mindestpunkte * moeglich / Notenschluessel.StandardBezug) - erreicht) * 100m) / 100m;
        return new Notenstand(erreicht, moeglich, note, Math.Round(prozent, 1), naechste?.Note, fehlt);
    }
}
