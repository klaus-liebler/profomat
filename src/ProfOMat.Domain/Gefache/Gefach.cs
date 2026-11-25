using System.Globalization;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Gefache;

/// <summary>Wie die Fächer in einer Richtung festgelegt werden.</summary>
public enum Teilungsart
{
    /// <summary>Gleich große Fächer, nur die Anzahl wird angegeben.</summary>
    Anzahl,

    /// <summary>Fachgrößen in mm („*“ = Rest, gleichmäßig verteilt).</summary>
    Masse,
}

/// <summary>Teilung in einer Richtung: Anzahl gleicher Fächer oder Fachgrößen in mm (z. B. „120 * 80“).</summary>
public sealed record Teilung(Teilungsart Art, int Anzahl, string? Masse)
{
    public const int MaxFaecher = 30;
    public const int MasseMaxLaenge = 200;

    public static Teilung Gleich(int anzahl) => new(Teilungsart.Anzahl, anzahl, null);

    /// <summary>Zerlegt die Maßangabe in Fachgrößen (<c>null</c> = „*“, Rest). Trennzeichen: Leerzeichen, Semikolon, „/“, „+“.</summary>
    public static IReadOnlyList<decimal?> MasseZerlegen(string? masse)
    {
        var teile = (masse ?? "").Split([' ', ';', '/', '+', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ergebnis = new List<decimal?>();
        foreach (var t in teile)
        {
            if (t == "*")
            {
                ergebnis.Add(null);
            }
            else if (decimal.TryParse(t.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && d > 0)
            {
                ergebnis.Add(d);
            }
            else
            {
                throw new FachlicherFehler($"„{t}“ ist keine Fachgröße – bitte Zahlen in mm, getrennt durch Leerzeichen, und „*“ für den Rest (z. B. „120 * 80“).");
            }
        }

        return ergebnis;
    }

    internal Teilung Geprueft(string richtung)
    {
        if (Art == Teilungsart.Anzahl)
        {
            return Anzahl is < 1 or > MaxFaecher
                ? throw new FachlicherFehler($"{richtung}: 1 bis {MaxFaecher} Fächer.")
                : this with { Masse = Gefach.Kuerzen(Masse, MasseMaxLaenge) };
        }

        var masse = Gefach.Kuerzen(Masse, MasseMaxLaenge) ?? throw new FachlicherFehler($"{richtung}: bitte Fachgrößen in mm angeben (z. B. „120 * 80“).");
        var zahl = MasseZerlegen(masse).Count;
        return zahl is 0 or > MaxFaecher ? throw new FachlicherFehler($"{richtung}: 1 bis {MaxFaecher} Fächer.") : this with { Masse = masse };
    }
}

/// <summary>
/// Alle Angaben zu einem Gefach (Teilungseinsatz) für eine Kiste. Maße in mm. Innenmaße oben und am Boden dürfen sich unterscheiden
/// (Kisten mit schrägen Wänden); die Rundung zwischen Boden und Wand wird an den unteren Ecken der Wände ausgespart.
/// </summary>
public sealed record Gefachangaben
{
    public decimal InnenLaengeMm { get; init; } = 570m;

    public decimal InnenBreiteMm { get; init; } = 370m;

    /// <summary>Nutzbare Innenhöhe im Stapel (die nächste Kiste darf nicht aufsitzen).</summary>
    public decimal StapelhoeheMm { get; init; } = 204m;

    /// <summary>Höhe der Wände des Gefachs (höchstens die Stapelhöhe).</summary>
    public decimal HoeheMm { get; init; } = 204m;

    /// <summary>Innenmaße am Boden (gleich den Innenmaßen oben, wenn die Wände senkrecht sind).</summary>
    public decimal BodenLaengeMm { get; init; } = 570m;

    public decimal BodenBreiteMm { get; init; } = 370m;

    /// <summary>Radius der Rundung zwischen Kistenboden und Wand.</summary>
    public decimal RadiusMm { get; init; } = 10m;

    public decimal DickeMm { get; init; } = 3m;

    public decimal PlatteLaengeMm { get; init; } = 800m;

    public decimal PlatteBreiteMm { get; init; } = 600m;

    /// <summary>Luft zwischen Gefach und Kistenwand je Seite.</summary>
    public decimal SpielMm { get; init; } = 1m;

    /// <summary>Schlitzbreite = Holzdicke + Zugabe (Laser-Schnittfuge, Toleranz der Platte).</summary>
    public decimal SchlitzzugabeMm { get; init; } = 0.2m;

    /// <summary>Abstand der Teile untereinander und zum Plattenrand.</summary>
    public decimal AbstandMm { get; init; } = 4m;

    /// <summary>Fächer entlang der Länge (getrennt durch Querwände).</summary>
    public Teilung Laenge { get; init; } = Teilung.Gleich(3);

    /// <summary>Fächer entlang der Breite (getrennt durch Längswände).</summary>
    public Teilung Breite { get; init; } = Teilung.Gleich(2);

    /// <summary>Teilenummer und Einbaurichtung auf jede Wand gravieren.</summary>
    public bool Gravur { get; init; } = true;

    /// <summary>Prüft die Wertebereiche; Geometrie (Fächer zu klein, Rundung) prüft erst der Plan.</summary>
    public Gefachangaben Geprueft()
    {
        static void Bereich(decimal wert, decimal min, decimal max, string text)
        {
            if (wert < min || wert > max)
            {
                throw new FachlicherFehler($"{text}: {min.ToString("0.##", Gefach.Deutsch)} bis {max.ToString("0.##", Gefach.Deutsch)} mm.");
            }
        }

        Bereich(InnenLaengeMm, 50, 2000, "Innenlänge");
        Bereich(InnenBreiteMm, 50, 2000, "Innenbreite");
        Bereich(StapelhoeheMm, 10, 1000, "Innenhöhe im Stapel");
        Bereich(HoeheMm, 10, 1000, "Höhe des Gefachs");
        if (HoeheMm > StapelhoeheMm)
        {
            throw new FachlicherFehler($"Das Gefach darf höchstens so hoch sein wie die nutzbare Innenhöhe im Stapel ({StapelhoeheMm.ToString("0.##", Gefach.Deutsch)} mm).");
        }

        Bereich(BodenLaengeMm, 50, 2000, "Innenlänge am Boden");
        Bereich(BodenBreiteMm, 50, 2000, "Innenbreite am Boden");
        if (BodenLaengeMm > InnenLaengeMm || BodenBreiteMm > InnenBreiteMm)
        {
            throw new FachlicherFehler("Am Boden kann die Kiste nicht größer sein als oben – Innenmaße am Boden prüfen.");
        }

        Bereich(RadiusMm, 0, 100, "Radius am Boden");
        Bereich(DickeMm, 1, 20, "Holzdicke");
        Bereich(PlatteLaengeMm, 50, 3000, "Plattenlänge");
        Bereich(PlatteBreiteMm, 50, 3000, "Plattenbreite");
        Bereich(SpielMm, 0, 20, "Spiel zur Kistenwand");
        Bereich(SchlitzzugabeMm, -1, 3, "Schlitzzugabe");
        Bereich(AbstandMm, 0, 50, "Abstand der Teile");
        return this with
        {
            Laenge = (Laenge ?? throw new FachlicherFehler("Teilung der Länge fehlt.")).Geprueft("Fächer entlang der Länge"),
            Breite = (Breite ?? throw new FachlicherFehler("Teilung der Breite fehlt.")).Geprueft("Fächer entlang der Breite"),
        };
    }
}

/// <summary>Ein gespeichertes Gefach (Nerd Tool, privat für das anlegende Konto).</summary>
public sealed class Gefach
{
    public const int BezeichnungMaxLaenge = 100;

    internal static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

    private Gefach()
    {
    }

    public Gefach(Guid kontoId, string bezeichnung, DateTimeOffset angelegt, Gefachangaben? angaben = null)
    {
        Id = Ids.Neu();
        KontoId = kontoId;
        Angelegt = angelegt;
        Umbenennen(bezeichnung);
        Einstellen(angaben ?? new Gefachangaben());
    }

    public Guid Id { get; private set; }

    public Guid KontoId { get; private set; }

    public string Bezeichnung { get; private set; } = string.Empty;

    public DateTimeOffset Angelegt { get; private set; }

    public decimal InnenLaengeMm { get; private set; }

    public decimal InnenBreiteMm { get; private set; }

    public decimal StapelhoeheMm { get; private set; }

    public decimal HoeheMm { get; private set; }

    public decimal BodenLaengeMm { get; private set; }

    public decimal BodenBreiteMm { get; private set; }

    public decimal RadiusMm { get; private set; }

    public decimal DickeMm { get; private set; }

    public decimal PlatteLaengeMm { get; private set; }

    public decimal PlatteBreiteMm { get; private set; }

    public decimal SpielMm { get; private set; }

    public decimal SchlitzzugabeMm { get; private set; }

    public decimal AbstandMm { get; private set; }

    public Teilungsart LaengeArt { get; private set; }

    public int LaengeAnzahl { get; private set; }

    public string? LaengeMasse { get; private set; }

    public Teilungsart BreiteArt { get; private set; }

    public int BreiteAnzahl { get; private set; }

    public string? BreiteMasse { get; private set; }

    public bool Gravur { get; private set; }

    /// <summary>Für alle angemeldeten Personen sichtbar (nur ansehen und als Vorlage übernehmen; ändern kann nur, wer es angelegt hat).</summary>
    public bool Oeffentlich { get; private set; }

    public Gefachangaben Angaben => new()
    {
        InnenLaengeMm = InnenLaengeMm,
        InnenBreiteMm = InnenBreiteMm,
        StapelhoeheMm = StapelhoeheMm,
        HoeheMm = HoeheMm,
        BodenLaengeMm = BodenLaengeMm,
        BodenBreiteMm = BodenBreiteMm,
        RadiusMm = RadiusMm,
        DickeMm = DickeMm,
        PlatteLaengeMm = PlatteLaengeMm,
        PlatteBreiteMm = PlatteBreiteMm,
        SpielMm = SpielMm,
        SchlitzzugabeMm = SchlitzzugabeMm,
        AbstandMm = AbstandMm,
        Laenge = new Teilung(LaengeArt, LaengeAnzahl, LaengeMasse),
        Breite = new Teilung(BreiteArt, BreiteAnzahl, BreiteMasse),
        Gravur = Gravur,
    };

    public void Umbenennen(string bezeichnung)
        => Bezeichnung = Kuerzen(bezeichnung, BezeichnungMaxLaenge) ?? throw new FachlicherFehler("Bitte eine Bezeichnung angeben (z. B. „Werkzeugkiste Keller“).");

    public void SichtbarkeitSetzen(bool oeffentlich) => Oeffentlich = oeffentlich;

    public void Einstellen(Gefachangaben angaben)
    {
        ArgumentNullException.ThrowIfNull(angaben);
        var a = angaben.Geprueft();
        (InnenLaengeMm, InnenBreiteMm, StapelhoeheMm, HoeheMm, BodenLaengeMm, BodenBreiteMm, RadiusMm)
            = (a.InnenLaengeMm, a.InnenBreiteMm, a.StapelhoeheMm, a.HoeheMm, a.BodenLaengeMm, a.BodenBreiteMm, a.RadiusMm);
        (DickeMm, PlatteLaengeMm, PlatteBreiteMm, SpielMm, SchlitzzugabeMm, AbstandMm, Gravur)
            = (a.DickeMm, a.PlatteLaengeMm, a.PlatteBreiteMm, a.SpielMm, a.SchlitzzugabeMm, a.AbstandMm, a.Gravur);
        (LaengeArt, LaengeAnzahl, LaengeMasse) = (a.Laenge.Art, a.Laenge.Anzahl, a.Laenge.Masse);
        (BreiteArt, BreiteAnzahl, BreiteMasse) = (a.Breite.Art, a.Breite.Anzahl, a.Breite.Masse);
    }

    internal static string? Kuerzen(string? text, int laenge)
    {
        var t = text?.Trim() ?? "";
        return t.Length == 0 ? null : t.Length > laenge ? t[..laenge] : t;
    }
}
