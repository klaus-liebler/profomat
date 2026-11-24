using ProfOMat.Application.Betrieb;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Katalog;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Katalog;

/// <summary>Ergebnis eines Abgleichs mit dem Hochschulsystem.</summary>
public sealed record AbgleichErgebnis(string Quelle, int Neu, int Geaendert, int Alt, int Reaktiviert)
{
    public override string ToString() => $"{Quelle}: {Neu} neu, {Geaendert} geändert, {Alt} als alt markiert, {Reaktiviert} wieder aktuell";
}

/// <summary>
/// Studiengänge und Prüfungen: Auswahlen (alte nur auf Wunsch), Pflege und Abgleich mit dem Hochschulsystem.
/// Gelöscht wird nie: Was im Hochschulsystem fehlt, wird „alt“; lokale Ergänzungen fasst der Abgleich nicht an.
/// </summary>
public sealed class KatalogDienst(IDaten daten, IAktuellerBenutzer aktuell, IHochschulKatalog quelle, Protokoll protokoll, TimeProvider uhr)
{
    public async Task<IReadOnlyList<Fakultaet>> FakultaetenAsync(CancellationToken abbruch = default)
        => await daten.ListeAsync(daten.Menge<Fakultaet>().OrderBy(f => f.Kuerzel).ThenBy(f => f.Id), abbruch);

    /// <summary>Studiengänge für Auswahlen; alte nur mit <paramref name="auchAlte"/>.</summary>
    public async Task<IReadOnlyList<Studiengang>> StudiengaengeAsync(bool auchAlte, CancellationToken abbruch = default)
    {
        var abfrage = daten.Menge<Studiengang>();
        if (!auchAlte)
        {
            abfrage = abfrage.Where(s => s.AltSeit == null);
        }

        return await daten.ListeAsync(abfrage.OrderBy(s => s.Name).ThenBy(s => s.Abschluss).ThenBy(s => s.Pruefungsordnung).ThenBy(s => s.Id), abbruch);
    }

    /// <summary>Prüfungen eines Studiengangs für Auswahlen; alte nur mit <paramref name="auchAlte"/>.</summary>
    public async Task<IReadOnlyList<Pruefung>> PruefungenAsync(Guid studiengangId, bool auchAlte, CancellationToken abbruch = default)
    {
        var abfrage = daten.Menge<Pruefung>().Where(p => p.StudiengangId == studiengangId);
        if (!auchAlte)
        {
            abfrage = abfrage.Where(p => p.AltSeit == null);
        }

        return await daten.ListeAsync(abfrage.OrderBy(p => p.Titel).ThenBy(p => p.Nummer).ThenBy(p => p.Id), abbruch);
    }

    public async Task<Studiengang> LokalenStudiengangAnlegenAsync(Guid fakultaetId, string kuerzel, string name, string abschluss, string po, CancellationToken abbruch = default)
    {
        var wer = await PflegeAsync(abbruch);
        var s = new Studiengang(fakultaetId, $"lokal:{Ids.Neu():N}", kuerzel, name, abschluss, po, Herkunft.Lokal);
        daten.Hinzufuegen(s);
        protokoll.Eintragen(wer, "Katalog.Lokal", $"Studiengang „{s.Anzeige}“ lokal ergänzt", s.Id);
        await daten.SpeichernAsync(abbruch);
        return s;
    }

    public async Task<Pruefung> LokalePruefungAnlegenAsync(Guid studiengangId, string nummer, string titel, string? form, CancellationToken abbruch = default)
    {
        var wer = await PflegeAsync(abbruch);
        _ = await daten.FindenAsync<Studiengang>(studiengangId, abbruch) ?? throw new FachlicherFehler("Studiengang nicht gefunden.");
        var p = new Pruefung(studiengangId, $"lokal:{Ids.Neu():N}", nummer, titel, form, Herkunft.Lokal);
        daten.Hinzufuegen(p);
        protokoll.Eintragen(wer, "Katalog.Lokal", $"Prüfung „{p.Anzeige}“ lokal ergänzt", p.Id);
        await daten.SpeichernAsync(abbruch);
        return p;
    }

    /// <summary>Von Hand als alt markieren (bzw. wieder aktuell machen). Nie löschen.</summary>
    public async Task AltSetzenAsync(Guid eintragId, bool alt, string? grund, CancellationToken abbruch = default)
    {
        var wer = await PflegeAsync(abbruch);
        Katalogeintrag eintrag = (Katalogeintrag?)await daten.FindenAsync<Studiengang>(eintragId, abbruch)
                                 ?? await daten.FindenAsync<Pruefung>(eintragId, abbruch)
                                 ?? throw new FachlicherFehler("Eintrag nicht gefunden.");
        if (alt)
        {
            eintrag.AlsAltMarkieren(uhr.GetUtcNow(), string.IsNullOrWhiteSpace(grund) ? "von Hand archiviert" : grund, vonHand: true);
        }
        else
        {
            eintrag.Reaktivieren();
        }

        protokoll.Eintragen(wer, "Katalog.Alt", $"{Bezeichnung(eintrag)} {(alt ? "als alt markiert" : "wieder aktuell")}", eintrag.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task FakultaetAendernAsync(Guid fakultaetId, string name, Guid? studiendekanat, string? sekretariatEmail, CancellationToken abbruch = default)
    {
        var wer = await PflegeAsync(abbruch);
        var f = await daten.FindenAsync<Fakultaet>(fakultaetId, abbruch) ?? throw new FachlicherFehler("Fakultät nicht gefunden.");
        await StudiendekanatPruefenAsync(studiendekanat, abbruch);
        f.Aendern(name, studiendekanat, sekretariatEmail);
        protokoll.Eintragen(wer, "Katalog.Fakultaet", $"Fakultät {f.Kuerzel} geändert", f.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task StudiengangStudiendekanatAsync(Guid studiengangId, Guid? studiendekanat, CancellationToken abbruch = default)
    {
        var wer = await PflegeAsync(abbruch);
        var s = await daten.FindenAsync<Studiengang>(studiengangId, abbruch) ?? throw new FachlicherFehler("Studiengang nicht gefunden.");
        await StudiendekanatPruefenAsync(studiendekanat, abbruch);
        s.StudiendekanatFestlegen(studiendekanat);
        protokoll.Eintragen(wer, "Katalog.Studiendekanat", $"Studiendekanat für „{s.Anzeige}“ {(studiendekanat is null ? "auf Fakultät zurückgesetzt" : "festgelegt")}", s.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>
    /// Beim Start: Ist der Katalog noch leer (neue Installation), sofort abgleichen statt bis zum nächtlichen Lauf zu warten –
    /// sonst bleiben alle Studiengang-Auswahlen bis dahin leer. Liefert <c>null</c>, wenn schon Einträge da sind.
    /// </summary>
    public async Task<AbgleichErgebnis?> ErstabgleichAsync(CancellationToken abbruch = default)
        => (await daten.ListeAsync(daten.Menge<Studiengang>().Take(1), abbruch)).Count > 0
            ? null
            : await AbgleichenAsync(alsAutomatik: true, abbruch);

    /// <summary>Abgleich mit dem Hochschulsystem (Hintergrundjob nachts oder „Jetzt abgleichen“).</summary>
    public async Task<AbgleichErgebnis> AbgleichenAsync(bool alsAutomatik = false, CancellationToken abbruch = default)
    {
        var wer = alsAutomatik ? Benutzer.Automatik : await PflegeAsync(abbruch);
        var stand = await quelle.LadenAsync(abbruch);
        var jetzt = uhr.GetUtcNow();
        int neu = 0, geaendert = 0, alt = 0, reaktiviert = 0;

        var fakultaeten = (await daten.ListeAsync(daten.Menge<Fakultaet>(), abbruch)).ToDictionary(f => f.Kuerzel, StringComparer.OrdinalIgnoreCase);
        var studiengaenge = (await daten.ListeAsync(daten.Menge<Studiengang>().Where(s => s.Herkunft == Herkunft.Hochschulsystem), abbruch))
            .ToDictionary(s => s.Schluessel, StringComparer.Ordinal);
        var pruefungen = (await daten.ListeAsync(daten.Menge<Pruefung>().Where(p => p.Herkunft == Herkunft.Hochschulsystem), abbruch))
            .ToDictionary(p => (p.StudiengangId, p.Schluessel));
        var gesehenS = new HashSet<Guid>();
        var gesehenP = new HashSet<Guid>();

        foreach (var fq in stand.Fakultaeten)
        {
            if (!fakultaeten.TryGetValue(fq.Kuerzel, out var fakultaet))
            {
                fakultaet = new Fakultaet(fq.Kuerzel, fq.Name);
                daten.Hinzufuegen(fakultaet);
                fakultaeten[fq.Kuerzel] = fakultaet;
            }

            foreach (var sq in fq.Studiengaenge)
            {
                if (!studiengaenge.TryGetValue(sq.Schluessel, out var s))
                {
                    s = new Studiengang(fakultaet.Id, sq.Schluessel, sq.Kuerzel, sq.Name, sq.Abschluss, sq.Pruefungsordnung, Herkunft.Hochschulsystem);
                    daten.Hinzufuegen(s);
                    studiengaenge[sq.Schluessel] = s;
                    neu++;
                }
                else if (s.Uebernehmen(sq.Kuerzel, sq.Name, sq.Abschluss, sq.Pruefungsordnung))
                {
                    geaendert++;
                }

                if (KatalogAbgleichHilfe.Gesehen(s, jetzt))
                {
                    reaktiviert++;
                }

                gesehenS.Add(s.Id);
                foreach (var pq in sq.Pruefungen)
                {
                    if (!pruefungen.TryGetValue((s.Id, pq.Schluessel), out var p))
                    {
                        p = new Pruefung(s.Id, pq.Schluessel, pq.Nummer, pq.Titel, pq.Pruefungsform, Herkunft.Hochschulsystem);
                        daten.Hinzufuegen(p);
                        pruefungen[(s.Id, pq.Schluessel)] = p;
                        neu++;
                    }
                    else if (p.Uebernehmen(pq.Nummer, pq.Titel, pq.Pruefungsform))
                    {
                        geaendert++;
                    }

                    if (KatalogAbgleichHilfe.Gesehen(p, jetzt))
                    {
                        reaktiviert++;
                    }

                    gesehenP.Add(p.Id);
                }
            }
        }

        foreach (var s in studiengaenge.Values.Where(s => !gesehenS.Contains(s.Id) && !s.IstAlt))
        {
            s.AlsAltMarkieren(jetzt, $"nicht mehr in {quelle.Quelle}", vonHand: false);
            alt++;
        }

        foreach (var p in pruefungen.Values.Where(p => !gesehenP.Contains(p.Id) && !p.IstAlt))
        {
            p.AlsAltMarkieren(jetzt, $"nicht mehr in {quelle.Quelle}", vonHand: false);
            alt++;
        }

        var ergebnis = new AbgleichErgebnis(quelle.Quelle, neu, geaendert, alt, reaktiviert);
        protokoll.Eintragen(wer, "Katalog.Abgleich", ergebnis.ToString());
        await daten.SpeichernAsync(abbruch);
        return ergebnis;
    }

    private async Task<Benutzer> PflegeAsync(CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.KatalogPflegen(wer).Sicherstellen();
        return wer;
    }

    private async Task StudiendekanatPruefenAsync(Guid? kontoId, CancellationToken abbruch)
    {
        if (kontoId is not { } id)
        {
            return;
        }

        var konto = await daten.FindenAsync<Konto>(id, abbruch) ?? throw new FachlicherFehler("Konto nicht gefunden.");
        if (!konto.Hat(Rolle.Studiendekanat))
        {
            throw new FachlicherFehler("Das gewählte Konto hat nicht die Rolle Studiendekanat.");
        }
    }

    private static string Bezeichnung(Katalogeintrag e) => e switch
    {
        Studiengang s => $"Studiengang „{s.Anzeige}“",
        Pruefung p => $"Prüfung „{p.Anzeige}“",
        _ => "Eintrag",
    };
}
