namespace ProfOMat.Domain.Klausuren;

/// <summary>Ergebniszeile (für Veröffentlichung nur mit Klausur-ID).</summary>
public sealed record Ergebniszeile(Teilnahme Teilnahme, bool Anwesend, IReadOnlyList<decimal?> Punkte, decimal Summe, decimal? Note);

/// <summary>
/// Statistik wie die bisherige Excel-Vorlage. Mit * gekennzeichnete Werte beziehen sich auf die aktiv Teilnehmenden
/// (mehr als <see cref="Klausur.AktivSchwelle"/> Punkte).
/// </summary>
public sealed record KlausurAuswertung(
    int Angemeldet,
    int Anwesend,
    int Aktiv,
    decimal? Durchfallquote,
    decimal? Durchschnittsnote,
    decimal? Maximalpunktzahl,
    decimal? Minimalpunktzahl,
    IReadOnlyDictionary<int, int> AnzahlGanzeNote,
    IReadOnlyDictionary<decimal, int> AnzahlNote,
    IReadOnlyList<Ergebniszeile> Zeilen,
    bool Vollstaendig)
{
    /// <summary>Ganze Notenstufe: 1,0/1,3 → 1; 1,7/2,0/2,3 → 2; 2,7/3,0/3,3 → 3; 3,7/4,0 → 4; 5,0 → 5.</summary>
    public static int Ganz(decimal note) => note switch
    {
        < 1.5m => 1,
        < 2.5m => 2,
        < 3.5m => 3,
        <= 4.0m => 4,
        _ => 5,
    };

    public static KlausurAuswertung Berechnen(Klausur k)
    {
        ArgumentNullException.ThrowIfNull(k);
        var aufgaben = k.AufgabenSortiert.ToList();
        var zeilen = k.Angemeldete.OrderBy(t => t.Code, StringComparer.Ordinal)
            .Select(t => new Ergebniszeile(t, t.Anwesend == true, aufgaben.Select(a => t.PunkteIn(a.Id)).ToList(), t.Summe, k.NoteVon(t)))
            .ToList();
        var anwesend = zeilen.Where(z => z.Anwesend).ToList();
        var aktiv = anwesend.Where(z => z.Summe > k.AktivSchwelle).ToList();
        var noten = aktiv.Select(z => z.Note!.Value).ToList();
        return new KlausurAuswertung(
            zeilen.Count,
            anwesend.Count,
            aktiv.Count,
            aktiv.Count == 0 ? null : Math.Round((decimal)noten.Count(n => n >= 5.0m) / aktiv.Count, 4),
            aktiv.Count == 0 ? null : Math.Round(noten.Average(), 2),
            aktiv.Count == 0 ? null : aktiv.Max(z => z.Summe),
            anwesend.Count == 0 ? null : anwesend.Min(z => z.Summe),
            Enumerable.Range(1, 5).ToDictionary(g => g, g => noten.Count(n => Ganz(n) == g)),
            Notenschluessel.ErlaubteNoten.ToDictionary(n => n, n => noten.Count(x => x == n)),
            zeilen,
            zeilen.All(z => z.Teilnahme.Anwesend is not null) && anwesend.All(z => z.Punkte.All(p => p is not null)));
    }
}
