using ProfOMat.Domain.Finanzen;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Finanzen;

/// <summary>Stände der Maßnahmen aus Listen der Fakultät: SQM-Excel (FA-704) und Investitionen per KI-JSON (FA-703).</summary>
public sealed partial class FinanzDienst
{
    // ------------------------------------------------------------------ SQM-Excel

    /// <summary>
    /// Vorschau: Anträge, die schon Töpfe des Teams sind, und Anträge von Teammitgliedern (Nachname in „Antragsteller“) zum Anlegen.
    /// Alle anderen Zeilen werden nur gezählt – weder angezeigt noch gespeichert.
    /// </summary>
    public async Task<Massnahmenimport> SqmVorschauAsync(Guid teamId, byte[] inhalt, DateOnly? stichtag = null, CancellationToken abbruch = default)
    {
        var (_, team) = await TeamMitRechtAsync(teamId, abbruch);
        var liste = sqmLeser.Lesen(inhalt);
        var tag = StichtagPruefen(stichtag ?? liste.Stand);
        var toepfe = (await ToepfeIntern(teamId, abbruch)).Where(t => t.Art == Topfart.Sqm).ToDictionary(t => t.Kennung);
        var staende = await LetzteStaendeAsync(toepfe.Values.Select(t => t.Id), abbruch);
        var namen = await NachnamenAsync(team, abbruch);
        var ergebnis = new List<Massnahmenvorschau>();
        var uebrige = 0;
        foreach (var z in liste.Zeilen.GroupBy(z => z.Antragsnummer).Select(g => g.First()))
        {
            var bewilligt = z.Personal + z.Sach > 0 ? z.Personal + z.Sach : z.Gesamt;
            var topf = toepfe.GetValueOrDefault(z.Antragsnummer);
            if (topf is null && !namen.Any(n => EnthaeltWort(z.Antragsteller, n)))
            {
                uebrige++;
                continue;
            }

            var hinweise = new List<string>();
            if (topf is not null && topf.Bewilligt != bewilligt)
            {
                hinweise.Add($"bewilligt ändert sich: {Geld.Text(topf.Bewilligt)} → {Geld.Text(bewilligt)}");
            }

            if (z.Verausgabt > bewilligt)
            {
                hinweise.Add("mehr verausgabt als bewilligt");
            }

            if (z.Verausgabt is null)
            {
                hinweise.Add("keine Angabe „verausgabt“ in der Liste");
            }

            ergebnis.Add(new(z.Antragsnummer, z.Bezeichnung, z.Antragsteller, topf?.Id, bewilligt, topf?.Bewilligt, z.Verausgabt,
                topf is null ? null : staende.GetValueOrDefault(topf.Id)?.Verausgabt, hinweise));
        }

        return new(tag, ergebnis, uebrige, []);
    }

    /// <param name="anlegen">Antragsnummern aus der Vorschau, die als neue SQM-Töpfe angelegt werden sollen.</param>
    public async Task<int> SqmUebernehmenAsync(Guid teamId, byte[] inhalt, DateOnly? stichtag, IReadOnlyCollection<string> anlegen, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(anlegen);
        var vorschau = await SqmVorschauAsync(teamId, inhalt, stichtag, abbruch);
        var zeilen = sqmLeser.Lesen(inhalt).Zeilen.GroupBy(z => z.Antragsnummer).ToDictionary(g => g.Key, g => g.First());
        var wer = (await TeamMitRechtAsync(teamId, abbruch)).Wer;
        var anzahl = 0;
        foreach (var v in vorschau.Massnahmen)
        {
            var z = zeilen[v.Antragsnummer];
            var (personal, sach) = z.Personal + z.Sach > 0 ? (z.Personal, z.Sach) : (0m, z.Gesamt);
            Topf? topf;
            if (v.TopfId is { } id)
            {
                topf = await daten.FindenAsync<Topf>(id, abbruch);
                if (topf is null || topf.Archiviert)
                {
                    continue;
                }

                if (topf.Bewilligt != v.Bewilligt)
                {
                    topf.Aendern(topf.Angaben with { BewilligtPersonal = personal, BewilligtSach = sach, Kategorie = topf.Kategorie ?? z.Kategorie });
                }
            }
            else if (anlegen.Contains(v.Antragsnummer, StringComparer.OrdinalIgnoreCase))
            {
                topf = Topf.Sqm(teamId, v.Antragsnummer, v.Bezeichnung, personal, sach, z.Kategorie);
                topf.Aendern(topf.Angaben with { Verantwortlich = z.Antragsteller });
                daten.Hinzufuegen(topf);
            }
            else
            {
                continue;
            }

            await StandErgaenzenAsync(topf.Id, vorschau.Stichtag, v.Ausgegeben, Standquelle.Excel, abbruch);
            anzahl++;
        }

        protokoll.Eintragen(wer, "Finanzen.SqmListe", $"SQM-Liste übernommen ({anzahl} Maßnahme(n), Stichtag {vorschau.Stichtag:dd.MM.yyyy})", teamId);
        await daten.SpeichernAsync(abbruch);
        return anzahl;
    }

    // ------------------------------------------------------------------ Investitionen per KI-JSON

    public async Task<Massnahmenimport> InvestitionenVorschauAsync(Guid teamId, string json, CancellationToken abbruch = default)
    {
        await TeamMitRechtAsync(teamId, abbruch);
        var inhalt = InvestitionenJson.Lesen(json);
        var tag = StichtagPruefen(inhalt.Stichtag);
        var toepfe = (await ToepfeIntern(teamId, abbruch)).Where(t => t.Art == Topfart.Investition).ToDictionary(t => t.Kennung);
        var staende = await LetzteStaendeAsync(toepfe.Values.Select(t => t.Id), abbruch);
        var liste = inhalt.Massnahmen.Select(e =>
        {
            var topf = toepfe.GetValueOrDefault(e.Antragsnummer);
            var hinweise = InvestitionenJson.Pruefen(e).ToList();
            if (topf is not null && topf.Bewilligt != e.Budget)
            {
                hinweise.Add($"bewilligt ändert sich: {Geld.Text(topf.Bewilligt)} → {Geld.Text(e.Budget)}");
            }

            return new Massnahmenvorschau(e.Antragsnummer, e.Laborbereich ?? "", null, topf?.Id, e.Budget, topf?.Bewilligt, e.Ausgegeben,
                topf is null ? null : staende.GetValueOrDefault(topf.Id)?.Verausgabt, hinweise);
        }).ToList();
        return new(tag, liste, 0, []);
    }

    public async Task<int> InvestitionenUebernehmenAsync(Guid teamId, string json, IReadOnlyCollection<string> anlegen, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(anlegen);
        var vorschau = await InvestitionenVorschauAsync(teamId, json, abbruch);
        var wer = (await TeamMitRechtAsync(teamId, abbruch)).Wer;
        var anzahl = 0;
        foreach (var v in vorschau.Massnahmen)
        {
            Topf? topf;
            if (v.TopfId is { } id)
            {
                topf = await daten.FindenAsync<Topf>(id, abbruch);
                if (topf is null || topf.Archiviert)
                {
                    continue;
                }

                if (topf.Bewilligt != v.Bewilligt)
                {
                    topf.Aendern(topf.Angaben with { BewilligtPersonal = 0, BewilligtSach = v.Bewilligt });
                }
            }
            else if (anlegen.Contains(v.Antragsnummer, StringComparer.OrdinalIgnoreCase))
            {
                topf = Topf.Investition(teamId, v.Antragsnummer, string.IsNullOrWhiteSpace(v.Bezeichnung) ? $"Investition {v.Antragsnummer}" : $"Investition {v.Bezeichnung}", v.Bewilligt);
                daten.Hinzufuegen(topf);
            }
            else
            {
                continue;
            }

            await StandErgaenzenAsync(topf.Id, vorschau.Stichtag, v.Ausgegeben is { } a ? Math.Max(0, a) : null, Standquelle.KiJson, abbruch);
            anzahl++;
        }

        protokoll.Eintragen(wer, "Finanzen.InvestitionenJson", $"Investitionsstand übernommen ({anzahl} Maßnahme(n), Stichtag {vorschau.Stichtag:dd.MM.yyyy})", teamId);
        await daten.SpeichernAsync(abbruch);
        return anzahl;
    }

    // ------------------------------------------------------------------ Beschlussliste der Investitionsanträge (FA-719)

    /// <summary>
    /// Vorschau: Anträge, die schon Investitionstöpfe des Teams sind, und Anträge von Teammitgliedern (Nachname unter den Antragstellern)
    /// zum Anlegen. Alle anderen werden nur gezählt – weder angezeigt noch gespeichert.
    /// </summary>
    public async Task<Antragsimport> AntraegeVorschauAsync(Guid teamId, string json, CancellationToken abbruch = default)
    {
        var (_, team) = await TeamMitRechtAsync(teamId, abbruch);
        var inhalt = InvestitionsantraegeJson.Lesen(json);
        var toepfe = (await ToepfeIntern(teamId, abbruch)).Where(t => t.Art == Topfart.Investition).ToDictionary(t => t.Kennung);
        var namen = await NachnamenAsync(team, abbruch);
        var liste = new List<Antragsvorschau>();
        var uebrige = 0;
        foreach (var e in inhalt.Antraege)
        {
            var topf = toepfe.GetValueOrDefault(e.Antragsnummer);
            if (topf is null && !namen.Any(n => EnthaeltWort(e.Antragsteller ?? "", n)))
            {
                uebrige++;
                continue;
            }

            var aenderungen = new List<string>();
            if (topf is not null)
            {
                if (topf.Bewilligt != e.Sachkosten)
                {
                    aenderungen.Add($"bewilligt {Geld.Text(topf.Bewilligt)} → {Geld.Text(e.Sachkosten)}");
                }

                if (e.Innenauftrag is { } ia && topf.Innenauftrag != ia)
                {
                    aenderungen.Add($"Innenauftrag {topf.Innenauftrag ?? "–"} → {ia}");
                }

                if (topf.Zweck != e.Investition)
                {
                    aenderungen.Add(topf.Zweck is null ? "Verwendungszweck neu" : "Verwendungszweck geändert");
                }

                if (e.Begruendung is not null && topf.Begruendung != e.Begruendung)
                {
                    aenderungen.Add(topf.Begruendung is null ? "Begründung neu" : "Begründung geändert");
                }
            }

            liste.Add(new(e.Antragsnummer, e.Innenauftrag, e.Antragsteller, e.Investition, e.Sachkosten, e.Anschaffung, e.Laborbereich, topf?.Id, aenderungen));
        }

        return new(inhalt.Beschluss, liste, uebrige);
    }

    /// <param name="anlegen">Antragsnummern aus der Vorschau, die als neue Investitionstöpfe angelegt werden sollen.</param>
    public async Task<int> AntraegeUebernehmenAsync(Guid teamId, string json, IReadOnlyCollection<string> anlegen, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(anlegen);
        var vorschau = await AntraegeVorschauAsync(teamId, json, abbruch);
        var eintraege = InvestitionsantraegeJson.Lesen(json).Antraege.ToDictionary(e => e.Antragsnummer);
        var wer = (await TeamMitRechtAsync(teamId, abbruch)).Wer;
        var anzahl = 0;
        foreach (var v in vorschau.Antraege)
        {
            var e = eintraege[v.Antragsnummer];
            Topf? topf;
            if (v.TopfId is { } id)
            {
                topf = await daten.FindenAsync<Topf>(id, abbruch);
                if (topf is null || topf.Archiviert)
                {
                    continue;
                }
            }
            else if (anlegen.Contains(v.Antragsnummer, StringComparer.OrdinalIgnoreCase))
            {
                topf = Topf.Investition(teamId, e.Antragsnummer, Kurz(e.Investition), e.Sachkosten, e.Innenauftrag);
                daten.Hinzufuegen(topf);
            }
            else
            {
                continue;
            }

            // Eine vom Stand-Import vorläufig vergebene Bezeichnung („Investition AUT-…“) durch die Kurzbeschreibung ersetzen, eigene bleiben
            var bezeichnung = topf.Bezeichnung.StartsWith("Investition ", StringComparison.Ordinal) ? Kurz(e.Investition) : topf.Bezeichnung;
            topf.Aendern(topf.Angaben with
            {
                Bezeichnung = bezeichnung,
                Innenauftrag = e.Innenauftrag ?? topf.Innenauftrag,
                BewilligtPersonal = 0,
                BewilligtSach = e.Sachkosten,
                Verantwortlich = e.Antragsteller ?? topf.Verantwortlich,
                Zweck = e.Investition,
                Begruendung = e.Begruendung ?? topf.Begruendung,
                Anschaffung = e.Anschaffung ?? topf.Anschaffung,
                Laborbereich = e.Laborbereich ?? topf.Laborbereich,
                Beschluss = vorschau.Beschluss ?? topf.Beschluss,
            });
            anzahl++;
        }

        protokoll.Eintragen(wer, "Finanzen.Investitionsantraege", $"Beschlussliste der Investitionsanträge übernommen ({anzahl} Antrag/Anträge)", teamId);
        await daten.SpeichernAsync(abbruch);
        return anzahl;
    }

    /// <summary>Kurzbeschreibung als Bezeichnung: bis zum ersten Doppelpunkt bzw. höchstens 120 Zeichen.</summary>
    private static string Kurz(string investition)
    {
        var t = investition.Split(':', 2)[0].Trim();
        return t.Length <= 120 ? t : t[..117].TrimEnd() + " …";
    }

    // ------------------------------------------------------------------ intern

    private DateOnly StichtagPruefen(DateOnly? tag)
    {
        var heute = Heute;
        return tag is { } t && t <= heute ? t : heute;
    }

    /// <summary>Neuer Stand, wenn sich Stichtag oder Betrag gegenüber dem letzten unterscheidet.</summary>
    private async Task StandErgaenzenAsync(Guid topfId, DateOnly stichtag, decimal? verausgabt, Standquelle quelle, CancellationToken abbruch)
    {
        if (verausgabt is not { } betrag)
        {
            return;
        }

        var letzter = (await LetzteStaendeAsync([topfId], abbruch)).GetValueOrDefault(topfId);
        if (letzter is null || letzter.Stichtag != stichtag || letzter.Verausgabt != betrag)
        {
            daten.Hinzufuegen(new Massnahmenstand(topfId, stichtag, betrag, quelle, uhr.GetUtcNow()));
        }
    }

    private async Task<Dictionary<Guid, Massnahmenstand>> LetzteStaendeAsync(IEnumerable<Guid> topfIds, CancellationToken abbruch)
    {
        var ids = topfIds.ToList();
        return (await daten.ListeAsync(daten.Menge<Massnahmenstand>().Where(s => ids.Contains(s.TopfId)), abbruch))
            .GroupBy(s => s.TopfId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Stichtag).ThenByDescending(s => s.Erfasst).First());
    }

    private async Task<List<string>> NachnamenAsync(Laborteam team, CancellationToken abbruch)
    {
        var ids = team.Mitglieder.Select(m => m.KontoId).ToList();
        return [.. (await daten.ListeAsync(daten.Menge<Konto>().Where(k => ids.Contains(k.Id)), abbruch)).Select(k => k.Nachname).Where(n => n.Length >= 2)];
    }

    private static bool EnthaeltWort(string text, string wort)
        => text.Split([' ', '/', ',', ';', '-', '(', ')'], StringSplitOptions.RemoveEmptyEntries).Any(t => string.Equals(t, wort, StringComparison.CurrentCultureIgnoreCase));
}
