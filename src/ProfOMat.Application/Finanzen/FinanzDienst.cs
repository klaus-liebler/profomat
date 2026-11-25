using ProfOMat.Application.Betrieb;
using ProfOMat.Domain.Finanzen;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Finanzen;

public sealed record Mitgliedansicht(Guid KontoId, string Name, DateTimeOffset Seit);

public sealed record Teamansicht(Laborteam Team, IReadOnlyList<Mitgliedansicht> Mitglieder);

/// <param name="Bindungen">Was den Topf bindet (geplante Ausgaben, Auslagen, Hilfskraft-Monate).</param>
public sealed record Topfansicht(Topf Topf, Topfkennzahlen Zahlen, IReadOnlyList<Bindung> Bindungen);

/// <param name="FreiVerwendbar">Summe frei aller Kostenstellen.</param>
/// <param name="FreiZweckgebunden">Summe frei aller Investitions- und SQM-Maßnahmen.</param>
/// <param name="OffeneMarkierungen">Je Kostenstelle: Buchungen, die als „nicht ok“, „zur Nachverfolgung“ oder „Rückfrage“ markiert und nicht geklärt sind.</param>
public sealed record Cockpit(Laborteam Team, DateOnly Heute, IReadOnlyList<Topfansicht> Toepfe, IReadOnlyList<Frist> Fristen,
    decimal FreiVerwendbar, decimal FreiZweckgebunden, IReadOnlyDictionary<Guid, int> OffeneMarkierungen);

/// <summary>Was die Übernahme eines Kostenstellenauszugs ändern würde.</summary>
/// <param name="NeuerTopf">Die Kostenstelle ist im Team noch nicht angelegt und wird mit der Übernahme angelegt.</param>
/// <param name="Aelter">Der vorhandene Stand dieses Jahres ist neuer – Übernahme nur mit Bestätigung.</param>
/// <param name="Ueberholt">In derselben Auswahl gibt es einen neueren Stand desselben Jahres.</param>
public sealed record Importvorschau(
    string Dateiname,
    string? Kostenstelle,
    int? Jahr,
    DateOnly? Exportdatum,
    DateOnly? BisDatum,
    int Anzahl,
    decimal? RestNeu,
    decimal? RestBisher,
    int Neu,
    int Entfallen,
    int Unveraendert,
    bool NeuerTopf,
    bool Aelter,
    bool Ueberholt,
    Freigabe Freigabe);

/// <summary>
/// Finanzcockpit für Laborteams (Iteration 6): Teams, Töpfe, Import der SAP-Jahresstände, Stände von Maßnahmen, Kennzahlen.
/// Jede Aktion prüft die Mitgliedschaft im Team; Buchungstexte (mit Namen) erscheinen nicht im Protokoll.
/// </summary>
public sealed partial class FinanzDienst(IDaten daten, IAktuellerBenutzer aktuell, Protokoll protokoll, TimeProvider uhr, Ortszeit zeit, IFinanzDokumente dokumente, ISqmListenLeser sqmLeser,
    Postausgang postausgang, Microsoft.Extensions.Options.IOptions<ProfOMatOptionen> optionen)
{
    /// <summary>Laborteams anlegen Lehrende; Mitglied kann jedes Konto werden, das ein Mitglied aufnimmt.</summary>
    public static Freigabe TeamsAnlegen(Benutzer wer)
    {
        ArgumentNullException.ThrowIfNull(wer);
        return !wer.Angemeldet ? Freigabe.Nein("Bitte zuerst anmelden.")
            : wer.Hat(Rolle.Pruefende) ? Freigabe.Ja
            : Freigabe.Nein("Laborteams legen Lehrende an (Rolle Prüfende). Wer aufgenommen wird, sieht das Team hier.");
    }

    public DateOnly Heute => DateOnly.FromDateTime(zeit.Lokal(uhr.GetUtcNow()));

    // ------------------------------------------------------------------ Teams

    public async Task<IReadOnlyList<Laborteam>> TeamsAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var id = wer.KontoIdPflicht;
        return await daten.ListeAsync(daten.Menge<Laborteam>().Where(t => t.Mitglieder.Any(m => m.KontoId == id)).OrderBy(t => t.Name).ThenBy(t => t.Id), abbruch);
    }

    public async Task<Laborteam> TeamAnlegenAsync(string name, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        TeamsAnlegen(wer).Sicherstellen();
        var t = new Laborteam(name, wer.KontoIdPflicht, uhr.GetUtcNow());
        daten.Hinzufuegen(t);
        protokoll.Eintragen(wer, "Finanzen.TeamAngelegt", "Laborteam angelegt", t.Id);
        await daten.SpeichernAsync(abbruch);
        return t;
    }

    public async Task<Teamansicht> TeamAsync(Guid teamId, CancellationToken abbruch = default)
    {
        var (_, t) = await TeamMitRechtAsync(teamId, abbruch);
        var ids = t.Mitglieder.Select(m => m.KontoId).ToList();
        var namen = (await daten.ListeAsync(daten.Menge<Konto>().Where(k => ids.Contains(k.Id)), abbruch)).ToDictionary(k => k.Id, k => k.Anzeigename);
        return new(t, [.. t.Mitglieder.Select(m => new Mitgliedansicht(m.KontoId, namen.GetValueOrDefault(m.KontoId, "unbekannt"), m.Seit))
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)]);
    }

    public async Task UmbenennenAsync(Guid teamId, string name, CancellationToken abbruch = default)
    {
        var (_, t) = await TeamMitRechtAsync(teamId, abbruch);
        t.Umbenennen(name);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Aktive Konten, die noch nicht im Team sind.</summary>
    public async Task<IReadOnlyList<Mitgliedansicht>> KandidatenAsync(Guid teamId, CancellationToken abbruch = default)
    {
        var (_, t) = await TeamMitRechtAsync(teamId, abbruch);
        var ids = t.Mitglieder.Select(m => m.KontoId).ToList();
        var konten = await daten.ListeAsync(daten.Menge<Konto>().Where(k => k.Aktiv && !ids.Contains(k.Id)), abbruch);
        return [.. konten.Select(k => new Mitgliedansicht(k.Id, k.Anzeigename, default)).OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task AufnehmenAsync(Guid teamId, Guid kontoId, CancellationToken abbruch = default)
    {
        var (wer, t) = await TeamMitRechtAsync(teamId, abbruch);
        var konto = await daten.FindenAsync<Konto>(kontoId, abbruch);
        if (konto is not { Aktiv: true })
        {
            throw new FachlicherFehler("Dieses Konto gibt es nicht oder es ist gesperrt.");
        }

        t.Aufnehmen(kontoId, uhr.GetUtcNow());
        protokoll.Eintragen(wer, "Finanzen.MitgliedAufgenommen", $"Konto {Pseudonym.Id(kontoId)} ins Laborteam aufgenommen", t.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Mitglied entfernen oder selbst austreten.</summary>
    public async Task EntfernenAsync(Guid teamId, Guid kontoId, CancellationToken abbruch = default)
    {
        var (wer, t) = await TeamMitRechtAsync(teamId, abbruch);
        t.Entfernen(kontoId);
        protokoll.Eintragen(wer, "Finanzen.MitgliedEntfernt", $"Konto {Pseudonym.Id(kontoId)} aus dem Laborteam entfernt", t.Id);
        await daten.SpeichernAsync(abbruch);
    }

    // ------------------------------------------------------------------ Töpfe

    public async Task<IReadOnlyList<Topf>> ToepfeAsync(Guid teamId, CancellationToken abbruch = default)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        return await ToepfeIntern(teamId, abbruch);
    }

    public async Task<Topf> TopfAsync(Guid teamId, Guid topfId, CancellationToken abbruch = default)
        => (await TopfMitRechtAsync(teamId, topfId, abbruch)).Topf;

    public Task<Topf> KostenstelleAnlegenAsync(Guid teamId, string nummer, string? bezeichnung = null, CancellationToken abbruch = default)
        => TopfAnlegenAsync(teamId, Topf.Kostenstelle(teamId, nummer, bezeichnung), abbruch);

    public Task<Topf> InvestitionAnlegenAsync(Guid teamId, string antragsnummer, string bezeichnung, decimal bewilligt, string? innenauftrag = null, CancellationToken abbruch = default)
        => TopfAnlegenAsync(teamId, Topf.Investition(teamId, antragsnummer, bezeichnung, bewilligt, innenauftrag), abbruch);

    public Task<Topf> SqmAnlegenAsync(Guid teamId, string antragsnummer, string bezeichnung, decimal personal, decimal sach, string? kategorie = null, CancellationToken abbruch = default)
        => TopfAnlegenAsync(teamId, Topf.Sqm(teamId, antragsnummer, bezeichnung, personal, sach, kategorie), abbruch);

    public async Task<Topf> TopfAendernAsync(Guid teamId, Guid topfId, Topfangaben angaben, CancellationToken abbruch = default)
    {
        var (_, t) = await TopfMitRechtAsync(teamId, topfId, abbruch);
        t.Aendern(angaben);
        await daten.SpeichernAsync(abbruch);
        return t;
    }

    public async Task ArchivierenAsync(Guid teamId, Guid topfId, bool archiviert, CancellationToken abbruch = default)
    {
        var (wer, t) = await TopfMitRechtAsync(teamId, topfId, abbruch);
        t.Archivieren(archiviert);
        protokoll.Eintragen(wer, archiviert ? "Finanzen.TopfArchiviert" : "Finanzen.TopfReaktiviert", $"{t.Art} {t.Kennung} {(archiviert ? "archiviert" : "wieder aktiv")}", t.Id);
        await daten.SpeichernAsync(abbruch);
    }

    // ------------------------------------------------------------------ Stände von Maßnahmen

    public async Task<Massnahmenstand> StandErfassenAsync(Guid teamId, Guid topfId, DateOnly stichtag, decimal verausgabt, string? notiz = null, CancellationToken abbruch = default)
    {
        var (wer, t) = await TopfMitRechtAsync(teamId, topfId, abbruch);
        if (t.Art == Topfart.Kostenstelle)
        {
            throw new FachlicherFehler("Den Stand der Kostenstelle liefert der SAP-Auszug – bitte die Datei importieren.");
        }

        if (stichtag > Heute)
        {
            throw new FachlicherFehler("Der Stichtag liegt in der Zukunft.");
        }

        var s = new Massnahmenstand(t.Id, stichtag, verausgabt, Standquelle.Hand, uhr.GetUtcNow(), notiz);
        daten.Hinzufuegen(s);
        protokoll.Eintragen(wer, "Finanzen.StandErfasst", $"Stand {t.Kennung} zum {stichtag:dd.MM.yyyy} von Hand erfasst", t.Id);
        await daten.SpeichernAsync(abbruch);
        return s;
    }

    public async Task<IReadOnlyList<Massnahmenstand>> StaendeAsync(Guid teamId, Guid topfId, CancellationToken abbruch = default)
    {
        await TopfMitRechtAsync(teamId, topfId, abbruch);
        return await daten.ListeAsync(daten.Menge<Massnahmenstand>().Where(s => s.TopfId == topfId)
            .OrderByDescending(s => s.Stichtag).ThenByDescending(s => s.Erfasst).ThenBy(s => s.Id), abbruch);
    }

    // ------------------------------------------------------------------ Kostenstelle: Jahre und Buchungen

    public async Task<IReadOnlyList<Kostenstellenstand>> JahreAsync(Guid teamId, Guid topfId, CancellationToken abbruch = default)
    {
        await TopfMitRechtAsync(teamId, topfId, abbruch);
        return await daten.ListeAsync(daten.Menge<Kostenstellenstand>().Where(s => s.TopfId == topfId).OrderByDescending(s => s.Jahr).ThenBy(s => s.Id), abbruch);
    }

    public async Task<IReadOnlyList<Buchung>> BuchungenAsync(Guid teamId, Guid topfId, int jahr, CancellationToken abbruch = default)
    {
        await TopfMitRechtAsync(teamId, topfId, abbruch);
        return await daten.ListeAsync(daten.Menge<Buchung>().Where(b => b.TopfId == topfId && b.Jahr == jahr).OrderBy(b => b.Nr).ThenBy(b => b.Id), abbruch);
    }

    /// <summary>
    /// Vorschau für eine oder mehrere Dateien. Jede Datei ist der Jahresstand ab 01.01. und ersetzt die Buchungen dieses Jahres.
    /// Gibt es in der Auswahl mehrere Stände desselben Jahres, ist nur der neueste zur Übernahme vorgesehen.
    /// </summary>
    public async Task<IReadOnlyList<Importvorschau>> ImportVorschauAsync(Guid teamId, IReadOnlyList<(string Name, byte[] Inhalt)> dateien, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(dateien);
        await TeamMitRechtAsync(teamId, abbruch);
        var gelesen = new List<(string Name, Kostenstellenauszug? Auszug, string? Fehler)>();
        foreach (var (name, inhalt) in dateien)
        {
            try
            {
                gelesen.Add((name, KostenstellenauszugLeser.Lesen(inhalt, name), null));
            }
            catch (FachlicherFehler ex)
            {
                gelesen.Add((name, null, ex.Message));
            }
        }

        var neueste = gelesen.Where(g => g.Auszug is { Gueltig: true })
            .GroupBy(g => (g.Auszug!.Kostenstelle, g.Auszug.Jahr))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => Rang(x.Auszug!.BisDatum, x.Name, x.Auszug.Zeilen.Count, x.Auszug.Exportdatum))
                .ThenByDescending(x => x.Name, StringComparer.Ordinal).First().Name);
        var ergebnis = new List<Importvorschau>();
        foreach (var (name, auszug, fehler) in gelesen)
        {
            if (auszug is null)
            {
                ergebnis.Add(new(name, null, null, null, null, 0, null, null, 0, 0, 0, false, false, false, Freigabe.Nein(fehler!)));
                continue;
            }

            var ueberholt = auszug.Gueltig && neueste[(auszug.Kostenstelle, auszug.Jahr)] != name;
            ergebnis.Add(await VorschauAsync(teamId, auszug, ueberholt, abbruch));
        }

        return ergebnis;
    }

    /// <summary>Übernimmt einen Jahresstand (legt die Kostenstelle bei Bedarf an).</summary>
    /// <param name="aelterBestaetigt">Auch übernehmen, wenn der vorhandene Stand neuer ist.</param>
    public async Task<Importvorschau> ImportUebernehmenAsync(Guid teamId, string dateiname, byte[] inhalt, bool aelterBestaetigt = false, CancellationToken abbruch = default)
    {
        var (wer, _) = await TeamMitRechtAsync(teamId, abbruch);
        var auszug = KostenstellenauszugLeser.Lesen(inhalt, dateiname);
        var v = await VorschauAsync(teamId, auszug, false, abbruch);
        v.Freigabe.Sicherstellen();
        if (v.Aelter && !aelterBestaetigt)
        {
            throw new FachlicherFehler($"Für {auszug.Jahr} ist schon ein neuerer Stand übernommen. Nur mit Bestätigung durch einen älteren ersetzen.");
        }

        var topf = (await ToepfeIntern(teamId, abbruch)).FirstOrDefault(t => t.Art == Topfart.Kostenstelle && t.Kennung == auszug.Kostenstelle);
        if (topf is null)
        {
            topf = Topf.Kostenstelle(teamId, auszug.Kostenstelle);
            daten.Hinzufuegen(topf);
            protokoll.Eintragen(wer, "Finanzen.TopfAngelegt", $"Kostenstelle {topf.Kennung} beim Import angelegt", topf.Id);
        }

        var topfId = topf.Id;
        foreach (var alt in await daten.ListeAsync(daten.Menge<Buchung>().Where(b => b.TopfId == topfId && b.Jahr == auszug.Jahr), abbruch))
        {
            daten.Entfernen(alt);
        }

        foreach (var z in auszug.Zeilen)
        {
            daten.Hinzufuegen(new Buchung(topfId, auszug.Jahr, z.Nr, z.Erfasst, z.Belegdatum, z.Buchungsdatum, z.Kostenart, z.KostenartText, z.Betrag, z.Text, z.Belegnummer));
        }

        var stand = await daten.ErsterAsync(daten.Menge<Kostenstellenstand>().Where(s => s.TopfId == topfId && s.Jahr == auszug.Jahr), abbruch);
        if (stand is null)
        {
            daten.Hinzufuegen(new Kostenstellenstand(topfId, auszug.Jahr, auszug.Dateiname, auszug.Exportdatum, auszug.BisDatum, uhr.GetUtcNow(), auszug.Zeilen.Count, auszug.Summe));
        }
        else
        {
            stand.Aktualisieren(auszug.Dateiname, auszug.Exportdatum, auszug.BisDatum, uhr.GetUtcNow(), auszug.Zeilen.Count, auszug.Summe);
        }

        protokoll.Eintragen(wer, "Finanzen.AuszugUebernommen",
            $"Kostenstellenauszug {auszug.Kostenstelle}/{auszug.Jahr} übernommen ({auszug.Zeilen.Count} Buchungen, +{v.Neu}/−{v.Entfallen})", topfId);
        await daten.SpeichernAsync(abbruch);
        return v;
    }

    // ------------------------------------------------------------------ Cockpit

    public async Task<Cockpit> CockpitAsync(Guid teamId, CancellationToken abbruch = default)
    {
        var (_, team) = await TeamMitRechtAsync(teamId, abbruch);
        var heute = Heute;
        var ansichten = await LageAsync(teamId, abbruch);
        var fristen = Finanzrechnung.Fristen(ansichten.Select(a => (a.Topf, a.Zahlen)), heute);
        var ids = ansichten.Select(a => a.Topf.Id).ToList();
        var markiert = (await daten.ListeAsync(daten.Menge<Buchungsnotiz>().Where(n => ids.Contains(n.TopfId) && n.Markierung != Buchungsmarkierung.Keine), abbruch))
            .Where(n => n.Offen).GroupBy(n => n.TopfId).ToDictionary(g => g.Key, g => g.Count());
        return new(team, heute, ansichten, fristen,
            ansichten.Where(a => !a.Topf.Zweckgebunden).Sum(a => a.Zahlen.Frei),
            ansichten.Where(a => a.Topf.Zweckgebunden && (a.Topf.Stichtag is null || a.Topf.Stichtag >= heute)).Sum(a => Math.Max(0, a.Zahlen.Frei)),
            markiert);
    }

    /// <summary>Kennzahlen aller aktiven Töpfe mit ihren Bindungen (ohne Rechteprüfung – nur nach <see cref="TeamMitRechtAsync"/>).</summary>
    private async Task<List<Topfansicht>> LageAsync(Guid teamId, CancellationToken abbruch)
    {
        var heute = Heute;
        var toepfe = (await ToepfeIntern(teamId, abbruch)).Where(t => !t.Archiviert).ToList();
        var ids = toepfe.Select(t => t.Id).ToList();
        var jahr = heute.Year;
        var buchungen = (await daten.ListeAsync(daten.Menge<Buchung>().Where(b => ids.Contains(b.TopfId) && b.Jahr == jahr), abbruch)).ToLookup(b => b.TopfId);
        var vorjahre = (await daten.ListeAsync(daten.Menge<Kostenstellenstand>().Where(s => ids.Contains(s.TopfId) && s.Jahr == jahr - 1), abbruch))
            .ToDictionary(s => s.TopfId);
        var staende = (await daten.ListeAsync(daten.Menge<Massnahmenstand>().Where(s => ids.Contains(s.TopfId)), abbruch))
            .GroupBy(s => s.TopfId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Stichtag).ThenByDescending(s => s.Erfasst).First());
        var ausgaben = await daten.ListeAsync(daten.Menge<GeplanteAusgabe>().Where(a => a.TeamId == teamId && a.Stand != Ausgabenstand.Erledigt), abbruch);
        var auslagen = await daten.ListeAsync(daten.Menge<Auslagenabrechnung>().Where(a => a.TeamId == teamId && a.Stand != Auslagenstand.Erstattet), abbruch);
        var hilfskraefte = await daten.ListeAsync(daten.Menge<Hilfskraft>().Where(h => h.TeamId == teamId), abbruch);
        var saetze = await daten.ListeAsync(daten.Menge<Stundensatz>().Where(s => s.TeamId == teamId), abbruch);
        decimal Satz(Hilfskraftart art, int j) => Stundensaetze.Satz(saetze, art, j);
        var einzel = (await daten.ListeAsync(daten.Menge<Einzelbuchung>().Where(e => ids.Contains(e.TopfId)), abbruch)).ToLookup(e => e.TopfId);

        var ansichten = new List<Topfansicht>();
        foreach (var t in toepfe)
        {
            List<Buchung> jahresbuchungen = [.. buchungen[t.Id]];
            DateOnly? bezahltBis = t.Art == Topfart.Kostenstelle
                ? jahresbuchungen.Count > 0 ? jahresbuchungen.Max(b => b.Buchungsdatum) : vorjahre.GetValueOrDefault(t.Id)?.BisDatum
                : staende.GetValueOrDefault(t.Id)?.Stichtag;
            var bindungen = Bindungsrechnung.FuerTopf(t.Id, ausgaben, auslagen, hilfskraefte, Satz, bezahltBis, heute);
            var zahlen = t.Art == Topfart.Kostenstelle
                ? Finanzrechnung.Kostenstelle(t, jahr, jahresbuchungen, vorjahre.GetValueOrDefault(t.Id)?.Rest, bindungen, heute)
                : Finanzrechnung.Massnahme(t, staende.GetValueOrDefault(t.Id), bindungen, heute, [.. einzel[t.Id]]);
            ansichten.Add(new(t, zahlen, bindungen));
        }

        return ansichten;
    }

    // ------------------------------------------------------------------ intern

    private async Task<Importvorschau> VorschauAsync(Guid teamId, Kostenstellenauszug a, bool ueberholt, CancellationToken abbruch)
    {
        var topf = (await ToepfeIntern(teamId, abbruch)).FirstOrDefault(t => t.Art == Topfart.Kostenstelle && t.Kennung == a.Kostenstelle);
        var alt = new List<Buchung>();
        Kostenstellenstand? stand = null;
        if (topf is not null)
        {
            var topfId = topf.Id;
            alt = await daten.ListeAsync(daten.Menge<Buchung>().Where(b => b.TopfId == topfId && b.Jahr == a.Jahr), abbruch);
            stand = await daten.ErsterAsync(daten.Menge<Kostenstellenstand>().Where(s => s.TopfId == topfId && s.Jahr == a.Jahr), abbruch);
        }

        var neu = a.Zeilen.Select(z => new Buchung(Guid.Empty, a.Jahr, z.Nr, z.Erfasst, z.Belegdatum, z.Buchungsdatum, z.Kostenart, z.KostenartText, z.Betrag, z.Text, z.Belegnummer).Fingerabdruck).ToList();
        var bisher = alt.Select(b => b.Fingerabdruck).ToList();
        var gleich = Schnittmenge(bisher, neu);
        var aelter = stand is not null
                     && Rang(stand.BisDatum, stand.Dateiname, stand.Anzahl, stand.Exportdatum).CompareTo(Rang(a.BisDatum, a.Dateiname, a.Zeilen.Count, a.Exportdatum)) > 0;
        var freigabe = !a.Gueltig ? Freigabe.Nein(string.Join(" ", a.Fehler))
            : topf is { Archiviert: true } ? Freigabe.Nein($"Die Kostenstelle {a.Kostenstelle} ist archiviert – erst wieder aktivieren.")
            : ueberholt ? Freigabe.Nein($"In der Auswahl ist ein neuerer Stand für {a.Jahr} – dieser wird übernommen, der ältere übersprungen.")
            : Freigabe.Ja;
        return new(a.Dateiname, a.Kostenstelle, a.Jahr, a.Exportdatum, a.BisDatum, a.Zeilen.Count, -a.Summe, stand?.Rest,
            neu.Count - gleich, bisher.Count - gleich, gleich, topf is null, aelter, ueberholt, freigabe);
    }

    /// <summary>
    /// Welcher Jahresstand ist neuer? Zuerst das späteste Buchungsdatum, dann der Jahresabschluss („Abschluss 2024.xls“ hat oft dasselbe
    /// letzte Buchungsdatum wie der Januar-Auszug, aber mehr Buchungen), dann die Zahl der Buchungen, zuletzt das Datum im Dateinamen.
    /// </summary>
    internal static (DateOnly Bis, int Abschluss, int Anzahl, DateOnly Export) Rang(DateOnly? bis, string dateiname, int anzahl, DateOnly? export)
        => (bis ?? DateOnly.MinValue, dateiname.Contains("abschluss", StringComparison.OrdinalIgnoreCase) ? 1 : 0, anzahl, export ?? DateOnly.MinValue);

    /// <summary>Anzahl gemeinsamer Einträge (Multimenge: gleiche Buchung zweimal zählt zweimal).</summary>
    private static int Schnittmenge(IEnumerable<string> a, IEnumerable<string> b)
    {
        var zaehler = a.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var gleich = 0;
        foreach (var x in b)
        {
            if (zaehler.TryGetValue(x, out var n) && n > 0)
            {
                zaehler[x] = n - 1;
                gleich++;
            }
        }

        return gleich;
    }

    private async Task<Topf> TopfAnlegenAsync(Guid teamId, Topf topf, CancellationToken abbruch)
    {
        var (wer, _) = await TeamMitRechtAsync(teamId, abbruch);
        if ((await ToepfeIntern(teamId, abbruch)).Any(t => t.Art == topf.Art && t.Kennung == topf.Kennung))
        {
            throw new FachlicherFehler($"„{topf.Kennung}“ gibt es in diesem Team schon.");
        }

        daten.Hinzufuegen(topf);
        protokoll.Eintragen(wer, "Finanzen.TopfAngelegt", $"{topf.Art} {topf.Kennung} angelegt", topf.Id);
        await daten.SpeichernAsync(abbruch);
        return topf;
    }

    private Task<List<Topf>> ToepfeIntern(Guid teamId, CancellationToken abbruch)
        => daten.ListeAsync(daten.Menge<Topf>().Where(t => t.TeamId == teamId).OrderBy(t => t.Art).ThenBy(t => t.Kennung).ThenBy(t => t.Id), abbruch);

    private async Task<(Benutzer Wer, Laborteam Team)> TeamMitRechtAsync(Guid teamId, CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var t = await daten.FindenAsync<Laborteam>(teamId, abbruch);
        return t is not null && t.IstMitglied(wer.KontoId) ? (wer, t)
            : throw new FachlicherFehler("Diese Finanzdaten sieht nur, wer zum Laborteam gehört.");
    }

    private async Task<(Benutzer Wer, Topf Topf)> TopfMitRechtAsync(Guid teamId, Guid topfId, CancellationToken abbruch)
    {
        var (wer, _) = await TeamMitRechtAsync(teamId, abbruch);
        var t = await daten.FindenAsync<Topf>(topfId, abbruch);
        return t is not null && t.TeamId == teamId ? (wer, t) : throw new FachlicherFehler("Diesen Topf gibt es in diesem Team nicht.");
    }
}
