using ProfOMat.Application.Betrieb;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;
using ProfOMat.Domain.Muendlich;

namespace ProfOMat.Application.Muendlich;

/// <summary>Erzeugt das Prüfungsprotokoll (PDF) mit Fragen, Punkten, Bemerkungen, Note und Unterschriftsfeldern.</summary>
public interface IMuendlichProtokollPdf
{
    byte[] Erzeugen(MuendlichProtokollAnsicht protokoll);
}

/// <param name="Erstpruefer">Anzeigename (mit Titel) der Erstprüferin/des Erstprüfers.</param>
/// <param name="Zweitpruefer"><c>null</c> = nicht gewählt (Feld bleibt zum Ausfüllen).</param>
/// <param name="Protokollfuehrung">Wer protokolliert hat, falls nicht eine/r der Prüfenden; sonst <c>null</c>.</param>
/// <param name="Begonnen">Beginn in Ortszeit, z. B. „03.12.2026 10:00“.</param>
public sealed record MuendlichProtokollAnsicht(MuendlichePruefung Pruefung, string Hochschule, string Erstpruefer, string? Zweitpruefer,
    string? Protokollfuehrung, string Begonnen, string Festgelegt);

/// <summary>Prüfling und Prüfende, schon während der Prüfung gespeichert (leer = noch offen).</summary>
/// <param name="ErstpruefendeName">Erstprüfer/in ohne Konto (noch nicht angemeldet); zählt nur ohne <paramref name="Erstpruefende"/>.</param>
/// <param name="ZweitpruefendeName">Zweitprüfer/in ohne Konto; zählt nur ohne <paramref name="Zweitpruefende"/>.</param>
public sealed record PruefungsAngaben(string? Pruefling, string? Matrikelnummer, string? Studiengang, Guid? Erstpruefende, Guid? Zweitpruefende,
    string? ErstpruefendeName = null, string? ZweitpruefendeName = null);

/// <summary>Angaben beim Erstellen des Protokolls (Prüfende ohne Konto wie bei <see cref="PruefungsAngaben"/> als Name).</summary>
public sealed record ProtokollAngaben(string? Pruefling, string? Matrikelnummer, string? Studiengang, Guid? Erstpruefende, Guid? Zweitpruefende,
    decimal Note, string? Begruendung, string? ErstpruefendeName = null, string? ZweitpruefendeName = null);

/// <summary>Auswahl für Erst- und Zweitprüfung.</summary>
public sealed record PruefendeWahl(Guid KontoId, string Name, string Sortiername);

/// <summary>
/// „Mündliche Prüfung live“: Protokoll der gestellten Fragen mit laufender Note. Ein Protokoll sieht und ändert nur, wer es angelegt hat.
/// Jede Änderung liefert das aktualisierte Protokoll zurück (die Seite rechnet damit sofort weiter).
/// </summary>
public sealed class MuendlichDienst(IDaten daten, IAktuellerBenutzer aktuell, Protokoll protokoll, TimeProvider uhr,
    IMuendlichProtokollPdf pdf, Ortszeit ortszeit, Microsoft.Extensions.Options.IOptions<ProfOMatOptionen> optionen)
{
    private static readonly System.Globalization.CultureInfo Deutsch = System.Globalization.CultureInfo.GetCultureInfo("de-DE");

    /// <summary>„Elektrotechnik (B.Sc., PO 2023)“.</summary>
    public static string StudiengangText(ProfOMat.Domain.Katalog.Studiengang s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return $"{s.Name} ({s.Abschluss}, {s.Pruefungsordnung})";
    }

    public static Freigabe Protokollieren(Benutzer wer) => wer.Hat(Rolle.Pruefende)
        ? Freigabe.Ja
        : Freigabe.Nein("Mündliche Prüfungen protokollieren Prüfende.");

    public static Freigabe ProtokollErstellen(MuendlichePruefung p) => p.Fragen.Count > 0
        ? Freigabe.Ja
        : Freigabe.Nein("Noch keine Fragen protokolliert.");

    /// <summary>Aktive Konten mit der Rolle Prüfende (Auswahl für Erst- und Zweitprüfung).</summary>
    public async Task<IReadOnlyList<PruefendeWahl>> PruefendeAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var konten = await daten.ListeAsync(daten.Menge<Konto>().Where(k => k.Aktiv), abbruch);
        return konten.Where(k => k.Hat(Rolle.Pruefende)).Select(k => new PruefendeWahl(k.Id, k.Anzeigename, k.Sortiername))
            .OrderBy(k => k.Sortiername, StringComparer.Create(Deutsch, true)).ToList();
    }

    /// <summary>Studiengänge aus dem Katalog als Vorschläge; frei eingeben geht immer.</summary>
    public async Task<IReadOnlyList<string>> StudiengaengeAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var liste = await daten.ListeAsync(daten.Menge<ProfOMat.Domain.Katalog.Studiengang>(), abbruch);
        return liste.Select(StudiengangText).Distinct().Order(StringComparer.Create(Deutsch, true)).ToList();
    }

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

    public Task<MuendlichePruefung> FrageBemerkenAsync(Guid id, Guid frageId, string? bemerkung, CancellationToken abbruch = default)
        => AendernAsync(id, p => p.FrageBemerken(frageId, bemerkung), abbruch);

    /// <summary>
    /// Prüfling und Prüfende schon während der Prüfung speichern (Formular oben auf der Seite); darf unvollständig sein.
    /// Die Personendaten kommen nicht ins Protokoll der Anwendung.
    /// </summary>
    public async Task<MuendlichePruefung> AngabenSpeichernAsync(Guid id, PruefungsAngaben angaben, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(angaben);
        var (_, p) = await LadenMitRechtAsync(id, abbruch);
        await PruefendePruefenAsync(angaben.Erstpruefende, angaben.Zweitpruefende, abbruch);
        p.AngabenSetzen(angaben.Pruefling, angaben.Matrikelnummer, angaben.Studiengang, angaben.Erstpruefende, angaben.Zweitpruefende,
            angaben.ErstpruefendeName, angaben.ZweitpruefendeName);
        await daten.SpeichernAsync(abbruch);
        return p;
    }

    /// <summary>Angaben übernehmen und die Note festlegen (Vorschlag oder angepasst); danach ist das PDF abrufbar.</summary>
    public async Task<MuendlichePruefung> ProtokollErstellenAsync(Guid id, ProtokollAngaben angaben, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(angaben);
        var (wer, p) = await LadenMitRechtAsync(id, abbruch);
        ProtokollErstellen(p).Sicherstellen();
        await PruefendePruefenAsync(angaben.Erstpruefende, angaben.Zweitpruefende, abbruch);

        p.AngabenSetzen(angaben.Pruefling, angaben.Matrikelnummer, angaben.Studiengang, angaben.Erstpruefende, angaben.Zweitpruefende,
            angaben.ErstpruefendeName, angaben.ZweitpruefendeName);
        p.NoteFestlegen(angaben.Note, angaben.Begruendung, uhr.GetUtcNow());
        protokoll.Eintragen(wer, "Muendlich.Note", p.WeichtAb ? "Note festgelegt (abweichend vom Vorschlag)" : "Note festgelegt (Vorschlag übernommen)", p.Id);
        await daten.SpeichernAsync(abbruch);
        return p;
    }

    /// <summary>Protokoll als PDF; erst, wenn die Note festgelegt ist.</summary>
    public async Task<(byte[] Inhalt, string Dateiname)> ProtokollPdfAsync(Guid id, CancellationToken abbruch = default)
    {
        var (wer, p) = await LadenMitRechtAsync(id, abbruch);
        if (p.Endnote is null || p.Festgelegt is not { } festgelegt)
        {
            throw new FachlicherFehler("Bitte zuerst die Note festlegen (Protokoll erstellen).");
        }

        async Task<string?> Name(Guid? konto) => konto is { } k ? (await daten.FindenAsync<Konto>(k, abbruch))?.Anzeigename : null;
        var erst = await Name(p.ErstpruefendeId) ?? p.ErstpruefendeName ?? "";
        var zweit = await Name(p.ZweitpruefendeId) ?? p.ZweitpruefendeName;
        var fuehrung = p.KontoId == p.ErstpruefendeId || p.KontoId == p.ZweitpruefendeId ? null : await Name(p.KontoId) ?? wer.Name;
        var inhalt = pdf.Erzeugen(new MuendlichProtokollAnsicht(p, optionen.Value.Hochschule, erst, zweit, fuehrung,
            ortszeit.Kurz(p.Begonnen), ortszeit.Kurz(festgelegt)));
        return (inhalt, $"Protokoll-muendliche-Pruefung-{ortszeit.Lokal(p.Begonnen):yyyy-MM-dd-HHmm}.pdf");
    }

    public Task<MuendlichePruefung> FrageEntfernenAsync(Guid id, Guid frageId, CancellationToken abbruch = default)
        => AendernAsync(id, p => p.FrageEntfernen(frageId), abbruch);

    public async Task LoeschenAsync(Guid id, CancellationToken abbruch = default)
    {
        var (wer, p) = await LadenMitRechtAsync(id, abbruch);
        daten.Entfernen(p);
        protokoll.Eintragen(wer, "Muendlich.Geloescht", $"Live-Protokoll einer mündlichen Prüfung gelöscht ({p.Fragen.Count} Fragen)", p.Id);
        await daten.SpeichernAsync(abbruch);
    }

    private async Task PruefendePruefenAsync(Guid? erst, Guid? zweit, CancellationToken abbruch)
    {
        foreach (var konto in new[] { erst, zweit }.OfType<Guid>())
        {
            if (await daten.FindenAsync<Konto>(konto, abbruch) is not { Aktiv: true } k || !k.Hat(Rolle.Pruefende))
            {
                throw new FachlicherFehler("Erst- und Zweitprüfung nur durch aktive Konten mit der Rolle Prüfende.");
            }
        }
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
