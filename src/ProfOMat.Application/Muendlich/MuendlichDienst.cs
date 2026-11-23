using ProfOMat.Application.Betrieb;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;
using ProfOMat.Domain.Muendlich;

namespace ProfOMat.Application.Muendlich;

/// <summary>
/// „Mündliche Prüfung live“: Protokoll der gestellten Fragen mit laufender Note. Ein Protokoll sieht und ändert nur, wer es angelegt hat.
/// Jede Änderung liefert das aktualisierte Protokoll zurück (die Seite rechnet damit sofort weiter).
/// </summary>
public sealed class MuendlichDienst(IDaten daten, IAktuellerBenutzer aktuell, Protokoll protokoll, TimeProvider uhr)
{
    public static Freigabe Protokollieren(Benutzer wer) => wer.Hat(Rolle.Pruefende)
        ? Freigabe.Ja
        : Freigabe.Nein("Mündliche Prüfungen protokollieren Prüfende.");

    public async Task<IReadOnlyList<MuendlichePruefung>> ListeAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        return await daten.ListeAsync(daten.Menge<MuendlichePruefung>().Where(p => p.KontoId == wer.KontoId).OrderByDescending(p => p.Id), abbruch);
    }

    public async Task<MuendlichePruefung> AnlegenAsync(string bezeichnung, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Und(() => Protokollieren(wer)).Sicherstellen();
        var p = new MuendlichePruefung(wer.KontoIdPflicht, bezeichnung, uhr.GetUtcNow());
        daten.Hinzufuegen(p);
        protokoll.Eintragen(wer, "Muendlich.Angelegt", "Live-Protokoll einer mündlichen Prüfung angelegt", p.Id);   // Bezeichnung kann Namen enthalten
        await daten.SpeichernAsync(abbruch);
        return p;
    }

    public async Task<MuendlichePruefung> LadenAsync(Guid id, CancellationToken abbruch = default) => (await LadenMitRechtAsync(id, abbruch)).Pruefung;

    public Task<MuendlichePruefung> UmbenennenAsync(Guid id, string bezeichnung, CancellationToken abbruch = default)
        => AendernAsync(id, p => p.Umbenennen(bezeichnung), abbruch);

    public async Task<(MuendlichePruefung Pruefung, Guid FrageId)> FrageHinzufuegenAsync(Guid id, string nummer, decimal maxPunkte, CancellationToken abbruch = default)
    {
        var (_, p) = await LadenMitRechtAsync(id, abbruch);
        var f = p.FrageHinzufuegen(nummer, maxPunkte);
        await daten.SpeichernAsync(abbruch);
        return (p, f.Id);
    }

    public Task<MuendlichePruefung> FrageAendernAsync(Guid id, Guid frageId, string nummer, decimal maxPunkte, CancellationToken abbruch = default)
        => AendernAsync(id, p => p.FrageAendern(frageId, nummer, maxPunkte), abbruch);

    public Task<MuendlichePruefung> FrageBewertenAsync(Guid id, Guid frageId, decimal? punkte, bool zurueckstellen, CancellationToken abbruch = default)
        => AendernAsync(id, p => p.FrageBewerten(frageId, punkte, zurueckstellen), abbruch);

    public Task<MuendlichePruefung> FrageEntfernenAsync(Guid id, Guid frageId, CancellationToken abbruch = default)
        => AendernAsync(id, p => p.FrageEntfernen(frageId), abbruch);

    public async Task LoeschenAsync(Guid id, CancellationToken abbruch = default)
    {
        var (wer, p) = await LadenMitRechtAsync(id, abbruch);
        daten.Entfernen(p);
        protokoll.Eintragen(wer, "Muendlich.Geloescht", $"Live-Protokoll einer mündlichen Prüfung gelöscht ({p.Fragen.Count} Fragen)", p.Id);
        await daten.SpeichernAsync(abbruch);
    }

    private async Task<MuendlichePruefung> AendernAsync(Guid id, Action<MuendlichePruefung> aenderung, CancellationToken abbruch)
    {
        var (_, p) = await LadenMitRechtAsync(id, abbruch);
        aenderung(p);
        await daten.SpeichernAsync(abbruch);
        return p;
    }

    private async Task<(Benutzer Wer, MuendlichePruefung Pruefung)> LadenMitRechtAsync(Guid id, CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var p = await daten.FindenAsync<MuendlichePruefung>(id, abbruch) ?? throw new FachlicherFehler("Protokoll nicht gefunden.");
        return p.KontoId == wer.KontoId ? (wer, p) : throw new FachlicherFehler("Dieses Protokoll sieht nur, wer es angelegt hat.");
    }
}
