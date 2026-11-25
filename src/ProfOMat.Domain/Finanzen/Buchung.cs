using System.Globalization;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Finanzen;

/// <summary>
/// Eine Buchung aus dem SAP-Kostenstellenauszug. Vorzeichen wie im Auszug: positiv = Ausgabe, negativ = Zugang
/// (Budget, Vortrag, Zuweisung, Gutschrift, Erlös).
/// </summary>
public sealed class Buchung
{
    public const int TextMaxLaenge = 200;

    private Buchung()
    {
    }

    public Buchung(Guid topfId, int jahr, int nr, DateOnly? erfasst, DateOnly belegdatum, DateOnly buchungsdatum, string kostenart,
        string kostenartText, decimal betrag, string text, string? belegnummer)
    {
        Id = Ids.Neu();
        TopfId = topfId;
        Jahr = jahr;
        Nr = nr;
        Erfasst = erfasst;
        Belegdatum = belegdatum;
        Buchungsdatum = buchungsdatum;
        Kostenart = Kuerzen(kostenart, 10);
        KostenartText = Kuerzen(kostenartText, 60);
        Betrag = betrag;
        Text = Kuerzen(text, TextMaxLaenge);
        Belegnummer = string.IsNullOrWhiteSpace(belegnummer) ? null : Kuerzen(belegnummer, 20);
    }

    public Guid Id { get; private set; }

    public Guid TopfId { get; private set; }

    /// <summary>Jahr des Auszugs (Buchungsjahr).</summary>
    public int Jahr { get; private set; }

    /// <summary>Reihenfolge in der Datei.</summary>
    public int Nr { get; private set; }

    public DateOnly? Erfasst { get; private set; }

    public DateOnly Belegdatum { get; private set; }

    public DateOnly Buchungsdatum { get; private set; }

    public string Kostenart { get; private set; } = string.Empty;

    public string KostenartText { get; private set; } = string.Empty;

    public decimal Betrag { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public string? Belegnummer { get; private set; }

    /// <summary>Mittelzugang: Zuweisungen, Budget und Vortrag (93…) sowie Erlöse (5…). Alles andere sind Ausgaben (Gutschriften mindern sie).</summary>
    public bool IstZugang => Kostenartgruppe.IstZugang(Kostenart);

    /// <summary>Vergleichsschlüssel beim Ersetzen eines Jahresstands (der Auszug hat keinen eindeutigen Schlüssel).</summary>
    public string Fingerabdruck => string.Join('|', Belegdatum.ToString("yyyyMMdd", CultureInfo.InvariantCulture), Kostenart,
        Betrag.ToString("0.00", CultureInfo.InvariantCulture), Text, Belegnummer ?? string.Empty);

    private static string Kuerzen(string s, int max)
    {
        var t = (s ?? string.Empty).Trim();
        return t.Length > max ? t[..max] : t;
    }
}

/// <summary>Stand eines importierten Jahres einer Kostenstelle (die Buchungen des Jahres stammen aus genau dieser Datei).</summary>
public sealed class Kostenstellenstand
{
    private Kostenstellenstand()
    {
    }

    public Kostenstellenstand(Guid topfId, int jahr, string dateiname, DateOnly? exportdatum, DateOnly? bisDatum, DateTimeOffset importiert, int anzahl, decimal summe)
    {
        Id = Ids.Neu();
        TopfId = topfId;
        Jahr = jahr;
        Aktualisieren(dateiname, exportdatum, bisDatum, importiert, anzahl, summe);
    }

    public Guid Id { get; private set; }

    public Guid TopfId { get; private set; }

    public int Jahr { get; private set; }

    public string Dateiname { get; private set; } = string.Empty;

    /// <summary>Datum aus dem Dateinamen („20260622 12103012 Tabelle.xls“), falls vorhanden.</summary>
    public DateOnly? Exportdatum { get; private set; }

    /// <summary>Spätestes Buchungsdatum im Auszug.</summary>
    public DateOnly? BisDatum { get; private set; }

    public DateTimeOffset Importiert { get; private set; }

    public int Anzahl { get; private set; }

    /// <summary>Summe laut Auszug (Rest = −Summe).</summary>
    public decimal Summe { get; private set; }

    public decimal Rest => -Summe;

    public void Aktualisieren(string dateiname, DateOnly? exportdatum, DateOnly? bisDatum, DateTimeOffset importiert, int anzahl, decimal summe)
    {
        var n = (dateiname ?? string.Empty).Trim();
        Dateiname = n.Length > 200 ? n[..200] : n;
        Exportdatum = exportdatum;
        BisDatum = bisDatum;
        Importiert = importiert;
        Anzahl = anzahl;
        Summe = summe;
    }
}

/// <summary>Woher ein Stand einer Maßnahme stammt.</summary>
public enum Standquelle
{
    Hand,
    Excel,
    KiJson,
}

/// <summary>Stand einer Investitions- oder SQM-Maßnahme zu einem Stichtag (Einzelbuchungen liegen dafür meist nicht vor).</summary>
public sealed class Massnahmenstand
{
    private Massnahmenstand()
    {
    }

    public Massnahmenstand(Guid topfId, DateOnly stichtag, decimal verausgabt, Standquelle quelle, DateTimeOffset erfasst, string? notiz = null)
    {
        if (verausgabt < 0)
        {
            throw new FachlicherFehler("Der verausgabte Betrag kann nicht negativ sein.");
        }

        Id = Ids.Neu();
        TopfId = topfId;
        Stichtag = stichtag;
        Verausgabt = decimal.Round(verausgabt, 2);
        Quelle = quelle;
        Erfasst = erfasst;
        Notiz = string.IsNullOrWhiteSpace(notiz) ? null : notiz.Trim()[..Math.Min(notiz.Trim().Length, 500)];
    }

    public Guid Id { get; private set; }

    public Guid TopfId { get; private set; }

    public DateOnly Stichtag { get; private set; }

    public decimal Verausgabt { get; private set; }

    public Standquelle Quelle { get; private set; }

    public DateTimeOffset Erfasst { get; private set; }

    public string? Notiz { get; private set; }
}

/// <summary>Grobe Gruppen der SAP-Kostenarten für Auswertungen.</summary>
public static class Kostenartgruppe
{
    public const string Zuweisungen = "Budget und Zuweisungen";
    public const string Erloese = "Erlöse";

    public static bool IstZugang(string kostenart) => kostenart.StartsWith("93", StringComparison.Ordinal) || kostenart.StartsWith('5');

    /// <summary>Budget Sachmittel der Fakultät (Bezugsgröße der Übertragsgrenze).</summary>
    public static bool IstBudget(string kostenart) => kostenart == "930300";

    /// <summary>Vortrag aus dem Vorjahr („Budget aus Vorjahr“ 930601 bzw. „Saldo aus Vorjahren“ 930600).</summary>
    public static bool IstVortrag(string kostenart) => kostenart.StartsWith("9306", StringComparison.Ordinal);

    public static string Von(string kostenart) => kostenart switch
    {
        _ when kostenart.StartsWith("93", StringComparison.Ordinal) => Zuweisungen,
        _ when kostenart.StartsWith('5') => Erloese,
        "607150" or "607550" => "EDV und Lizenzen",
        _ when kostenart.StartsWith("601", StringComparison.Ordinal) || kostenart.StartsWith("602", StringComparison.Ordinal)
               || kostenart.StartsWith("603", StringComparison.Ordinal) || kostenart.StartsWith("606", StringComparison.Ordinal) => "Material",
        _ when kostenart.StartsWith("607", StringComparison.Ordinal) => "Ausstattung und Kleingeräte",
        _ when kostenart.StartsWith("617", StringComparison.Ordinal) || kostenart.StartsWith("680", StringComparison.Ordinal)
               || kostenart.StartsWith("682", StringComparison.Ordinal) => "Büro, Druck, Telefon",
        _ when kostenart.StartsWith("685", StringComparison.Ordinal) => "Dienstreisen",
        _ when kostenart.StartsWith("634", StringComparison.Ordinal) || kostenart.StartsWith("641", StringComparison.Ordinal) => "Hilfskräfte",
        _ when kostenart.StartsWith("664", StringComparison.Ordinal) || kostenart.StartsWith("692", StringComparison.Ordinal) => "Weiterbildung, Mitgliedschaften",
        _ when kostenart.StartsWith("94", StringComparison.Ordinal) => "Interne Verrechnung (Werkstatt)",
        _ when kostenart.StartsWith("98", StringComparison.Ordinal) => "Investitionen",
        _ => "Sonstiges",
    };
}
