using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Finanzen;

/// <summary>Stand einer geplanten Ausgabe.</summary>
public enum Ausgabenstand
{
    Geplant,
    Bestellt,
    Abgerechnet,

    /// <summary>Steht im Ist-Stand (Buchung bzw. Stand der Maßnahme) – bindet nichts mehr.</summary>
    Erledigt,
}

/// <summary>Anteil eines Betrags an einem Topf.</summary>
public sealed record Anteil(Guid TopfId, decimal Betrag);

/// <summary>Geplante Ausgabe (FA-706); kann auf mehrere Töpfe aufgeteilt sein. Offen bindet sie Mittel im voraussichtlichen Monat.</summary>
public sealed class GeplanteAusgabe
{
    public const int TextMaxLaenge = 200;

    private readonly List<Ausgabenanteil> anteile = [];

    private GeplanteAusgabe()
    {
    }

    public GeplanteAusgabe(Guid teamId, string bezeichnung, DateOnly monat, IReadOnlyCollection<Anteil> anteile, DateTimeOffset angelegt, string? notiz = null)
    {
        Id = Ids.Neu();
        TeamId = teamId;
        Angelegt = angelegt;
        Aendern(bezeichnung, monat, anteile, notiz);
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public string Bezeichnung { get; private set; } = string.Empty;

    /// <summary>Erster Tag des voraussichtlichen Monats.</summary>
    public DateOnly Monat { get; private set; }

    public Ausgabenstand Stand { get; private set; }

    public string? Notiz { get; private set; }

    public DateTimeOffset Angelegt { get; private set; }

    /// <summary>Verknüpfte Buchung der Kostenstelle (Fingerabdruck, siehe <see cref="Buchung.Fingerabdruck"/>).</summary>
    public string? BuchungVerweis { get; private set; }

    public IReadOnlyList<Ausgabenanteil> Anteile => anteile;

    public decimal Betrag => anteile.Sum(a => a.Betrag);

    public bool Offen => Stand != Ausgabenstand.Erledigt;

    public void Aendern(string bezeichnung, DateOnly monat, IReadOnlyCollection<Anteil> neu, string? notiz)
    {
        ArgumentNullException.ThrowIfNull(neu);
        if (string.IsNullOrWhiteSpace(bezeichnung))
        {
            throw new FachlicherFehler("Bitte beschreiben, was ausgegeben werden soll.");
        }

        var liste = neu.Where(a => a.Betrag != 0).ToList();
        if (liste.Count == 0)
        {
            throw new FachlicherFehler("Bitte einen Betrag und einen Topf angeben.");
        }

        if (liste.Any(a => a.Betrag < 0))
        {
            throw new FachlicherFehler("Geplante Ausgaben sind positiv.");
        }

        if (liste.GroupBy(a => a.TopfId).Any(g => g.Count() > 1))
        {
            throw new FachlicherFehler("Jeder Topf nur einmal je Ausgabe.");
        }

        Bezeichnung = Kuerzen(bezeichnung, TextMaxLaenge)!;
        Monat = new DateOnly(monat.Year, monat.Month, 1);
        Notiz = Kuerzen(notiz, 2000);
        anteile.Clear();
        anteile.AddRange(liste.Select(a => new Ausgabenanteil(Id, a.TopfId, decimal.Round(a.Betrag, 2))));
    }

    public void StandSetzen(Ausgabenstand stand, string? buchungVerweis = null)
    {
        Stand = stand;
        BuchungVerweis = stand == Ausgabenstand.Erledigt ? buchungVerweis : null;
    }

    internal static string? Kuerzen(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var t = s.Trim();
        return t.Length > max ? t[..max] : t;
    }
}

public sealed class Ausgabenanteil
{
    private Ausgabenanteil()
    {
    }

    internal Ausgabenanteil(Guid ausgabeId, Guid topfId, decimal betrag)
    {
        Id = Ids.Neu();
        AusgabeId = ausgabeId;
        TopfId = topfId;
        Betrag = betrag;
    }

    public Guid Id { get; private set; }

    public Guid AusgabeId { get; private set; }

    public Guid TopfId { get; private set; }

    public decimal Betrag { get; private set; }
}

public enum Auslagenstand
{
    Offen,
    Eingereicht,

    /// <summary>Erstattet; die Beträge stehen dann im Ist-Stand und binden nichts mehr.</summary>
    Erstattet,
}

/// <summary>Sammel-Auslagenabrechnung privat vorfinanzierter Käufe (FA-707); jede Position gehört zu einem Topf.</summary>
public sealed class Auslagenabrechnung
{
    private readonly List<Auslagenposition> positionen = [];

    private Auslagenabrechnung()
    {
    }

    public Auslagenabrechnung(Guid teamId, string bezeichnung, Guid kontoId, DateTimeOffset angelegt)
    {
        Id = Ids.Neu();
        TeamId = teamId;
        KontoId = kontoId;
        Angelegt = angelegt;
        Umbenennen(bezeichnung);
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    /// <summary>Wer die Käufe vorfinanziert hat (und die Abrechnung einreicht).</summary>
    public Guid KontoId { get; private set; }

    public string Bezeichnung { get; private set; } = string.Empty;

    public Auslagenstand Stand { get; private set; }

    public DateOnly? Eingereicht { get; private set; }

    public DateTimeOffset Angelegt { get; private set; }

    public IReadOnlyList<Auslagenposition> Positionen => positionen;

    public IEnumerable<Auslagenposition> PositionenSortiert => positionen.OrderBy(p => p.Kaufdatum).ThenBy(p => p.Nr);

    public decimal Summe => positionen.Sum(p => p.Betrag);

    public bool Offen => Stand != Auslagenstand.Erstattet;

    public Freigabe Aenderbar => Stand == Auslagenstand.Offen ? Freigabe.Ja
        : Freigabe.Nein("Die Abrechnung ist schon eingereicht – Positionen ändern nur, nachdem sie wieder auf „offen“ gesetzt ist.");

    public void Umbenennen(string bezeichnung)
        => Bezeichnung = GeplanteAusgabe.Kuerzen(bezeichnung, 200) ?? throw new FachlicherFehler("Bitte eine Bezeichnung angeben (z. B. „Sammelabrechnung November“).");

    public Auslagenposition Hinzufuegen(DateOnly kaufdatum, string haendler, string artikel, decimal betrag, Guid topfId)
    {
        Aenderbar.Sicherstellen();
        var p = new Auslagenposition(Id, positionen.Count == 0 ? 1 : positionen.Max(x => x.Nr) + 1, kaufdatum, haendler, artikel, betrag, topfId);
        positionen.Add(p);
        return p;
    }

    public void PositionAendern(Guid positionId, DateOnly kaufdatum, string haendler, string artikel, decimal betrag, Guid topfId)
    {
        Aenderbar.Sicherstellen();
        Position(positionId).Setzen(kaufdatum, haendler, artikel, betrag, topfId);
    }

    public void PositionEntfernen(Guid positionId)
    {
        Aenderbar.Sicherstellen();
        positionen.Remove(Position(positionId));
    }

    public void StandSetzen(Auslagenstand stand, DateOnly heute)
    {
        if (stand != Auslagenstand.Offen && positionen.Count == 0)
        {
            throw new FachlicherFehler("Die Abrechnung hat noch keine Positionen.");
        }

        Stand = stand;
        Eingereicht = stand == Auslagenstand.Offen ? null : Eingereicht ?? heute;
    }

    /// <summary>Prüfungen mit Begründung (Laufzeit eines SQM-Topfs, Rechnungsabgabe, Bestellschluss).</summary>
    public IReadOnlyList<(Guid? PositionId, string Text)> Pruefen(IReadOnlyDictionary<Guid, Topf> toepfe, DateOnly heute)
    {
        ArgumentNullException.ThrowIfNull(toepfe);
        var liste = new List<(Guid?, string)>();
        var einreichung = Eingereicht ?? heute;
        foreach (var p in PositionenSortiert)
        {
            if (!toepfe.TryGetValue(p.TopfId, out var t))
            {
                liste.Add((p.Id, $"Position {p.Nr}: Der Topf gibt es nicht mehr."));
                continue;
            }

            if (t.Archiviert)
            {
                liste.Add((p.Id, $"Position {p.Nr}: {t.Kennung} ist archiviert."));
            }

            if (t.Art == Topfart.Sqm)
            {
                if ((t.LaufzeitVon is { } von && p.Kaufdatum < von) || (t.LaufzeitBis is { } bis && p.Kaufdatum > bis))
                {
                    liste.Add((p.Id, $"Position {p.Nr}: Kaufdatum {p.Kaufdatum:dd.MM.yyyy} liegt außerhalb der Laufzeit von {t.Kennung} "
                                     + $"({t.LaufzeitVon:dd.MM.yyyy}–{t.LaufzeitBis:dd.MM.yyyy}) – SQM werden projektbezogen nur in der Laufzeit abgerechnet."));
                }

                if (t.Rechnungsschluss is { } rs && einreichung > rs)
                {
                    liste.Add((p.Id, $"Position {p.Nr}: Die Rechnungsabgabe für {t.Kennung} endete am {rs:dd.MM.yyyy}."));
                }
            }
            else if (t.Art == Topfart.Investition && t.Bestellschluss is { } bs && p.Kaufdatum > bs)
            {
                liste.Add((p.Id, $"Position {p.Nr}: Kauf nach dem Bestellschluss von {t.Kennung} ({bs:dd.MM.yyyy})."));
            }
        }

        return liste;
    }

    private Auslagenposition Position(Guid id) => positionen.FirstOrDefault(p => p.Id == id) ?? throw new FachlicherFehler("Diese Position gibt es nicht (mehr).");
}

public sealed class Auslagenposition
{
    private Auslagenposition()
    {
    }

    internal Auslagenposition(Guid abrechnungId, int nr, DateOnly kaufdatum, string haendler, string artikel, decimal betrag, Guid topfId)
    {
        Id = Ids.Neu();
        AbrechnungId = abrechnungId;
        Nr = nr;
        Setzen(kaufdatum, haendler, artikel, betrag, topfId);
    }

    public Guid Id { get; private set; }

    public Guid AbrechnungId { get; private set; }

    public int Nr { get; private set; }

    public DateOnly Kaufdatum { get; private set; }

    public string Haendler { get; private set; } = string.Empty;

    public string Artikel { get; private set; } = string.Empty;

    /// <summary>Brutto.</summary>
    public decimal Betrag { get; private set; }

    public Guid TopfId { get; private set; }

    internal void Setzen(DateOnly kaufdatum, string haendler, string artikel, decimal betrag, Guid topfId)
    {
        if (betrag <= 0)
        {
            throw new FachlicherFehler("Der Betrag einer Position ist größer als 0 €.");
        }

        Kaufdatum = kaufdatum;
        Haendler = GeplanteAusgabe.Kuerzen(haendler, 100) ?? throw new FachlicherFehler("Bitte den Händler angeben.");
        Artikel = GeplanteAusgabe.Kuerzen(artikel, 200) ?? throw new FachlicherFehler("Bitte den Artikel angeben.");
        Betrag = decimal.Round(betrag, 2);
        TopfId = topfId;
    }
}

/// <summary>Art der Beschäftigung (Stundensätze „Haushaltsbelastung“ der Fakultät).</summary>
public enum Hilfskraftart
{
    StudentischeHilfskraft,
    WissHilfskraftBachelor,
    WissHilfskraftMaster,
    TutorOhneAbschluss,
    TutorBachelor,
    TutorMaster,
}

/// <summary>Stundensatz („Haushaltsbelastung“ je Stunde) eines Jahres; je Team pflegbar, sonst gelten die Werte der Fakultät.</summary>
public sealed class Stundensatz
{
    private Stundensatz()
    {
    }

    public Stundensatz(Guid teamId, int jahr, Hilfskraftart art, decimal euroProStunde)
    {
        Id = Ids.Neu();
        TeamId = teamId;
        Jahr = jahr;
        Art = art;
        Setzen(euroProStunde);
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public int Jahr { get; private set; }

    public Hilfskraftart Art { get; private set; }

    public decimal EuroProStunde { get; private set; }

    public void Setzen(decimal euro)
    {
        if (euro is <= 0 or > 200)
        {
            throw new FachlicherFehler("Der Stundensatz liegt zwischen 0 und 200 €.");
        }

        EuroProStunde = decimal.Round(euro, 2);
    }
}

/// <summary>Geplante oder laufende Beschäftigung einer Hilfskraft (FA-708), anteilig auf Töpfe (Prozent).</summary>
public sealed class Hilfskraft
{
    private readonly List<Hilfskraftanteil> anteile = [];

    private Hilfskraft()
    {
    }

    public Hilfskraft(Guid teamId, string person, Hilfskraftart art, decimal stundenProMonat, DateOnly beginn, DateOnly ende,
        IReadOnlyCollection<(Guid TopfId, decimal Prozent)> anteile, string? notiz = null)
    {
        Id = Ids.Neu();
        TeamId = teamId;
        Aendern(person, art, stundenProMonat, beginn, ende, anteile, notiz);
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    /// <summary>Name (nur im Team sichtbar, nie im Protokoll).</summary>
    public string Person { get; private set; } = string.Empty;

    public Hilfskraftart Art { get; private set; }

    public decimal StundenProMonat { get; private set; }

    /// <summary>Erster Monat (1. Tag).</summary>
    public DateOnly Beginn { get; private set; }

    /// <summary>Letzter Monat (1. Tag).</summary>
    public DateOnly Ende { get; private set; }

    public string? Notiz { get; private set; }

    public IReadOnlyList<Hilfskraftanteil> Anteile => anteile;

    public int Monate => ((Ende.Year - Beginn.Year) * 12) + Ende.Month - Beginn.Month + 1;

    public void Aendern(string person, Hilfskraftart art, decimal stundenProMonat, DateOnly beginn, DateOnly ende,
        IReadOnlyCollection<(Guid TopfId, decimal Prozent)> neu, string? notiz)
    {
        ArgumentNullException.ThrowIfNull(neu);
        var b = new DateOnly(beginn.Year, beginn.Month, 1);
        var e = new DateOnly(ende.Year, ende.Month, 1);
        if (e < b)
        {
            throw new FachlicherFehler("Das Ende liegt vor dem Beginn.");
        }

        if (stundenProMonat is <= 0 or > 200)
        {
            throw new FachlicherFehler("Stunden je Monat: mehr als 0 und höchstens 200.");
        }

        var liste = neu.Where(a => a.Prozent != 0).ToList();
        if (liste.Count == 0 || liste.Any(a => a.Prozent < 0) || liste.Sum(a => a.Prozent) != 100m)
        {
            throw new FachlicherFehler("Die Anteile der Töpfe ergeben zusammen 100 %.");
        }

        if (liste.GroupBy(a => a.TopfId).Any(g => g.Count() > 1))
        {
            throw new FachlicherFehler("Jeder Topf nur einmal je Beschäftigung.");
        }

        Person = GeplanteAusgabe.Kuerzen(person, 120) ?? throw new FachlicherFehler("Bitte die Person angeben (oder z. B. „N. N. Tutor Embedded“).");
        Art = art;
        StundenProMonat = decimal.Round(stundenProMonat, 2);
        Beginn = b;
        Ende = e;
        Notiz = GeplanteAusgabe.Kuerzen(notiz, 2000);
        anteile.Clear();
        anteile.AddRange(liste.Select(a => new Hilfskraftanteil(Id, a.TopfId, a.Prozent)));
    }
}

public sealed class Hilfskraftanteil
{
    private Hilfskraftanteil()
    {
    }

    internal Hilfskraftanteil(Guid hilfskraftId, Guid topfId, decimal prozent)
    {
        Id = Ids.Neu();
        HilfskraftId = hilfskraftId;
        TopfId = topfId;
        Prozent = prozent;
    }

    public Guid Id { get; private set; }

    public Guid HilfskraftId { get; private set; }

    public Guid TopfId { get; private set; }

    public decimal Prozent { get; private set; }
}
