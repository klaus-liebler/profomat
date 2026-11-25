using ProfOMat.Domain.Finanzen;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Finanzen;

/// <summary>Erzeugt Dateien zu den Finanzen (Infrastruktur: ClosedXML).</summary>
public interface IFinanzDokumente
{
    /// <summary>Aufstellung einer Sammel-Auslagenabrechnung: Übersicht und je Topf ein Blatt (Anlage zum Abrechnungsformular).</summary>
    byte[] AuslagenXlsx(Auslagenabrechnung abrechnung, IReadOnlyDictionary<Guid, Topf> toepfe, string person);
}

public sealed record Finanzdatei(byte[] Inhalt, string Typ, string Name);

/// <summary>Eine Auslagenabrechnung mit Namen der vorfinanzierenden Person und Prüfungen.</summary>
public sealed record Auslagenansicht(Auslagenabrechnung Abrechnung, string Person, IReadOnlyList<(Guid? PositionId, string Text)> Hinweise);

/// <summary>Stundensatz einer Art im Jahr, mit Herkunft.</summary>
public sealed record Stundensatzansicht(Hilfskraftart Art, int Jahr, decimal EuroProStunde, bool EigenerWert);

/// <summary>Planung: geplante Ausgaben (FA-706), Auslagen (FA-707), Hilfskräfte (FA-708/709).</summary>
public sealed partial class FinanzDienst
{
    // ------------------------------------------------------------------ geplante Ausgaben

    public async Task<IReadOnlyList<GeplanteAusgabe>> AusgabenAsync(Guid teamId, CancellationToken abbruch = default)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        return await daten.ListeAsync(daten.Menge<GeplanteAusgabe>().Where(a => a.TeamId == teamId).OrderBy(a => a.Monat).ThenBy(a => a.Angelegt).ThenBy(a => a.Id), abbruch);
    }

    public async Task<GeplanteAusgabe> AusgabeAnlegenAsync(Guid teamId, string bezeichnung, DateOnly monat, IReadOnlyCollection<Anteil> anteile, string? notiz = null, CancellationToken abbruch = default)
    {
        var (wer, _) = await TeamMitRechtAsync(teamId, abbruch);
        await AnteilePruefenAsync(teamId, anteile.Select(a => a.TopfId), abbruch);
        var a = new GeplanteAusgabe(teamId, bezeichnung, monat, anteile, uhr.GetUtcNow(), notiz);
        daten.Hinzufuegen(a);
        protokoll.Eintragen(wer, "Finanzen.AusgabeGeplant", $"Ausgabe geplant ({a.Anteile.Count} Topf/Töpfe)", a.Id);
        await daten.SpeichernAsync(abbruch);
        return a;
    }

    public async Task<GeplanteAusgabe> AusgabeAendernAsync(Guid teamId, Guid id, string bezeichnung, DateOnly monat, IReadOnlyCollection<Anteil> anteile, string? notiz = null, CancellationToken abbruch = default)
    {
        var a = await AusgabeMitRechtAsync(teamId, id, abbruch);
        await AnteilePruefenAsync(teamId, anteile.Select(x => x.TopfId), abbruch);
        a.Aendern(bezeichnung, monat, anteile, notiz);
        await daten.SpeichernAsync(abbruch);
        return a;
    }

    public async Task AusgabeStandAsync(Guid teamId, Guid id, Ausgabenstand stand, string? buchungVerweis = null, CancellationToken abbruch = default)
    {
        var a = await AusgabeMitRechtAsync(teamId, id, abbruch);
        a.StandSetzen(stand, buchungVerweis);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task AusgabeLoeschenAsync(Guid teamId, Guid id, CancellationToken abbruch = default)
    {
        var a = await AusgabeMitRechtAsync(teamId, id, abbruch);
        daten.Entfernen(a);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>
    /// Buchungen der Kostenstelle(n) einer geplanten Ausgabe, die dazu passen könnten: Betrag des Anteils (±2 %), Belegdatum ab zwei Monate
    /// vor dem geplanten Monat, noch mit keiner anderen Ausgabe verknüpft. Die besten zuerst.
    /// </summary>
    public async Task<IReadOnlyList<Buchung>> BuchungsvorschlaegeAsync(Guid teamId, Guid id, CancellationToken abbruch = default)
    {
        var a = await AusgabeMitRechtAsync(teamId, id, abbruch);
        var kst = (await ToepfeIntern(teamId, abbruch)).Where(t => t.Art == Topfart.Kostenstelle).Select(t => t.Id).ToHashSet();
        var anteile = a.Anteile.Where(x => kst.Contains(x.TopfId)).ToList();
        if (anteile.Count == 0)
        {
            return [];
        }

        var ab = a.Monat.AddMonths(-2);
        var topfIds = anteile.Select(x => x.TopfId).ToList();
        var jahre = new[] { ab.Year, ab.Year + 1, ab.Year + 2 };
        var kandidaten = await daten.ListeAsync(daten.Menge<Buchung>().Where(b => topfIds.Contains(b.TopfId) && jahre.Contains(b.Jahr)), abbruch);
        var vergeben = (await daten.ListeAsync(daten.Menge<GeplanteAusgabe>().Where(x => x.TeamId == teamId && x.Id != id && x.BuchungVerweis != null), abbruch))
            .Select(x => x.BuchungVerweis!).ToHashSet(StringComparer.Ordinal);
        return [.. kandidaten
            .Where(b => !b.IstZugang && b.Belegdatum >= ab && !vergeben.Contains(b.Fingerabdruck))
            .Select(b => (b, Abweichung: anteile.Where(x => x.TopfId == b.TopfId).Min(x => Math.Abs(x.Betrag - b.Betrag) / Math.Max(x.Betrag, 0.01m))))
            .Where(x => x.Abweichung <= 0.02m)
            .OrderBy(x => x.Abweichung).ThenBy(x => Math.Abs(x.b.Belegdatum.DayNumber - a.Monat.DayNumber))
            .Take(10)
            .Select(x => x.b)];
    }

    // ------------------------------------------------------------------ Auslagen

    public async Task<IReadOnlyList<Auslagenansicht>> AuslagenAsync(Guid teamId, CancellationToken abbruch = default)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        var liste = await daten.ListeAsync(daten.Menge<Auslagenabrechnung>().Where(a => a.TeamId == teamId).OrderByDescending(a => a.Angelegt).ThenBy(a => a.Id), abbruch);
        var toepfe = (await ToepfeIntern(teamId, abbruch)).ToDictionary(t => t.Id);
        var namen = await NamenAsync(liste.Select(a => a.KontoId), abbruch);
        return [.. liste.Select(a => new Auslagenansicht(a, namen.GetValueOrDefault(a.KontoId, "unbekannt"), a.Pruefen(toepfe, Heute)))];
    }

    public async Task<Auslagenansicht> AuslageAsync(Guid teamId, Guid id, CancellationToken abbruch = default)
    {
        var a = await AuslageMitRechtAsync(teamId, id, abbruch);
        var toepfe = (await ToepfeIntern(teamId, abbruch)).ToDictionary(t => t.Id);
        return new(a, (await NamenAsync([a.KontoId], abbruch)).GetValueOrDefault(a.KontoId, "unbekannt"), a.Pruefen(toepfe, Heute));
    }

    public async Task<Auslagenabrechnung> AuslageAnlegenAsync(Guid teamId, string bezeichnung, CancellationToken abbruch = default)
    {
        var (wer, _) = await TeamMitRechtAsync(teamId, abbruch);
        var a = new Auslagenabrechnung(teamId, bezeichnung, wer.KontoIdPflicht, uhr.GetUtcNow());
        daten.Hinzufuegen(a);
        protokoll.Eintragen(wer, "Finanzen.AuslageAngelegt", "Sammel-Auslagenabrechnung angelegt", a.Id);
        await daten.SpeichernAsync(abbruch);
        return a;
    }

    public async Task AuslageUmbenennenAsync(Guid teamId, Guid id, string bezeichnung, CancellationToken abbruch = default)
    {
        var a = await AuslageMitRechtAsync(teamId, id, abbruch);
        a.Umbenennen(bezeichnung);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task PositionHinzufuegenAsync(Guid teamId, Guid id, DateOnly kaufdatum, string haendler, string artikel, decimal betrag, Guid topfId, CancellationToken abbruch = default)
    {
        var a = await AuslageMitRechtAsync(teamId, id, abbruch);
        await AnteilePruefenAsync(teamId, [topfId], abbruch);
        if (kaufdatum > Heute)
        {
            throw new FachlicherFehler("Das Kaufdatum liegt in der Zukunft.");
        }

        daten.Hinzufuegen(a.Hinzufuegen(kaufdatum, haendler, artikel, betrag, topfId));
        await daten.SpeichernAsync(abbruch);
    }

    public async Task PositionAendernAsync(Guid teamId, Guid id, Guid positionId, DateOnly kaufdatum, string haendler, string artikel, decimal betrag, Guid topfId, CancellationToken abbruch = default)
    {
        var a = await AuslageMitRechtAsync(teamId, id, abbruch);
        await AnteilePruefenAsync(teamId, [topfId], abbruch);
        a.PositionAendern(positionId, kaufdatum, haendler, artikel, betrag, topfId);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task PositionEntfernenAsync(Guid teamId, Guid id, Guid positionId, CancellationToken abbruch = default)
    {
        var a = await AuslageMitRechtAsync(teamId, id, abbruch);
        a.PositionEntfernen(positionId);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task AuslageStandAsync(Guid teamId, Guid id, Auslagenstand stand, CancellationToken abbruch = default)
    {
        var wer = (await TeamMitRechtAsync(teamId, abbruch)).Wer;
        var a = await AuslageMitRechtAsync(teamId, id, abbruch);
        a.StandSetzen(stand, Heute);
        protokoll.Eintragen(wer, "Finanzen.AuslageStand", $"Auslagenabrechnung: {stand}", a.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task AuslageLoeschenAsync(Guid teamId, Guid id, CancellationToken abbruch = default)
    {
        var a = await AuslageMitRechtAsync(teamId, id, abbruch);
        a.Aenderbar.Sicherstellen();
        daten.Entfernen(a);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task<Finanzdatei> AuslagenXlsxAsync(Guid teamId, Guid id, CancellationToken abbruch = default)
    {
        var ansicht = await AuslageAsync(teamId, id, abbruch);
        var toepfe = (await ToepfeIntern(teamId, abbruch)).ToDictionary(t => t.Id);
        var name = Gefache.GefachDienst.Dateiname(ansicht.Abrechnung.Bezeichnung);
        return new(dokumente.AuslagenXlsx(ansicht.Abrechnung, toepfe, ansicht.Person),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}.xlsx");
    }

    // ------------------------------------------------------------------ Hilfskräfte

    public async Task<IReadOnlyList<Hilfskraft>> HilfskraefteAsync(Guid teamId, CancellationToken abbruch = default)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        return await daten.ListeAsync(daten.Menge<Hilfskraft>().Where(h => h.TeamId == teamId).OrderBy(h => h.Beginn).ThenBy(h => h.Person).ThenBy(h => h.Id), abbruch);
    }

    public async Task<Hilfskraft> HilfskraftAnlegenAsync(Guid teamId, string person, Hilfskraftart art, decimal stunden, DateOnly beginn, DateOnly ende,
        IReadOnlyCollection<(Guid TopfId, decimal Prozent)> anteile, string? notiz = null, CancellationToken abbruch = default)
    {
        var (wer, _) = await TeamMitRechtAsync(teamId, abbruch);
        await AnteileFuerHilfskraefteAsync(teamId, anteile.Select(a => a.TopfId), abbruch);
        var h = new Hilfskraft(teamId, person, art, stunden, beginn, ende, anteile, notiz);
        daten.Hinzufuegen(h);
        protokoll.Eintragen(wer, "Finanzen.HilfskraftGeplant", $"Hilfskraft geplant ({h.Monate} Monate)", h.Id);
        await daten.SpeichernAsync(abbruch);
        return h;
    }

    public async Task HilfskraftAendernAsync(Guid teamId, Guid id, string person, Hilfskraftart art, decimal stunden, DateOnly beginn, DateOnly ende,
        IReadOnlyCollection<(Guid TopfId, decimal Prozent)> anteile, string? notiz = null, CancellationToken abbruch = default)
    {
        var h = await HilfskraftMitRechtAsync(teamId, id, abbruch);
        await AnteileFuerHilfskraefteAsync(teamId, anteile.Select(a => a.TopfId), abbruch);
        h.Aendern(person, art, stunden, beginn, ende, anteile, notiz);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task HilfskraftLoeschenAsync(Guid teamId, Guid id, CancellationToken abbruch = default)
    {
        var h = await HilfskraftMitRechtAsync(teamId, id, abbruch);
        daten.Entfernen(h);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Geltende Sätze für ein Jahr (eigene Werte des Teams oder die der Fakultät).</summary>
    public async Task<IReadOnlyList<Stundensatzansicht>> StundensaetzeAsync(Guid teamId, int jahr, CancellationToken abbruch = default)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        var saetze = await daten.ListeAsync(daten.Menge<Stundensatz>().Where(s => s.TeamId == teamId), abbruch);
        return [.. Enum.GetValues<Hilfskraftart>().Select(a => new Stundensatzansicht(a, jahr, Stundensaetze.Satz(saetze, a, jahr), saetze.Any(s => s.Art == a && s.Jahr == jahr)))];
    }

    public async Task StundensatzSetzenAsync(Guid teamId, int jahr, Hilfskraftart art, decimal euro, CancellationToken abbruch = default)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        var s = await daten.ErsterAsync(daten.Menge<Stundensatz>().Where(x => x.TeamId == teamId && x.Jahr == jahr && x.Art == art), abbruch);
        if (s is null)
        {
            daten.Hinzufuegen(new Stundensatz(teamId, jahr, art, euro));
        }
        else
        {
            s.Setzen(euro);
        }

        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Je Topf, der Hilfskräfte bezahlen darf: Stunden je Monat, die der freie Betrag von <paramref name="beginn"/> bis zum Ende trägt.</summary>
    public async Task<IReadOnlyList<Einstellungsmoeglichkeit>> EinstellungsmoeglichkeitenAsync(Guid teamId, DateOnly beginn, DateOnly? ende = null, CancellationToken abbruch = default)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        var lage = await LageAsync(teamId, abbruch);
        var satz = await SatzfunktionAsync(teamId, abbruch);
        return [.. lage.Select(a => Bindungsrechnung.Moeglichkeit(a.Topf, a.Zahlen.Frei, beginn, ende, satz)).OfType<Einstellungsmoeglichkeit>()];
    }

    /// <summary>Rechner: aus welchen Töpfen und bis wann eine Hilfskraft mit <paramref name="stunden"/> je Monat bezahlt werden kann.</summary>
    public async Task<IReadOnlyList<Hilfskraftabschnitt>> HilfskraftRechnenAsync(Guid teamId, Hilfskraftart art, decimal stunden, DateOnly beginn, DateOnly? ende = null, CancellationToken abbruch = default)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        var lage = await LageAsync(teamId, abbruch);
        var satz = await SatzfunktionAsync(teamId, abbruch);
        return Bindungsrechnung.Vorschlagen(lage.Select(a => (a.Topf, a.Zahlen.Frei)), art, stunden, beginn, ende, satz);
    }

    /// <summary>Übernimmt einen Vorschlag des Rechners: je Abschnitt eine Beschäftigung (100 % aus dem Topf des Abschnitts).</summary>
    public async Task<IReadOnlyList<Hilfskraft>> VorschlagUebernehmenAsync(Guid teamId, string person, Hilfskraftart art, decimal stunden,
        IReadOnlyCollection<(Guid TopfId, DateOnly Von, DateOnly Bis)> abschnitte, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(abschnitte);
        if (abschnitte.Count == 0)
        {
            throw new FachlicherFehler("Der Vorschlag ist leer – es sind keine Mittel frei.");
        }

        var liste = new List<Hilfskraft>();
        foreach (var (topfId, von, bis) in abschnitte)
        {
            liste.Add(await HilfskraftAnlegenAsync(teamId, person, art, stunden, von, bis, [(topfId, 100m)], "aus dem Rechner übernommen", abbruch));
        }

        return liste;
    }

    // ------------------------------------------------------------------ Zeitachse und Aufteilen

    /// <summary>Freie Mittel aller aktiven Töpfe im Monatsraster (FA-716), optional mit einer nur angenommenen Ausgabe.</summary>
    /// <param name="annahme">Angenommene Anteile einer neuen Ausgabe im Monat <paramref name="monat"/> („Was wäre, wenn“).</param>
    public async Task<Zeitachse> ZeitachseAsync(Guid teamId, IReadOnlyCollection<Anteil>? annahme = null, DateOnly? monat = null, CancellationToken abbruch = default)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        var lage = await LageAsync(teamId, abbruch);
        Dictionary<Guid, IReadOnlyList<Bindung>>? zusaetzlich = null;
        if (annahme is { Count: > 0 } && monat is { } m)
        {
            zusaetzlich = annahme.Where(a => a.Betrag > 0).GroupBy(a => a.TopfId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<Bindung>)[.. g.Select(a => new Bindung(a.Betrag, Bindungsrechnung.Monatsende(m), "angenommen"))]);
        }

        var folgejahr = Heute.Year + 1;
        var kst = lage.Where(a => a.Topf.Art == Topfart.Kostenstelle).Select(a => a.Topf.Id).ToList();
        var prognosen = (await daten.ListeAsync(daten.Menge<Budgetprognose>().Where(p => kst.Contains(p.TopfId) && p.Jahr == folgejahr), abbruch))
            .ToDictionary(p => p.TopfId, p => p.Summe);
        return Zeitachse.Berechnen(lage.Select(a => (a.Topf, a.Zahlen, a.Bindungen)), Heute, zusaetzlich, prognosen);
    }

    /// <summary>Vorschlag, eine neue Ausgabe auf die passenden Töpfe aufzuteilen (FA-717).</summary>
    public async Task<Aufteilung> AufteilenAsync(Guid teamId, decimal betrag, Ausgabenzweck zweck, DateOnly monat, CancellationToken abbruch = default)
        => Aufteilungsrechnung.Vorschlagen(betrag, zweck, monat, await ZeitachseAsync(teamId, null, null, abbruch));

    // ------------------------------------------------------------------ intern

    private async Task<Func<Hilfskraftart, int, decimal>> SatzfunktionAsync(Guid teamId, CancellationToken abbruch)
    {
        var saetze = await daten.ListeAsync(daten.Menge<Stundensatz>().Where(s => s.TeamId == teamId), abbruch);
        return (art, jahr) => Stundensaetze.Satz(saetze, art, jahr);
    }

    private async Task AnteilePruefenAsync(Guid teamId, IEnumerable<Guid> topfIds, CancellationToken abbruch)
    {
        var toepfe = (await ToepfeIntern(teamId, abbruch)).ToDictionary(t => t.Id);
        foreach (var id in topfIds)
        {
            if (!toepfe.TryGetValue(id, out var t))
            {
                throw new FachlicherFehler("Diesen Topf gibt es in diesem Team nicht.");
            }

            if (t.Archiviert)
            {
                throw new FachlicherFehler($"{t.Kennung} ist archiviert – erst wieder aktivieren.");
            }
        }
    }

    private async Task AnteileFuerHilfskraefteAsync(Guid teamId, IEnumerable<Guid> topfIds, CancellationToken abbruch)
    {
        var ids = topfIds.ToList();
        await AnteilePruefenAsync(teamId, ids, abbruch);
        var toepfe = (await ToepfeIntern(teamId, abbruch)).ToDictionary(t => t.Id);
        if (ids.Select(i => toepfe[i]).FirstOrDefault(t => !Bindungsrechnung.ErlaubtHilfskraefte(t)) is { } nein)
        {
            throw new FachlicherFehler(nein.Art == Topfart.Investition
                ? $"{nein.Kennung} ist eine Investitionsmaßnahme – daraus werden keine Hilfskräfte bezahlt."
                : $"{nein.Kennung} hat keine bewilligten Personalmittel.");
        }
    }

    private async Task<GeplanteAusgabe> AusgabeMitRechtAsync(Guid teamId, Guid id, CancellationToken abbruch)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        var a = await daten.FindenAsync<GeplanteAusgabe>(id, abbruch);
        return a is not null && a.TeamId == teamId ? a : throw new FachlicherFehler("Diese geplante Ausgabe gibt es in diesem Team nicht.");
    }

    private async Task<Auslagenabrechnung> AuslageMitRechtAsync(Guid teamId, Guid id, CancellationToken abbruch)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        var a = await daten.FindenAsync<Auslagenabrechnung>(id, abbruch);
        return a is not null && a.TeamId == teamId ? a : throw new FachlicherFehler("Diese Auslagenabrechnung gibt es in diesem Team nicht.");
    }

    private async Task<Hilfskraft> HilfskraftMitRechtAsync(Guid teamId, Guid id, CancellationToken abbruch)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        var h = await daten.FindenAsync<Hilfskraft>(id, abbruch);
        return h is not null && h.TeamId == teamId ? h : throw new FachlicherFehler("Diese Beschäftigung gibt es in diesem Team nicht.");
    }

    private async Task<Dictionary<Guid, string>> NamenAsync(IEnumerable<Guid> kontoIds, CancellationToken abbruch)
    {
        var ids = kontoIds.Distinct().ToList();
        return (await daten.ListeAsync(daten.Menge<Konto>().Where(k => ids.Contains(k.Id)), abbruch)).ToDictionary(k => k.Id, k => k.Anzeigename);
    }
}
