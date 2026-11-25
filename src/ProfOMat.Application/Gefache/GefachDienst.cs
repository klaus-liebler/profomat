using System.Text;
using System.Text.RegularExpressions;
using ProfOMat.Application.Betrieb;
using ProfOMat.Domain.Gefache;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Application.Gefache;

/// <summary>Eine herunterladbare Schnittvorlage.</summary>
public sealed record Gefachdatei(byte[] Inhalt, string Typ, string Dateiname);

/// <summary>Ein Gefach, wie die angemeldete Person es sieht: eigenes (änderbar) oder öffentliches einer anderen Person (nur ansehen).</summary>
/// <param name="Von">Anzeigename der Person, die es angelegt hat.</param>
public sealed record Gefachansicht(Gefach Gefach, bool Eigenes, string Von);

/// <summary>
/// Gefache (Teilungseinsätze) für Kisten, z. B. Euro-Lagerboxen, aus MDF lasern: Angaben speichern, Plan berechnen, Schnittvorlagen
/// (SVG/DXF je Platte, ZIP) liefern. Ändern, löschen und freigeben kann nur, wer ein Gefach angelegt hat; als „öffentlich sichtbar“
/// markierte Gefache können alle angemeldeten Personen ansehen, herunterladen und als Vorlage zu sich übernehmen.
/// </summary>
public sealed partial class GefachDienst(IDaten daten, IAktuellerBenutzer aktuell, Protokoll protokoll, TimeProvider uhr)
{
    public async Task<IReadOnlyList<Gefach>> ListeAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        return await daten.ListeAsync(daten.Menge<Gefach>().Where(g => g.KontoId == wer.KontoId).OrderBy(g => g.Bezeichnung), abbruch);
    }

    public async Task<Gefach> AnlegenAsync(string bezeichnung, Gefachangaben? angaben = null, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var g = new Gefach(wer.KontoIdPflicht, bezeichnung, uhr.GetUtcNow(), angaben);
        daten.Hinzufuegen(g);
        protokoll.Eintragen(wer, "Gefach.Angelegt", "Gefach für eine Kiste angelegt", g.Id);
        await daten.SpeichernAsync(abbruch);
        return g;
    }

    /// <summary>Öffentlich sichtbare Gefache anderer Personen, mit Namen.</summary>
    public async Task<IReadOnlyList<Gefachansicht>> OeffentlicheAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var liste = await daten.ListeAsync(daten.Menge<Gefach>().Where(g => g.Oeffentlich && g.KontoId != wer.KontoId), abbruch);
        var namen = await NamenAsync(liste.Select(g => g.KontoId), abbruch);
        return [.. liste.Select(g => new Gefachansicht(g, false, namen.GetValueOrDefault(g.KontoId, "unbekannt")))
            .OrderBy(a => a.Gefach.Bezeichnung, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Von, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Nur für die Person, die es angelegt hat (zum Ändern).</summary>
    public async Task<Gefach> LadenAsync(Guid id, CancellationToken abbruch = default) => (await LadenMitRechtAsync(id, abbruch)).Gefach;

    /// <summary>Eigenes oder öffentliches Gefach ansehen.</summary>
    public async Task<Gefachansicht> AnsehenAsync(Guid id, CancellationToken abbruch = default)
    {
        var (wer, g) = await LadenZumAnsehenAsync(id, abbruch);
        var eigenes = g.KontoId == wer.KontoId;
        var von = (await NamenAsync([g.KontoId], abbruch)).GetValueOrDefault(g.KontoId, "unbekannt");
        return new(g, eigenes, von);
    }

    public Task<Gefach> SichtbarkeitSetzenAsync(Guid id, bool oeffentlich, CancellationToken abbruch = default)
        => AendernAsync(id, g => g.SichtbarkeitSetzen(oeffentlich), abbruch);

    public Task<Gefach> EinstellenAsync(Guid id, Gefachangaben angaben, CancellationToken abbruch = default)
        => AendernAsync(id, g => g.Einstellen(angaben), abbruch);

    public Task<Gefach> UmbenennenAsync(Guid id, string bezeichnung, CancellationToken abbruch = default)
        => AendernAsync(id, g => g.Umbenennen(bezeichnung), abbruch);

    /// <summary>
    /// Legt eine eigene Kopie eines eigenen oder öffentlichen Gefachs an – mit dessen Angaben oder mit <paramref name="angaben"/>
    /// (in der Ansicht eines fremden Gefachs ausprobierte, nicht gespeicherte Änderungen). Die Kopie ist zunächst privat.
    /// </summary>
    public async Task<Gefach> UebernehmenAsync(Guid id, Gefachangaben? angaben = null, CancellationToken abbruch = default)
    {
        var (wer, g) = await LadenZumAnsehenAsync(id, abbruch);
        return await AnlegenAsync(g.KontoId == wer.KontoId ? $"{g.Bezeichnung} (Kopie)" : g.Bezeichnung, angaben ?? g.Angaben, abbruch);
    }

    public Task<Gefach> KopierenAsync(Guid id, CancellationToken abbruch = default) => UebernehmenAsync(id, null, abbruch);

    public async Task LoeschenAsync(Guid id, CancellationToken abbruch = default)
    {
        var (wer, g) = await LadenMitRechtAsync(id, abbruch);
        daten.Entfernen(g);
        protokoll.Eintragen(wer, "Gefach.Geloescht", "Gefach gelöscht", g.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>„platte-1.svg“, „platte-1.dxf“ oder „schnittvorlagen.zip“.</summary>
    public async Task<Gefachdatei> DateiAsync(Guid id, string datei, CancellationToken abbruch = default)
    {
        var (_, g) = await LadenZumAnsehenAsync(id, abbruch);
        var plan = Gefachplan.Berechnen(g.Angaben);
        plan.Freigabe.Sicherstellen();
        var basis = Dateiname(g.Bezeichnung);
        if (datei == "schnittvorlagen.zip")
        {
            return new(Schnittvorlage.Zip(plan, g.Bezeichnung), "application/zip", $"{basis}.zip");
        }

        var m = PlattenDatei().Match(datei);
        var platte = m.Success ? plan.Platten.FirstOrDefault(p => p.Nr == int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)) : null;
        if (platte is null)
        {
            throw new FachlicherFehler("Diese Schnittvorlage gibt es nicht (mehr) – die Seite neu laden.");
        }

        return m.Groups[2].Value == "svg"
            ? new(Encoding.UTF8.GetBytes(Schnittvorlage.Svg(plan, platte, g.Bezeichnung)), "image/svg+xml", $"{basis}-platte-{platte.Nr}.svg")
            : new(Encoding.ASCII.GetBytes(Schnittvorlage.Dxf(plan, platte)), "application/dxf", $"{basis}-platte-{platte.Nr}.dxf");
    }

    /// <summary>Dateiname aus der Bezeichnung (nur Buchstaben, Ziffern, Bindestrich).</summary>
    public static string Dateiname(string bezeichnung)
    {
        var s = Unerlaubt().Replace(bezeichnung.Replace('ä', 'a').Replace('ö', 'o').Replace('ü', 'u').Replace('Ä', 'A').Replace('Ö', 'O').Replace('Ü', 'U')
            .Replace("ß", "ss", StringComparison.Ordinal), "-").Trim('-');
        return s.Length == 0 ? "gefach" : s.Length > 60 ? s[..60] : s;
    }

    private async Task<Gefach> AendernAsync(Guid id, Action<Gefach> aenderung, CancellationToken abbruch)
    {
        var (_, g) = await LadenMitRechtAsync(id, abbruch);
        aenderung(g);
        await daten.SpeichernAsync(abbruch);
        return g;
    }

    private async Task<(Benutzer Wer, Gefach Gefach)> LadenMitRechtAsync(Guid id, CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var g = await daten.FindenAsync<Gefach>(id, abbruch) ?? throw new FachlicherFehler("Gefach nicht gefunden.");
        return g.KontoId == wer.KontoId ? (wer, g)
            : g.Oeffentlich ? throw new FachlicherFehler("Dieses Gefach ist öffentlich sichtbar, ändern kann es aber nur, wer es angelegt hat – als Vorlage übernehmen und dann ändern.")
            : throw new FachlicherFehler("Dieses Gefach sieht nur, wer es angelegt hat.");
    }

    private async Task<(Benutzer Wer, Gefach Gefach)> LadenZumAnsehenAsync(Guid id, CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var g = await daten.FindenAsync<Gefach>(id, abbruch) ?? throw new FachlicherFehler("Gefach nicht gefunden.");
        return g.KontoId == wer.KontoId || g.Oeffentlich ? (wer, g) : throw new FachlicherFehler("Dieses Gefach sieht nur, wer es angelegt hat.");
    }

    private async Task<Dictionary<Guid, string>> NamenAsync(IEnumerable<Guid> kontoIds, CancellationToken abbruch)
    {
        var ids = kontoIds.Distinct().ToList();
        var konten = await daten.ListeAsync(daten.Menge<ProfOMat.Domain.Konten.Konto>().Where(k => ids.Contains(k.Id)), abbruch);
        return konten.ToDictionary(k => k.Id, k => k.Anzeigename);
    }

    [GeneratedRegex("^platte-([0-9]{1,3})\\.(svg|dxf)$")]
    private static partial Regex PlattenDatei();

    [GeneratedRegex("[^A-Za-z0-9-]+")]
    private static partial Regex Unerlaubt();
}
