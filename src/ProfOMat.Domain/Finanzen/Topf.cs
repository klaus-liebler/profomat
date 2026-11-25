using System.Globalization;
using System.Text.RegularExpressions;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Finanzen;

/// <summary>Art einer Mittelquelle (Regeln der Fakultät IuI, siehe architektur.md „Finanzen“).</summary>
public enum Topfart
{
    /// <summary>Laborkostenstelle (SAP): laufend, frei verwendbar, Rest wird vorgetragen (Übertragsgrenze beachten).</summary>
    Kostenstelle,

    /// <summary>Investitionsmaßnahme mit eigenem Innenauftrag, zweckgebunden, Bestellschluss 31.08. des Folgejahres.</summary>
    Investition,

    /// <summary>Studienqualitätsmittel je Antrag: 01.03. bis 31.01. des Folgejahres (Rechnungsabgabe), projektbezogen abzurechnen.</summary>
    Sqm,
}

/// <summary>Änderbare Angaben eines Topfs.</summary>
/// <param name="UebertragsgrenzeProzent">Nur Kostenstelle: Überträge über diesem Anteil des Jahresbudgets werden ggf. eingezogen.</param>
public sealed record Topfangaben(
    string Bezeichnung,
    string? Innenauftrag = null,
    decimal BewilligtPersonal = 0,
    decimal BewilligtSach = 0,
    string? Kategorie = null,
    DateOnly? LaufzeitVon = null,
    DateOnly? LaufzeitBis = null,
    DateOnly? Bestellschluss = null,
    DateOnly? Rechnungsschluss = null,
    string? Verantwortlich = null,
    decimal UebertragsgrenzeProzent = Topf.StandardUebertragsgrenze,
    string? Notiz = null);

/// <summary>Eine Mittelquelle eines Laborteams.</summary>
public sealed partial class Topf
{
    public const int KennungMaxLaenge = 40;
    public const int TextMaxLaenge = 200;
    public const int NotizMaxLaenge = 2000;
    public const decimal StandardUebertragsgrenze = 12m;

    private Topf()
    {
    }

    private Topf(Guid teamId, Topfart art, string kennung)
    {
        Id = Ids.Neu();
        TeamId = teamId;
        Art = art;
        Kennung = kennung;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public Topfart Art { get; private set; }

    /// <summary>Kostenstellennummer (8 Ziffern) bzw. Antragsnummer (z. B. AUT-07-26, SQM-2026-15); fest, dient der Zuordnung beim Import.</summary>
    public string Kennung { get; private set; } = string.Empty;

    public string Bezeichnung { get; private set; } = string.Empty;

    /// <summary>SAP-Innenauftrag einer Investitionsmaßnahme (z. B. 71211637).</summary>
    public string? Innenauftrag { get; private set; }

    /// <summary>Jahr des Antrags (aus der Antragsnummer); bei der Kostenstelle leer.</summary>
    public int? Jahr { get; private set; }

    public decimal BewilligtPersonal { get; private set; }

    public decimal BewilligtSach { get; private set; }

    public string? Kategorie { get; private set; }

    public DateOnly? LaufzeitVon { get; private set; }

    public DateOnly? LaufzeitBis { get; private set; }

    public DateOnly? Bestellschluss { get; private set; }

    public DateOnly? Rechnungsschluss { get; private set; }

    public string? Verantwortlich { get; private set; }

    public decimal UebertragsgrenzeProzent { get; private set; } = StandardUebertragsgrenze;

    public string? Notiz { get; private set; }

    public bool Archiviert { get; private set; }

    public decimal Bewilligt => BewilligtPersonal + BewilligtSach;

    public bool Zweckgebunden => Art != Topfart.Kostenstelle;

    public string Anzeige => Art == Topfart.Kostenstelle ? $"Kostenstelle {Kennung}" : $"{Kennung} · {Bezeichnung}";

    public Topfangaben Angaben => new(Bezeichnung, Innenauftrag, BewilligtPersonal, BewilligtSach, Kategorie, LaufzeitVon, LaufzeitBis,
        Bestellschluss, Rechnungsschluss, Verantwortlich, UebertragsgrenzeProzent, Notiz);

    /// <summary>Letzter Tag, an dem Mittel noch eingesetzt werden können (Rechnungsabgabe bzw. Bestellschluss bzw. Laufzeitende).</summary>
    public DateOnly? Stichtag => Art switch
    {
        Topfart.Sqm => Rechnungsschluss ?? LaufzeitBis,
        Topfart.Investition => Bestellschluss ?? LaufzeitBis,
        _ => LaufzeitBis,
    };

    public static Topf Kostenstelle(Guid teamId, string nummer, string? bezeichnung = null)
    {
        var n = (nummer ?? string.Empty).Trim();
        if (!KostenstellenNummer().IsMatch(n))
        {
            throw new FachlicherFehler("Die Kostenstelle hat 8 Ziffern (z. B. 12103012).");
        }

        var t = new Topf(teamId, Topfart.Kostenstelle, n);
        t.Aendern(new Topfangaben(string.IsNullOrWhiteSpace(bezeichnung) ? "Laborkostenstelle" : bezeichnung));
        return t;
    }

    /// <summary>Investitionsmaßnahme; Bestellschluss nach dem Zeitraster der Fakultät: 31.08. des Folgejahres.</summary>
    public static Topf Investition(Guid teamId, string antragsnummer, string bezeichnung, decimal bewilligt, string? innenauftrag = null)
    {
        var t = new Topf(teamId, Topfart.Investition, Antragsnummer(antragsnummer));
        t.Jahr = JahrAus(t.Kennung);
        t.Aendern(new Topfangaben(bezeichnung, innenauftrag, 0, bewilligt,
            LaufzeitVon: t.Jahr is { } j ? new DateOnly(j, 1, 1) : null,
            Bestellschluss: t.Jahr is { } j2 ? new DateOnly(j2 + 1, 8, 31) : null));
        return t;
    }

    /// <summary>SQM-Maßnahme; Laufzeit nach dem Zeitraster: Mittelfreigabe 01.03., Rechnungsabgabe bis 31.01. des Folgejahres.</summary>
    public static Topf Sqm(Guid teamId, string antragsnummer, string bezeichnung, decimal personal, decimal sach, string? kategorie = null)
    {
        var t = new Topf(teamId, Topfart.Sqm, Antragsnummer(antragsnummer));
        t.Jahr = JahrAus(t.Kennung);
        t.Aendern(new Topfangaben(bezeichnung, null, personal, sach, kategorie,
            LaufzeitVon: t.Jahr is { } j ? new DateOnly(j, 3, 1) : null,
            LaufzeitBis: t.Jahr is { } j2 ? new DateOnly(j2 + 1, 1, 31) : null,
            Rechnungsschluss: t.Jahr is { } j3 ? new DateOnly(j3 + 1, 1, 31) : null));
        return t;
    }

    public void Aendern(Topfangaben a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (string.IsNullOrWhiteSpace(a.Bezeichnung))
        {
            throw new FachlicherFehler("Bitte eine Bezeichnung angeben.");
        }

        if (a.BewilligtPersonal < 0 || a.BewilligtSach < 0)
        {
            throw new FachlicherFehler("Bewilligte Beträge können nicht negativ sein.");
        }

        if (Art == Topfart.Kostenstelle && (a.BewilligtPersonal != 0 || a.BewilligtSach != 0))
        {
            throw new FachlicherFehler("Die Kostenstelle hat keinen bewilligten Betrag – ihr Budget kommt aus dem SAP-Auszug.");
        }

        if (a.UebertragsgrenzeProzent is < 0 or > 100)
        {
            throw new FachlicherFehler("Die Übertragsgrenze liegt zwischen 0 und 100 %.");
        }

        if (a.LaufzeitVon is { } von && a.LaufzeitBis is { } bis && bis < von)
        {
            throw new FachlicherFehler("Das Laufzeitende liegt vor dem Beginn.");
        }

        Bezeichnung = Text(a.Bezeichnung, TextMaxLaenge)!;
        Innenauftrag = Text(a.Innenauftrag, 20);
        BewilligtPersonal = decimal.Round(a.BewilligtPersonal, 2);
        BewilligtSach = decimal.Round(a.BewilligtSach, 2);
        Kategorie = Text(a.Kategorie, TextMaxLaenge);
        LaufzeitVon = a.LaufzeitVon;
        LaufzeitBis = a.LaufzeitBis;
        Bestellschluss = a.Bestellschluss;
        Rechnungsschluss = a.Rechnungsschluss;
        Verantwortlich = Text(a.Verantwortlich, TextMaxLaenge);
        UebertragsgrenzeProzent = a.UebertragsgrenzeProzent;
        Notiz = Text(a.Notiz, NotizMaxLaenge);
    }

    public void Archivieren(bool archiviert) => Archiviert = archiviert;

    /// <summary>Antragsjahr aus „SQM-2026-15“ (vierstellig) oder „AUT-07-26“ (zweistellig am Ende).</summary>
    public static int? JahrAus(string antragsnummer)
    {
        var m = JahrImAntrag().Match(antragsnummer ?? string.Empty);
        if (!m.Success)
        {
            return null;
        }

        return m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 2000 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
    }

    private static string Antragsnummer(string antragsnummer)
    {
        var n = (antragsnummer ?? string.Empty).Trim().ToUpperInvariant();
        return n.Length is 0 or > KennungMaxLaenge
            ? throw new FachlicherFehler("Bitte die Antragsnummer angeben (z. B. AUT-07-26 oder SQM-2026-15).")
            : n;
    }

    private static string? Text(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var t = s.Trim();
        return t.Length > max ? t[..max] : t;
    }

    [GeneratedRegex("^[0-9]{8}$")]
    private static partial Regex KostenstellenNummer();

    [GeneratedRegex(@"(?:-((?:19|20)[0-9]{2})-)|(?:-([0-9]{2})$)")]
    private static partial Regex JahrImAntrag();
}
