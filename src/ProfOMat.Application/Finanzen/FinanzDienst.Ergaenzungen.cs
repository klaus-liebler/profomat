using System.Globalization;
using ProfOMat.Domain.Finanzen;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Finanzen;

public sealed record Notizansicht(Buchungsnotiz Notiz, string Von);

/// <param name="JeBuchung">Notiz je Fingerabdruck der aktuellen Buchungen.</param>
/// <param name="Verwaist">Notizen, zu denen es im aktuellen Stand keine Buchung mehr gibt (Storno, Korrektur).</param>
public sealed record Buchungsnotizen(IReadOnlyDictionary<string, Notizansicht> JeBuchung, IReadOnlyList<Notizansicht> Verwaist);

/// <param name="Vorlage">Noch nicht gespeichert; Werte aus der Prognose des Vorjahres übernommen.</param>
/// <param name="Gebucht">Tatsächlich gebuchtes Sachmittelbudget des Jahres (930300), sobald importiert.</param>
public sealed record Prognoseansicht(Budgetprognose Prognose, bool Neu, bool Vorlage, decimal? Gebucht);

/// <summary>Ergänzungen: Buchungsnotizen (FA-718), Einzelbuchungen (FA-705), Verlauf und Jahresvergleich (FA-711), Budgetprognose (FA-713), Erinnerungen (FA-712).</summary>
public sealed partial class FinanzDienst
{
    // ------------------------------------------------------------------ Buchungsnotizen

    public async Task<Buchungsnotizen> NotizenAsync(Guid teamId, Guid topfId, int jahr, CancellationToken abbruch = default)
    {
        await TopfMitRechtAsync(teamId, topfId, abbruch);
        var notizen = await daten.ListeAsync(daten.Menge<Buchungsnotiz>().Where(n => n.TopfId == topfId && n.Jahr == jahr), abbruch);
        var namen = await NamenAsync(notizen.Select(n => n.GeaendertVon), abbruch);
        var aktuell = (await daten.ListeAsync(daten.Menge<Buchung>().Where(b => b.TopfId == topfId && b.Jahr == jahr), abbruch))
            .Select(b => b.Fingerabdruck).ToHashSet(StringComparer.Ordinal);
        var ansichten = notizen.Select(n => new Notizansicht(n, namen.GetValueOrDefault(n.GeaendertVon, "unbekannt"))).ToList();
        return new(ansichten.Where(a => aktuell.Contains(a.Notiz.Fingerabdruck)).ToDictionary(a => a.Notiz.Fingerabdruck, StringComparer.Ordinal),
            [.. ansichten.Where(a => !aktuell.Contains(a.Notiz.Fingerabdruck)).OrderBy(a => a.Notiz.Beschreibung, StringComparer.Ordinal)]);
    }

    /// <summary>Kommentar und Markierungen setzen; beides leer entfernt die Notiz.</summary>
    public async Task NotizSetzenAsync(Guid teamId, Guid topfId, Guid buchungId, string? kommentar, Buchungsmarkierung markierung, CancellationToken abbruch = default)
    {
        var (wer, _) = await TopfMitRechtAsync(teamId, topfId, abbruch);
        var b = await daten.FindenAsync<Buchung>(buchungId, abbruch);
        if (b is null || b.TopfId != topfId)
        {
            throw new FachlicherFehler("Diese Buchung gibt es nicht mehr – die Seite neu laden (ein neuer Auszug ersetzt die Buchungen).");
        }

        var fp = b.Fingerabdruck;
        var n = await daten.ErsterAsync(daten.Menge<Buchungsnotiz>().Where(x => x.TopfId == topfId && x.Fingerabdruck == fp), abbruch);
        if (n is null)
        {
            n = new Buchungsnotiz(topfId, b.Jahr, fp, $"{b.Belegdatum:dd.MM.yyyy} · {Geld.Text(b.Betrag)} · {b.Text}");
            daten.Hinzufuegen(n);
        }

        n.Setzen(kommentar, markierung, wer.KontoIdPflicht, uhr.GetUtcNow());
        if (n.Leer)
        {
            daten.Entfernen(n);
        }

        await daten.SpeichernAsync(abbruch);
    }

    public async Task NotizLoeschenAsync(Guid teamId, Guid topfId, Guid notizId, CancellationToken abbruch = default)
    {
        await TopfMitRechtAsync(teamId, topfId, abbruch);
        var n = await daten.FindenAsync<Buchungsnotiz>(notizId, abbruch);
        if (n is not null && n.TopfId == topfId)
        {
            daten.Entfernen(n);
            await daten.SpeichernAsync(abbruch);
        }
    }

    // ------------------------------------------------------------------ Einzelbuchungen von Maßnahmen

    public async Task<IReadOnlyList<Einzelbuchung>> EinzelbuchungenAsync(Guid teamId, Guid topfId, CancellationToken abbruch = default)
    {
        await TopfMitRechtAsync(teamId, topfId, abbruch);
        return await daten.ListeAsync(daten.Menge<Einzelbuchung>().Where(e => e.TopfId == topfId).OrderByDescending(e => e.Datum).ThenBy(e => e.Id), abbruch);
    }

    public async Task EinzelbuchungAnlegenAsync(Guid teamId, Guid topfId, DateOnly datum, decimal betrag, string text, CancellationToken abbruch = default)
    {
        var (_, t) = await TopfMitRechtAsync(teamId, topfId, abbruch);
        if (t.Art == Topfart.Kostenstelle)
        {
            throw new FachlicherFehler("Buchungen der Kostenstelle kommen aus dem SAP-Auszug.");
        }

        if (datum > Heute)
        {
            throw new FachlicherFehler("Das Datum liegt in der Zukunft – dafür eine geplante Ausgabe anlegen.");
        }

        daten.Hinzufuegen(new Einzelbuchung(topfId, datum, betrag, text, uhr.GetUtcNow()));
        await daten.SpeichernAsync(abbruch);
    }

    public async Task EinzelbuchungLoeschenAsync(Guid teamId, Guid topfId, Guid id, CancellationToken abbruch = default)
    {
        await TopfMitRechtAsync(teamId, topfId, abbruch);
        var e = await daten.FindenAsync<Einzelbuchung>(id, abbruch);
        if (e is not null && e.TopfId == topfId)
        {
            daten.Entfernen(e);
            await daten.SpeichernAsync(abbruch);
        }
    }

    // ------------------------------------------------------------------ Verlauf und Jahresvergleich

    public async Task<IReadOnlyList<Monatswert>> VerlaufAsync(Guid teamId, Guid topfId, int jahr, CancellationToken abbruch = default)
    {
        await TopfMitRechtAsync(teamId, topfId, abbruch);
        var buchungen = await daten.ListeAsync(daten.Menge<Buchung>().Where(b => b.TopfId == topfId && b.Jahr == jahr), abbruch);
        var bindungen = jahr == Heute.Year ? (await LageAsync(teamId, abbruch)).FirstOrDefault(a => a.Topf.Id == topfId)?.Bindungen ?? [] : [];
        return Auswertung.Verlauf(jahr, buchungen, [.. bindungen]);
    }

    public async Task<(IReadOnlyList<int> Jahre, IReadOnlyList<(string Gruppe, IReadOnlyDictionary<int, decimal> Werte)> Zeilen)> JahresvergleichAsync(Guid teamId, Guid topfId, CancellationToken abbruch = default)
    {
        await TopfMitRechtAsync(teamId, topfId, abbruch);
        return Auswertung.Jahresvergleich(await daten.ListeAsync(daten.Menge<Buchung>().Where(b => b.TopfId == topfId), abbruch));
    }

    // ------------------------------------------------------------------ Budgetprognose

    public async Task<Prognoseansicht> PrognoseAsync(Guid teamId, Guid topfId, int jahr, CancellationToken abbruch = default)
    {
        var (_, t) = await TopfMitRechtAsync(teamId, topfId, abbruch);
        if (t.Art != Topfart.Kostenstelle)
        {
            throw new FachlicherFehler("Eine Budgetprognose gibt es nur für die Laborkostenstelle.");
        }

        var gebucht = -(await daten.ListeAsync(daten.Menge<Buchung>().Where(b => b.TopfId == topfId && b.Jahr == jahr && b.Kostenart == "930300"), abbruch)).Sum(b => b.Betrag);
        var p = await daten.ErsterAsync(daten.Menge<Budgetprognose>().Where(x => x.TopfId == topfId && x.Jahr == jahr), abbruch);
        if (p is not null)
        {
            return new(p, false, false, gebucht == 0 ? null : gebucht);
        }

        var neu = new Budgetprognose(topfId, jahr);
        var vorjahr = await daten.ErsterAsync(daten.Menge<Budgetprognose>().Where(x => x.TopfId == topfId && x.Jahr == jahr - 1), abbruch);
        if (vorjahr is not null)
        {
            neu.SaetzeSetzen(vorjahr.SatzStudierendeSws, vorjahr.SatzSws, vorjahr.HoechstVerfallenJeSemester, vorjahr.SatzProfessur, vorjahr.SatzMitarbeitende);
            neu.Setzen(vorjahr.Professuren, vorjahr.Mitarbeitende, 0, 0, 0,
                [.. vorjahr.Veranstaltungen.OrderBy(v => v.Nr).Select(v => (Semester: Semesterweiter(v.Semester), v.Bezeichnung, v.Art, v.Teilnehmende, v.Sws))], null);
        }

        return new(neu, true, vorjahr is not null, gebucht == 0 ? null : gebucht);
    }

    public async Task<Budgetprognose> PrognoseSpeichernAsync(Guid teamId, Guid topfId, int jahr, decimal professuren, decimal mitarbeitende, decimal verfallenSoSe,
        decimal verfallenWiSe, decimal nichtAnrechenbar, IReadOnlyCollection<(string Semester, string Bezeichnung, Veranstaltungsart Art, int Teilnehmende, decimal Sws)> veranstaltungen,
        (decimal StudierendeSws, decimal Sws, decimal HoechstVerfallen, decimal Professur, decimal Mitarbeitende) saetze, string? notiz = null, CancellationToken abbruch = default)
    {
        var (wer, t) = await TopfMitRechtAsync(teamId, topfId, abbruch);
        if (t.Art != Topfart.Kostenstelle)
        {
            throw new FachlicherFehler("Eine Budgetprognose gibt es nur für die Laborkostenstelle.");
        }

        if (jahr < 2000 || jahr > Heute.Year + 2)
        {
            throw new FachlicherFehler("Prognosen gibt es bis zwei Jahre im Voraus.");
        }

        var p = await daten.ErsterAsync(daten.Menge<Budgetprognose>().Where(x => x.TopfId == topfId && x.Jahr == jahr), abbruch);
        if (p is null)
        {
            p = new Budgetprognose(topfId, jahr);
            daten.Hinzufuegen(p);
        }

        p.SaetzeSetzen(saetze.StudierendeSws, saetze.Sws, saetze.HoechstVerfallen, saetze.Professur, saetze.Mitarbeitende);
        p.Setzen(professuren, mitarbeitende, verfallenSoSe, verfallenWiSe, nichtAnrechenbar, veranstaltungen, notiz);
        protokoll.Eintragen(wer, "Finanzen.Prognose", $"Budgetprognose {jahr} für {t.Kennung} gespeichert", t.Id);
        await daten.SpeichernAsync(abbruch);
        return p;
    }

    /// <summary>„SoSe 2025“ → „SoSe 2026“, „WiSe 2025/26“ → „WiSe 2026/27“ (Vorlage aus dem Vorjahr).</summary>
    internal static string Semesterweiter(string semester)
    {
        var teile = semester.Split(' ', 2);
        if (teile.Length != 2)
        {
            return semester;
        }

        var jahre = teile[1].Split('/');
        if (!int.TryParse(jahre[0], NumberStyles.None, CultureInfo.InvariantCulture, out var j))
        {
            return semester;
        }

        return jahre.Length == 2 ? $"{teile[0]} {j + 1}/{(j + 2) % 100:00}" : $"{teile[0]} {j + 1}";
    }

    // ------------------------------------------------------------------ Erinnerungen

    public async Task ErinnerungEinstellenAsync(Guid teamId, int tage, CancellationToken abbruch = default)
    {
        var (_, t) = await TeamMitRechtAsync(teamId, abbruch);
        t.ErinnerungEinstellen(tage);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>
    /// Hintergrundjob (täglich): Für jede Frist im Vorlauf des Teams eine Mail an alle Mitglieder, einmal je Frist. Fristen eines Topfs nur, wenn
    /// dort noch Mittel frei sind (Kostenstelle: nur bei einem Überhang über der Übertragsgrenze); Fristen der Fakultät (Anträge) immer.
    /// </summary>
    public async Task<int> ErinnerungenVersendenAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        if (wer.Angemeldet)
        {
            throw new FachlicherFehler("Erinnerungen verschickt nur der Hintergrunddienst.");
        }

        var heute = Heute;
        var mails = 0;
        foreach (var team in await daten.ListeAsync(daten.Menge<Laborteam>().Where(t => t.ErinnerungVorlaufTage > 0).OrderBy(t => t.Id), abbruch))
        {
            var lage = await LageAsync(team.Id, abbruch);
            var zahlen = lage.ToDictionary(a => a.Topf.Id, a => a.Zahlen);
            var faellig = Finanzrechnung.Fristen(lage.Select(a => (a.Topf, a.Zahlen)), heute)
                .Where(f => f.Datum.DayNumber - heute.DayNumber <= team.ErinnerungVorlaufTage)
                .Where(f => f.TopfId is not { } id || (zahlen.TryGetValue(id, out var z) && (lage.First(a => a.Topf.Id == id).Topf.Art == Topfart.Kostenstelle ? z.UeberGrenze > 0 : f.Frei > 0)))
                .ToList();
            if (faellig.Count == 0)
            {
                continue;
            }

            var teamId = team.Id;
            var gesendet = (await daten.ListeAsync(daten.Menge<Finanzerinnerung>().Where(e => e.TeamId == teamId), abbruch)).Select(e => e.Schluessel).ToHashSet(StringComparer.Ordinal);
            var ids = team.Mitglieder.Select(m => m.KontoId).ToList();
            var adressen = (await daten.ListeAsync(daten.Menge<Konto>().Where(k => ids.Contains(k.Id) && k.Aktiv), abbruch)).Select(k => k.Email).Where(e => e.Contains('@', StringComparison.Ordinal)).ToList();
            foreach (var f in faellig)
            {
                var schluessel = $"{f.Datum:yyyyMMdd}|{(f.TopfId is { } tid ? tid.ToString("N") : f.Text)}";
                if (!gesendet.Add(schluessel))
                {
                    continue;
                }

                var tage = f.Datum.DayNumber - heute.DayNumber;
                var betrag = f.TopfId is { } t2 && zahlen.TryGetValue(t2, out var z2)
                    ? z2.UeberGrenze is > 0 ? $"\nÜber der Übertragsgrenze: {Geld.Text(z2.UeberGrenze.Value)} – bis zum Jahresende ausgeben oder fest einplanen.\n"
                    : $"\nNoch frei: {Geld.Text(f.Frei ?? z2.Frei)}.\n"
                    : "";
                var text = $"Frist in {tage} Tagen ({f.Datum:dd.MM.yyyy}): {f.Text}\n{betrag}\nCockpit: {optionen.Value.OeffentlicheAdresse}finanzen/{team.Id}\n\n"
                           + $"Diese Erinnerung geht an alle Mitglieder des Laborteams „{team.Name}“. Vorlauf ändern oder abschalten: Reiter „Team“.";
                foreach (var an in adressen)
                {
                    postausgang.Einreihen(an, $"ProfOMat-Finanzen: {f.Text} am {f.Datum:dd.MM.yyyy}", text);
                    mails++;
                }

                daten.Hinzufuegen(new Finanzerinnerung(team.Id, schluessel, uhr.GetUtcNow()));
            }
        }

        await daten.SpeichernAsync(abbruch);
        return mails;
    }
}
