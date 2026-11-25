using System.Globalization;
using ProfOMat.Application.Betrieb;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Klausuren;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Klausuren;

/// <summary>Datei zum Herunterladen.</summary>
public sealed record Datei(byte[] Inhalt, string Name, string Typ);

/// <summary>Klausur mit allem, was Ausgaben brauchen.</summary>
public sealed record KlausurAnsicht(Klausur Klausur, IReadOnlyList<Raum> GewaehlteRaeume, IReadOnlyDictionary<Guid, Raum> AlleRaeume, string? TerminText,
    string Verantwortlich, KlausurAuswertung Auswertung)
{
    public string RaumVon(Teilnahme t) => t.RaumId is { } id && AlleRaeume.TryGetValue(id, out var r) ? r.Kuerzel : "";
}

/// <summary>PDF- und XLSX-Ausgaben (Infrastruktur).</summary>
public interface IKlausurDokumente
{
    byte[] Sitzlisten(KlausurAnsicht k);

    byte[] Aushang(KlausurAnsicht k);

    byte[] ErgebnisPdf(KlausurAnsicht k);

    byte[] ErgebnisXlsx(KlausurAnsicht k);
}

/// <summary>
/// Klausuren: Import der Prüfungsliste, Räume und Sitzplätze, Serienbrief-Daten, Aufgaben, Notenschlüssel, Punkte, Veröffentlichung.
/// Sichtbar für die verantwortliche Person und die Systemverwaltung.
/// </summary>
public sealed class KlausurDienst(IDaten daten, IAktuellerBenutzer aktuell, IKlausurDokumente dokumente, Protokoll protokoll, Ortszeit ortszeit, TimeProvider uhr)
{
    private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

    public static Freigabe KlausurenAnlegen(Benutzer wer) => wer.Hat(Rolle.Pruefende) || wer.Hat(Rolle.Systemverwaltung)
        ? Freigabe.Ja
        : Freigabe.Nein("Klausuren legen Prüfende an.");

    public async Task<IReadOnlyList<Klausur>> ListeAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var abfrage = daten.Menge<Klausur>().AsQueryable();
        if (!wer.Hat(Rolle.Systemverwaltung))
        {
            abfrage = abfrage.Where(k => k.VerantwortlichKontoId == wer.KontoId);
        }

        return await daten.ListeAsync(abfrage.OrderByDescending(k => k.Id), abbruch);
    }

    /// <summary>Neue Klausur aus einer Prüfungsliste; enthält sie mehrere Prüfungen, muss eine gewählt sein.</summary>
    public async Task<(Klausur Klausur, ImportErgebnis Ergebnis, IReadOnlyList<string> Hinweise)> AusListeAnlegenAsync(byte[] csv, string? titel, string kuerzel,
        DateTimeOffset? termin, string? pruefungsnummer = null, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        KlausurenAnlegen(wer).Sicherstellen();
        var liste = PruefungslisteLeser.Lesen(csv, string.IsNullOrWhiteSpace(pruefungsnummer) ? null : pruefungsnummer);
        if (liste.AuswahlNoetig)
        {
            throw new FachlicherFehler("Die Datei enthält mehrere Prüfungen – bitte eine auswählen.");
        }

        var k = new Klausur(wer.KontoIdPflicht, string.IsNullOrWhiteSpace(titel) ? liste.Pruefungsname ?? "Klausur" : titel, kuerzel, liste.Pruefungsnummer,
            liste.Pruefer, termin);
        var ergebnis = k.Abgleichen(liste.Zeilen);
        daten.Hinzufuegen(k);
        protokoll.Eintragen(wer, "Klausur.Angelegt", $"Klausur „{k.Titel}“ aus Prüfungsliste angelegt ({ergebnis})", k.Id);
        await daten.SpeichernAsync(abbruch);
        return (k, ergebnis, liste.Hinweise);
    }

    /// <summary>Aktuelle Prüfungsliste erneut hochladen (Nachmeldungen, Abmeldungen).</summary>
    public async Task<(ImportErgebnis Ergebnis, IReadOnlyList<string> Hinweise)> ListeAbgleichenAsync(Guid id, byte[] csv, CancellationToken abbruch = default)
    {
        var (wer, k) = await LadenMitRechtAsync(id, abbruch);
        var liste = PruefungslisteLeser.Lesen(csv);
        if (liste.AuswahlNoetig)
        {
            // Liste mit mehreren Prüfungen: nur die Zeilen der Prüfung dieser Klausur
            liste = k.Pruefungsnummer is { } eigene ? PruefungslisteLeser.Lesen(csv, eigene)
                : throw new FachlicherFehler("Die Datei enthält mehrere Prüfungen, diese Klausur hat aber keine Prüfungsnummer – bitte zuerst eintragen.");
        }
        if (k.Pruefungsnummer is { } nr && liste.Pruefungsnummer is { } neu && nr != neu)
        {
            throw new FachlicherFehler($"Die Liste gehört zur Prüfung {neu}, diese Klausur zu {nr}.");
        }

        var ergebnis = k.Abgleichen(liste.Zeilen);
        protokoll.Eintragen(wer, "Klausur.Abgleich", $"Prüfungsliste abgeglichen: {ergebnis}", k.Id);
        await daten.SpeichernAsync(abbruch);
        return (ergebnis, liste.Hinweise);
    }

    public async Task<KlausurAnsicht> AnsichtAsync(Guid id, CancellationToken abbruch = default)
    {
        var (_, k) = await LadenMitRechtAsync(id, abbruch);
        return await AnsichtBildenAsync(k, abbruch);
    }

    public async Task AendernAsync(Guid id, string titel, string kuerzel, string? pruefungsnummer, DateTimeOffset? termin, decimal aktivSchwelle,
        CancellationToken abbruch = default)
    {
        var (wer, k) = await LadenMitRechtAsync(id, abbruch);
        k.Aendern(titel, kuerzel, pruefungsnummer, termin, aktivSchwelle);
        protokoll.Eintragen(wer, "Klausur.Geaendert", $"Klausur „{k.Titel}“ geändert", k.Id);
        await daten.SpeichernAsync(abbruch);
    }

    // ---------------------------------------------------------------- Räume
    public async Task<IReadOnlyList<Raum>> RaeumeAsync(bool auchInaktive = false, CancellationToken abbruch = default)
    {
        var abfrage = daten.Menge<Raum>().AsQueryable();
        if (!auchInaktive)
        {
            abfrage = abfrage.Where(r => r.Aktiv);
        }

        return await daten.ListeAsync(abfrage.OrderBy(r => r.Kuerzel).ThenBy(r => r.Id), abbruch);
    }

    public async Task RaumSpeichernAsync(Guid? id, string kuerzel, string? bezeichnung, int plaetze, bool aktiv, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        KlausurenAnlegen(wer).Sicherstellen();
        var k = kuerzel?.Trim() ?? "";
        if (await daten.AnzahlAsync(daten.Menge<Raum>().Where(r => r.Kuerzel == k && r.Id != id), abbruch) > 0)
        {
            throw new FachlicherFehler($"Den Raum {k} gibt es schon.");
        }

        if (id is { } vorhanden)
        {
            var raum = await daten.FindenAsync<Raum>(vorhanden, abbruch) ?? throw new FachlicherFehler("Raum nicht gefunden.");
            raum.Aendern(k, bezeichnung, plaetze, aktiv);
        }
        else
        {
            daten.Hinzufuegen(new Raum(k, bezeichnung, plaetze));
        }

        protokoll.Eintragen(wer, "Raum.Gespeichert", $"Raum {k} ({plaetze} Plätze) gespeichert");
        await daten.SpeichernAsync(abbruch);
    }

    public async Task<Raum> RaumAsync(Guid raumId, CancellationToken abbruch = default)
        => await daten.FindenAsync<Raum>(raumId, abbruch) ?? throw new FachlicherFehler("Raum nicht gefunden.");

    /// <summary>Grundriss aus dem Editor speichern; die Platzzahl ergibt sich aus den Tischen.</summary>
    public async Task GrundrissSpeichernAsync(Guid raumId, Raumplan plan, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var wer = await aktuell.ErmittelnAsync(abbruch);
        KlausurenAnlegen(wer).Sicherstellen();
        var raum = await RaumAsync(raumId, abbruch);
        raum.GrundrissSetzen(plan);
        protokoll.Eintragen(wer, "Raum.Grundriss", $"Grundriss {raum.Kuerzel}: {raum.Plaetze} Tische, Nummerierung ab {plan.Startecke.Anzeigename()}"
            + $"{(plan.Richtung == Laufrichtung.Spalten ? " spaltenweise" : " reihenweise")}{(plan.Schlange ? " in Schlangenlinie" : "")}", raum.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task RaeumeWaehlenAsync(Guid id, IReadOnlyList<Guid> raumIds, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(raumIds);
        var (wer, k) = await LadenMitRechtAsync(id, abbruch);
        var alle = (await RaeumeAsync(true, abbruch)).ToDictionary(r => r.Id);
        var gewaehlt = raumIds.Distinct().Select(r => alle.TryGetValue(r, out var raum) ? raum : throw new FachlicherFehler("Raum nicht gefunden.")).ToList();
        k.RaeumeWaehlen(gewaehlt);
        protokoll.Eintragen(wer, "Klausur.Raeume", $"Räume gewählt: {string.Join(", ", gewaehlt.Select(r => r.Kuerzel))}", k.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task VerteilenAsync(Guid id, bool zufaellig, CancellationToken abbruch = default)
    {
        var (wer, k) = await LadenMitRechtAsync(id, abbruch);
        var gewaehlt = await GewaehlteRaeumeAsync(k, abbruch);
        k.Verteilen(gewaehlt, zufaellig, uhr.GetUtcNow());
        protokoll.Eintragen(wer, "Klausur.Verteilt", $"{k.Angemeldete.Count()} Teilnehmende auf {gewaehlt.Count} Räume verteilt ({(zufaellig ? "zufällig" : "alphabetisch")})", k.Id);
        await daten.SpeichernAsync(abbruch);
    }

    // ---------------------------------------------------------------- Bewertung
    public async Task AufgabenSetzenAsync(Guid id, IReadOnlyList<(string Titel, decimal MaxPunkte, bool Sonderpunkte)> aufgaben, CancellationToken abbruch = default)
    {
        var (wer, k) = await LadenMitRechtAsync(id, abbruch);
        k.AufgabenSetzen(aufgaben);
        protokoll.Eintragen(wer, "Klausur.Aufgaben", $"{aufgaben.Count} Aufgaben, Höchstpunktzahl {k.Hoechstpunktzahl.ToString(Deutsch)}", k.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task NotenschluesselSetzenAsync(Guid id, IReadOnlyList<Notenstufe> stufen, CancellationToken abbruch = default)
    {
        var (wer, k) = await LadenMitRechtAsync(id, abbruch);
        k.NotenschluesselSetzen(stufen);
        protokoll.Eintragen(wer, "Klausur.Notenschluessel", $"Notenschlüssel gesetzt (4,0 ab {stufen.Where(s => s.Note == 4.0m).Select(s => s.Mindestpunkte.ToString(Deutsch)).FirstOrDefault() ?? "–"} Punkten)", k.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task PunkteSetzenAsync(Guid id, Guid teilnahmeId, Guid aufgabeId, decimal? punkte, CancellationToken abbruch = default)
    {
        var (_, k) = await LadenMitRechtAsync(id, abbruch);
        k.PunkteSetzen(teilnahmeId, aufgabeId, punkte);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task AnwesendSetzenAsync(Guid id, Guid teilnahmeId, bool? anwesend, CancellationToken abbruch = default)
    {
        var (_, k) = await LadenMitRechtAsync(id, abbruch);
        k.AnwesendSetzen(teilnahmeId, anwesend);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Alle ohne Eintrag als „nicht erschienen“ markieren (nach der Punkteerfassung).</summary>
    public async Task<int> RestNichtErschienenAsync(Guid id, CancellationToken abbruch = default)
    {
        var (wer, k) = await LadenMitRechtAsync(id, abbruch);
        var offen = k.Angemeldete.Where(t => t.Anwesend is null).ToList();
        foreach (var t in offen)
        {
            k.AnwesendSetzen(t.Id, false);
        }

        protokoll.Eintragen(wer, "Klausur.Anwesenheit", $"{offen.Count} ohne Eintrag als nicht erschienen markiert", k.Id);
        await daten.SpeichernAsync(abbruch);
        return offen.Count;
    }

    // ---------------------------------------------------------------- Ausgaben
    /// <summary>
    /// Datenquelle für den Word-Serienbrief (Feldnamen ohne Umlaute und Leerzeichen). Sortiert zum Austeilen: Raum für Raum in der
    /// gewählten Reihenfolge, darin nach Tischnummer (= Laufweg des Nummerierungsmusters); noch nicht Verteilte am Ende.
    /// </summary>
    public async Task<Datei> SerienbriefAsync(Guid id, CancellationToken abbruch = default)
    {
        var a = await AnsichtMitProtokollAsync(id, "Serienbrief", abbruch);
        var k = a.Klausur;
        var datum = k.Termin is { } t ? ortszeit.Lokal(t).ToString("dd.MM.yyyy", Deutsch) : "";
        var zeit = k.Termin is { } t2 ? ortszeit.Klausuruhrzeit(t2) : "";
        var raumfolge = a.GewaehlteRaeume.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var plaetze = a.GewaehlteRaeume.ToDictionary(r => r.Id, r => r.Sitzplaetze().ToDictionary(p => p.Nummer));
        var zeilen = k.Angemeldete
            .OrderBy(x => x.RaumId is { } r && raumfolge.TryGetValue(r, out var i) ? i : int.MaxValue)
            .ThenBy(x => x.Platz ?? int.MaxValue)
            .ThenBy(x => x.Zeile)
            .Select(x =>
            {
                var raum = x.RaumId is { } r && a.AlleRaeume.TryGetValue(r, out var rr) ? rr : null;
                var sitz = raum is not null && x.Platz is { } nr && plaetze.TryGetValue(raum.Id, out var ps) && ps.TryGetValue(nr, out var sp) ? sp : null;
                return new string?[]
                {
                    x.Code, x.Nachname, x.Vorname, x.Matrikelnummer, x.Email, raum?.Kuerzel, raum?.Bezeichnung, x.Platz?.ToString(Deutsch),
                    sitz is { Reihe: > 0 } ? sitz.Reihe.ToString(Deutsch) : "", sitz is { Spalte: > 0 } ? sitz.Spalte.ToString(Deutsch) : "",
                    k.Titel, k.Pruefungsnummer, datum, zeit, k.Pruefer ?? a.Verantwortlich, x.Zeile.ToString(Deutsch),
                };
            });
        var csv = CsvSchreiber.Schreiben(
            ["KlausurID", "Nachname", "Vorname", "Matrikelnummer", "EMail", "Raum", "Raumbezeichnung", "Platz", "Reihe", "PlatzInReihe", "Klausur",
                "Pruefungsnummer", "Datum", "Uhrzeit", "Pruefer", "Listennummer"], zeilen);
        return new Datei(csv, $"Serienbrief-{k.Kuerzel}.csv", "text/csv; charset=utf-8");
    }

    /// <summary>Noten zur Eintragung im Hochschulsystem.</summary>
    public async Task<Datei> NotenCsvAsync(Guid id, CancellationToken abbruch = default)
    {
        var a = await AnsichtMitProtokollAsync(id, "Notenliste", abbruch);
        var zeilen = a.Auswertung.Zeilen.OrderBy(z => z.Teilnahme.Zeile).Select(z => new string?[]
        {
            z.Teilnahme.Matrikelnummer, z.Teilnahme.Nachname, z.Teilnahme.Vorname,
            z.Note?.ToString("0.0", Deutsch), z.Teilnahme.Anwesend switch { false => "nicht erschienen", null => "nicht erfasst", _ => "" },
            z.Summe.ToString(Deutsch),
        });
        return new Datei(CsvSchreiber.Schreiben(["Matrikelnummer", "Nachname", "Vorname", "Note", "Bemerkung", "Punkte"], zeilen),
            $"Noten-{a.Klausur.Kuerzel}.csv", "text/csv; charset=utf-8");
    }

    public async Task<Datei> SitzlistenAsync(Guid id, CancellationToken abbruch = default)
    {
        var a = await AnsichtMitProtokollAsync(id, "Sitzlisten", abbruch);
        return a.Klausur.Verteilt is null
            ? throw new FachlicherFehler("Bitte zuerst Räume wählen und Plätze verteilen.")
            : new Datei(dokumente.Sitzlisten(a), $"Sitzlisten-{a.Klausur.Kuerzel}.pdf", "application/pdf");
    }

    public async Task<Datei> AushangAsync(Guid id, CancellationToken abbruch = default)
    {
        var a = await AnsichtMitProtokollAsync(id, "Aushang", abbruch);
        return a.Klausur.Verteilt is null
            ? throw new FachlicherFehler("Bitte zuerst Räume wählen und Plätze verteilen.")
            : new Datei(dokumente.Aushang(a), $"Aushang-{a.Klausur.Kuerzel}.pdf", "application/pdf");
    }

    public async Task<Datei> ErgebnisPdfAsync(Guid id, CancellationToken abbruch = default)
    {
        var a = await AnsichtMitProtokollAsync(id, "Ergebnis (PDF)", abbruch);
        return new Datei(dokumente.ErgebnisPdf(a), $"Ergebnis-{a.Klausur.Kuerzel}.pdf", "application/pdf");
    }

    public async Task<Datei> ErgebnisXlsxAsync(Guid id, CancellationToken abbruch = default)
    {
        var a = await AnsichtMitProtokollAsync(id, "Ergebnis (XLSX)", abbruch);
        return new Datei(dokumente.ErgebnisXlsx(a), $"Ergebnis-{a.Klausur.Kuerzel}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    // ---------------------------------------------------------------- intern
    private async Task<KlausurAnsicht> AnsichtMitProtokollAsync(Guid id, string was, CancellationToken abbruch)
    {
        var (wer, k) = await LadenMitRechtAsync(id, abbruch);
        protokoll.Eintragen(wer, "Klausur.Export", $"{was} erzeugt", k.Id);
        await daten.SpeichernAsync(abbruch);
        return await AnsichtBildenAsync(k, abbruch);
    }

    private async Task<(Benutzer, Klausur)> LadenMitRechtAsync(Guid id, CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var k = await daten.FindenAsync<Klausur>(id, abbruch) ?? throw new FachlicherFehler("Klausur nicht gefunden.");
        return k.VerantwortlichKontoId == wer.KontoId || wer.Hat(Rolle.Systemverwaltung)
            ? (wer, k)
            : throw new FachlicherFehler("Diese Klausur sieht nur, wer sie verantwortet.");
    }

    private async Task<List<Raum>> GewaehlteRaeumeAsync(Klausur k, CancellationToken abbruch)
    {
        var alle = (await RaeumeAsync(true, abbruch)).ToDictionary(r => r.Id);
        return k.Raeume.OrderBy(r => r.Reihenfolge).Where(r => alle.ContainsKey(r.RaumId)).Select(r => alle[r.RaumId]).ToList();
    }

    private async Task<KlausurAnsicht> AnsichtBildenAsync(Klausur k, CancellationToken abbruch)
    {
        var alle = (await RaeumeAsync(true, abbruch)).ToDictionary(r => r.Id);
        var gewaehlt = k.Raeume.OrderBy(r => r.Reihenfolge).Where(r => alle.ContainsKey(r.RaumId)).Select(r => alle[r.RaumId]).ToList();
        var verantwortlich = (await daten.FindenAsync<Konto>(k.VerantwortlichKontoId, abbruch))?.Anzeigename ?? "";
        return new KlausurAnsicht(k, gewaehlt, alle, k.Termin is { } t ? ortszeit.Klausurtermin(t) : null, verantwortlich, KlausurAuswertung.Berechnen(k));
    }
}
