using System.Globalization;
using System.Text.RegularExpressions;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Beschriftung;

/// <summary>Gerätearten im Verteiler (Reiheneinbaugeräte).</summary>
public enum Geraetetyp
{
    /// <summary>Leitungsschutzschalter.</summary>
    LS,

    /// <summary>Fehlerstromschutzschalter.</summary>
    FI,

    /// <summary>FI/LS-Kombination.</summary>
    RCBO,

    Zaehler,

    Automation,

    Schuetz,

    /// <summary>Digitaler Ausgangsbaustein.</summary>
    Ausgang,

    /// <summary>Digitaler Eingangsbaustein.</summary>
    Eingang,

    Sonstiges,
}

/// <param name="Breite">Übliche Breite in Teilungseinheiten (änderbar).</param>
/// <param name="Praefix">Üblicher Kennbuchstabe (nur als Eingabehilfe; das Kennzeichen selbst kommt aus dem Stromlaufplan).</param>
public sealed record Geraeteart(Geraetetyp Typ, string Name, string Kurz, int Breite, string Hintergrund, string Praefix);

/// <summary>Voreinstellungen je Geräteart.</summary>
public static class Geraetearten
{
    public static IReadOnlyList<Geraeteart> Alle { get; } =
    [
        new(Geraetetyp.LS, "Leitungsschutzschalter", "LS", 1, "#FFFFFF", "F"),
        new(Geraetetyp.FI, "Fehlerstromschutzschalter", "FI", 4, "#FFF3B0", "F"),
        new(Geraetetyp.RCBO, "FI/LS-Kombination", "RCBO", 2, "#FFE4C2", "F"),
        new(Geraetetyp.Zaehler, "Zähler", "Zähler", 4, "#DDEBFF", "P"),
        new(Geraetetyp.Automation, "Automationsstation", "Automation", 4, "#E7DDFF", "A"),
        new(Geraetetyp.Schuetz, "Schaltschütz", "Schütz", 2, "#DDF5E3", "Q"),
        new(Geraetetyp.Ausgang, "Digitaler Ausgangsbaustein", "DA", 4, "#D7F3F5", "K"),
        new(Geraetetyp.Eingang, "Digitaler Eingangsbaustein", "DE", 4, "#F0F0F0", "K"),
        new(Geraetetyp.Sonstiges, "Sonstiges Gerät", "Sonstiges", 1, "#FFFFFF", ""),
    ];

    public static Geraeteart Von(Geraetetyp typ) => Alle.First(a => a.Typ == typ);
}

/// <summary>Symbole für die Beschriftung (Zeichnung: <see cref="Symbolzeichnung"/>).</summary>
public enum Symbol
{
    Keins,
    Steckdose,
    Licht,
    Herd,
    Backofen,
    Waschmaschine,
    Trockner,
    Spuelmaschine,
    Kuehlen,
    Heizung,
    Warmwasser,
    Lueftung,
    Pumpe,
    Motor,
    Rollladen,
    Klingel,
    Netzwerk,
    Computer,
    Garten,
    Haus,
    Solar,
    Elektroauto,
    Blitz,
}

/// <summary>Ein Reiheneinbaugerät mit seiner Beschriftung.</summary>
public sealed class Geraet
{
    public const int TextMaxLaenge = 60;
    public const int KennzeichenMaxLaenge = 12;

    private Geraet()
    {
    }

    internal Geraet(Guid feldId, int reihe, int start, Geraetetyp typ, int breite, string? kennzeichen)
    {
        Id = Ids.Neu();
        FeldId = feldId;
        (Reihe, Start, Typ, Breite, Kennzeichen) = (reihe, start, typ, breite, kennzeichen);
        Hintergrund = Geraetearten.Von(typ).Hintergrund;
    }

    public Guid Id { get; private set; }

    public Guid FeldId { get; private set; }

    /// <summary>Reihe im Feld, ab 1 (oben).</summary>
    public int Reihe { get; private set; }

    /// <summary>Erste belegte Teilungseinheit, ab 0 (links).</summary>
    public int Start { get; private set; }

    /// <summary>Breite in Teilungseinheiten.</summary>
    public int Breite { get; private set; }

    public int Ende => Start + Breite;

    public Geraetetyp Typ { get; private set; }

    /// <summary>Betriebsmittelkennzeichen, z. B. „F3“ (klein oben links).</summary>
    public string? Kennzeichen { get; private set; }

    /// <summary>Haupttext, z. B. „Küche Steckdosen“ (wird bei Bedarf umbrochen und verkleinert).</summary>
    public string? Text { get; private set; }

    /// <summary>Zweite, kleinere Zeile, z. B. „B16“ oder „30 mA“.</summary>
    public string? Zusatz { get; private set; }

    public Symbol Symbol { get; private set; }

    /// <summary>Hintergrundfarbe „#RRGGBB“.</summary>
    public string Hintergrund { get; private set; } = "#FFFFFF";

    /// <summary>Schriftfarbe „#RRGGBB“.</summary>
    public string Schrift { get; private set; } = "#000000";

    internal void Platzieren(Guid feldId, int reihe, int start) => (FeldId, Reihe, Start) = (feldId, reihe, start);

    internal void Beschriften(Geraetetyp typ, int breite, string? kennzeichen, string? text, string? zusatz, Symbol symbol, string hintergrund, string schrift)
    {
        if (typ != Typ && string.Equals(Hintergrund, Geraetearten.Von(Typ).Hintergrund, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(hintergrund, Hintergrund, StringComparison.OrdinalIgnoreCase))
        {
            hintergrund = Geraetearten.Von(typ).Hintergrund;   // Farbe war die Voreinstellung der alten Art → die der neuen nehmen
        }

        (Typ, Breite) = (typ, breite);
        Kennzeichen = Schaltschrank.Kuerzen(kennzeichen, KennzeichenMaxLaenge);
        Text = Schaltschrank.Kuerzen(text, TextMaxLaenge);
        Zusatz = Schaltschrank.Kuerzen(zusatz, TextMaxLaenge);
        Symbol = Enum.IsDefined(symbol) ? symbol : Symbol.Keins;
        Hintergrund = Schaltschrank.Farbe(hintergrund, "Hintergrundfarbe");
        Schrift = Schaltschrank.Farbe(schrift, "Schriftfarbe");
    }

    internal Geraet Kopie(int start)
    {
        var k = new Geraet(FeldId, Reihe, start, Typ, Breite, null);
        k.Beschriften(Typ, Breite, null, Text, Zusatz, Symbol, Hintergrund, Schrift);
        return k;
    }
}

/// <summary>Ein Feld (Verteilerfeld) mit einer Anzahl Reihen; jede Reihe hat <see cref="Schaltschrank.TeProReihe"/> Teilungseinheiten.</summary>
public sealed class Feld
{
    public const int BezeichnungMaxLaenge = 60;
    public const int MaxReihen = 12;

    private readonly List<Geraet> geraete = [];

    private Feld()
    {
    }

    internal Feld(Guid schrankId, int position, int reihen)
    {
        Id = Ids.Neu();
        SchrankId = schrankId;
        Position = position;
        Reihen = reihen;
    }

    public Guid Id { get; private set; }

    public Guid SchrankId { get; private set; }

    /// <summary>Reihenfolge von links nach rechts.</summary>
    public int Position { get; private set; }

    public string? Bezeichnung { get; private set; }

    public int Reihen { get; private set; }

    public IReadOnlyList<Geraet> Geraete => geraete;

    public IEnumerable<Geraet> InReihe(int reihe) => geraete.Where(g => g.Reihe == reihe).OrderBy(g => g.Start);

    internal void Aendern(string? bezeichnung, int reihen)
    {
        if (reihen is < 1 or > MaxReihen)
        {
            throw new FachlicherFehler($"Ein Feld hat 1 bis {MaxReihen} Reihen.");
        }

        if (geraete.FirstOrDefault(g => g.Reihe > reihen) is { } weg)
        {
            throw new FachlicherFehler($"In Reihe {weg.Reihe} sind noch Geräte – erst entfernen oder verschieben.");
        }

        Bezeichnung = Schaltschrank.Kuerzen(bezeichnung, BezeichnungMaxLaenge);
        Reihen = reihen;
    }

    internal void Hinzufuegen(Geraet g) => geraete.Add(g);

    internal void Entfernen(Geraet g) => geraete.Remove(g);
}

/// <summary>
/// Schaltschrank bzw. Verteiler zum Beschriften: Felder nebeneinander, je Feld Reihen, je Reihe gleich viele Teilungseinheiten (TE).
/// Geräte belegen zusammenhängende TE einer Reihe und dürfen sich nicht überlappen. Gehört genau einem Konto.
/// </summary>
public sealed partial class Schaltschrank
{
    public const int BezeichnungMaxLaenge = 100;
    public const int MaxFelder = 12;

    private readonly List<Feld> felder = [];

    private Schaltschrank()
    {
    }

    /// <param name="teProReihe">Teilungseinheiten je Reihe (üblich 12).</param>
    /// <param name="teBreiteMm">Breite einer TE in mm (üblich 17,5 oder 18).</param>
    /// <param name="hoeheMm">Höhe des Beschriftungsstreifens in mm.</param>
    public Schaltschrank(Guid kontoId, string bezeichnung, DateTimeOffset angelegt, int teProReihe = 12, decimal teBreiteMm = 18m, decimal hoeheMm = 10m)
    {
        Id = Ids.Neu();
        KontoId = kontoId;
        Angelegt = angelegt;
        Einstellen(bezeichnung, teProReihe, teBreiteMm, hoeheMm);
        FeldHinzufuegen(4);
    }

    public Guid Id { get; private set; }

    public Guid KontoId { get; private set; }

    public string Bezeichnung { get; private set; } = string.Empty;

    public DateTimeOffset Angelegt { get; private set; }

    /// <summary>Teilungseinheiten je Reihe (üblich 12).</summary>
    public int TeProReihe { get; private set; }

    /// <summary>Breite einer Teilungseinheit in mm (üblich 17,5–18).</summary>
    public decimal TeBreiteMm { get; private set; }

    /// <summary>Höhe des Beschriftungsstreifens in mm.</summary>
    public decimal HoeheMm { get; private set; }

    public decimal ReiheBreiteMm => TeProReihe * TeBreiteMm;

    public IReadOnlyList<Feld> Felder => felder;

    public IEnumerable<Feld> FelderSortiert => felder.OrderBy(f => f.Position);

    public IEnumerable<Geraet> AlleGeraete => felder.SelectMany(f => f.Geraete);

    public void Einstellen(string bezeichnung, int teProReihe, decimal teBreiteMm, decimal hoeheMm)
    {
        var b = Kuerzen(bezeichnung, BezeichnungMaxLaenge) ?? throw new FachlicherFehler("Bitte eine Bezeichnung angeben (z. B. „Hauptverteiler Keller“).");
        if (teProReihe is < 1 or > 48)
        {
            throw new FachlicherFehler("Eine Reihe hat 1 bis 48 Teilungseinheiten.");
        }

        if (teBreiteMm is < 9m or > 36m)
        {
            throw new FachlicherFehler("Die Breite einer Teilungseinheit liegt zwischen 9 und 36 mm (üblich 17,5 oder 18 mm).");
        }

        if (hoeheMm is < 5m or > 40m)
        {
            throw new FachlicherFehler("Die Streifenhöhe liegt zwischen 5 und 40 mm.");
        }

        if (AlleGeraete.FirstOrDefault(g => g.Ende > teProReihe) is { } zuBreit)
        {
            throw new FachlicherFehler($"Ein Gerät reicht bis TE {zuBreit.Ende} – erst verschieben oder entfernen.");
        }

        (Bezeichnung, TeProReihe, TeBreiteMm, HoeheMm) = (b, teProReihe, teBreiteMm, hoeheMm);
    }

    public Feld FeldHinzufuegen(int reihen)
    {
        if (felder.Count >= MaxFelder)
        {
            throw new FachlicherFehler($"Höchstens {MaxFelder} Felder.");
        }

        var f = new Feld(Id, felder.Count == 0 ? 1 : felder.Max(x => x.Position) + 1, 1);
        f.Aendern(null, reihen);
        felder.Add(f);
        return f;
    }

    public void FeldAendern(Guid feldId, string? bezeichnung, int reihen) => FeldVon(feldId).Aendern(bezeichnung, reihen);

    public void FeldEntfernen(Guid feldId)
    {
        if (felder.Count == 1)
        {
            throw new FachlicherFehler("Mindestens ein Feld bleibt.");
        }

        felder.Remove(FeldVon(feldId));
    }

    /// <summary>Neues Gerät mit den Voreinstellungen seiner Art an einer freien Stelle, ohne Kennzeichen (das steht im Stromlaufplan).</summary>
    public Geraet GeraetEinsetzen(Guid feldId, int reihe, int start, Geraetetyp typ)
    {
        var feld = FeldVon(feldId);
        var art = Geraetearten.Von(typ);
        var breite = Math.Min(art.Breite, TeProReihe);
        PlatzPruefen(feld, reihe, start, breite, null);
        var g = new Geraet(feldId, reihe, start, typ, breite, null);
        feld.Hinzufuegen(g);
        return g;
    }

    public void GeraetBeschriften(Guid geraetId, Geraetetyp typ, int breite, string? kennzeichen, string? text, string? zusatz, Symbol symbol,
        string hintergrund, string schrift)
    {
        var (feld, g) = GeraetVon(geraetId);
        PlatzPruefen(feld, g.Reihe, g.Start, breite, g);
        g.Beschriften(typ, breite, kennzeichen, text, zusatz, symbol, hintergrund, schrift);
    }

    public void GeraetVerschieben(Guid geraetId, Guid feldId, int reihe, int start)
    {
        var (alt, g) = GeraetVon(geraetId);
        var ziel = FeldVon(feldId);
        PlatzPruefen(ziel, reihe, start, g.Breite, g);
        if (alt != ziel)
        {
            alt.Entfernen(g);
            ziel.Hinzufuegen(g);
        }

        g.Platzieren(feldId, reihe, start);
    }

    /// <summary>Kopie rechts daneben (erste freie Lücke rechts in derselben Reihe).</summary>
    public Geraet GeraetKopieren(Guid geraetId)
    {
        var (feld, g) = GeraetVon(geraetId);
        for (var s = g.Ende; s + g.Breite <= TeProReihe; s++)
        {
            if (Frei(feld, g.Reihe, s, g.Breite, null))
            {
                var k = g.Kopie(s);
                feld.Hinzufuegen(k);
                return k;
            }
        }

        throw new FachlicherFehler($"Rechts daneben ist in Reihe {g.Reihe} kein Platz für {g.Breite} TE.");
    }

    public void GeraetEntfernen(Guid geraetId)
    {
        var (feld, g) = GeraetVon(geraetId);
        feld.Entfernen(g);
    }

    /// <summary>Freie TE ab <paramref name="start"/> nach rechts (bis zum nächsten Gerät oder Reihenende).</summary>
    public int FreiAb(Guid feldId, int reihe, int start)
    {
        var feld = FeldVon(feldId);
        var naechstes = feld.InReihe(reihe).Where(g => g.Ende > start).Select(g => g.Start).DefaultIfEmpty(TeProReihe).Min();
        return Math.Max(0, naechstes - start);
    }

    private void PlatzPruefen(Feld feld, int reihe, int start, int breite, Geraet? ausser)
    {
        if (reihe < 1 || reihe > feld.Reihen)
        {
            throw new FachlicherFehler($"Das Feld hat nur {feld.Reihen} Reihen.");
        }

        if (breite < 1 || breite > TeProReihe)
        {
            throw new FachlicherFehler($"Ein Gerät ist 1 bis {TeProReihe} TE breit.");
        }

        if (start < 0 || start + breite > TeProReihe)
        {
            throw new FachlicherFehler($"Das passt nicht in die Reihe: {breite} TE ab Platz {start + 1}, die Reihe hat {TeProReihe} TE.");
        }

        if (!Frei(feld, reihe, start, breite, ausser))
        {
            var belegt = feld.InReihe(reihe).First(g => g != ausser && g.Start < start + breite && start < g.Ende);
            throw new FachlicherFehler($"Dort ist schon {belegt.Kennzeichen ?? Geraetearten.Von(belegt.Typ).Kurz} – {breite} TE ab Platz {start + 1} sind nicht frei.");
        }
    }

    private static bool Frei(Feld feld, int reihe, int start, int breite, Geraet? ausser)
        => !feld.InReihe(reihe).Any(g => g != ausser && g.Start < start + breite && start < g.Ende);

    private Feld FeldVon(Guid feldId) => felder.FirstOrDefault(f => f.Id == feldId) ?? throw new FachlicherFehler("Feld nicht gefunden.");

    private (Feld Feld, Geraet Geraet) GeraetVon(Guid geraetId)
    {
        foreach (var f in felder)
        {
            if (f.Geraete.FirstOrDefault(g => g.Id == geraetId) is { } g)
            {
                return (f, g);
            }
        }

        throw new FachlicherFehler("Gerät nicht gefunden.");
    }

    internal static string? Kuerzen(string? text, int laenge)
    {
        var t = text?.Trim() ?? "";
        return t.Length == 0 ? null : t.Length > laenge ? t[..laenge] : t;
    }

    internal static string Farbe(string? wert, string was)
        => wert?.Trim() is { } w && FarbMuster().IsMatch(w) ? w.ToUpperInvariant() : throw new FachlicherFehler($"{was}: bitte als #RRGGBB angeben.");

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex FarbMuster();
}
