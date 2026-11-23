using ProfOMat.Application.Betrieb;
using ProfOMat.Domain.Beschriftung;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Application.Beschriftung;

/// <summary>Setzt Beschriftungsstreifen als DIN-A4-PDF (quer) mit Schnittmarken.</summary>
public interface IStreifenPdf
{
    byte[] Erzeugen(string titel, IReadOnlyList<(string Beschriftung, Streifen Streifen)> streifen);
}

/// <summary>Angaben zur Beschriftung eines Geräts.</summary>
public sealed record GeraetAngaben(Geraetetyp Typ, int Breite, string? Kennzeichen, string? Text, string? Zusatz, Symbol Symbol, string Hintergrund, string Schrift);

/// <summary>Eine Reihe eines Feldes (für die PDF-Auswahl).</summary>
public sealed record ReiheWahl(Guid FeldId, int Reihe);

/// <summary>
/// Beschriftungsgenerator für Verteiler: Schrank nachbauen (Felder, Reihen), Geräte einsetzen und beschriften, Streifen als PDF.
/// Private Hilfe für jedes Konto; einen Schrank sieht und ändert nur, wer ihn angelegt hat.
/// </summary>
public sealed class BeschriftungDienst(IDaten daten, IAktuellerBenutzer aktuell, Protokoll protokoll, TimeProvider uhr, Streifenlayout layout, IStreifenPdf pdf)
{
    /// <summary>Druckbreite A4 quer abzüglich Ränder und Schnittmarken.</summary>
    public const decimal MaxStreifenbreiteMm = 265m;

    public static Freigabe Drucken(Schaltschrank s, IReadOnlyCollection<ReiheWahl> auswahl)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(auswahl);
        return s.ReiheBreiteMm > MaxStreifenbreiteMm
            ? Freigabe.Nein($"Eine Reihe ist {s.ReiheBreiteMm:0.#} mm breit – auf A4 quer passen höchstens {MaxStreifenbreiteMm:0} mm.")
            : auswahl.Count == 0 ? Freigabe.Nein("Bitte mindestens eine Reihe wählen.") : Freigabe.Ja;
    }

    public async Task<IReadOnlyList<Schaltschrank>> ListeAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        return await daten.ListeAsync(daten.Menge<Schaltschrank>().Where(s => s.KontoId == wer.KontoId).OrderBy(s => s.Bezeichnung), abbruch);
    }

    public async Task<Schaltschrank> AnlegenAsync(string bezeichnung, int teProReihe = 12, decimal teBreiteMm = 18m, decimal hoeheMm = 10m,
        CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var s = new Schaltschrank(wer.KontoIdPflicht, bezeichnung, uhr.GetUtcNow(), teProReihe, teBreiteMm, hoeheMm);
        daten.Hinzufuegen(s);
        protokoll.Eintragen(wer, "Beschriftung.Angelegt", "Schaltschrank zum Beschriften angelegt", s.Id);
        await daten.SpeichernAsync(abbruch);
        return s;
    }

    public async Task<Schaltschrank> LadenAsync(Guid id, CancellationToken abbruch = default) => (await LadenMitRechtAsync(id, abbruch)).Schrank;

    public Task<Schaltschrank> EinstellenAsync(Guid id, string bezeichnung, int teProReihe, decimal teBreiteMm, decimal hoeheMm, CancellationToken abbruch = default)
        => AendernAsync(id, s => s.Einstellen(bezeichnung, teProReihe, teBreiteMm, hoeheMm), abbruch);

    public Task<Schaltschrank> FeldHinzufuegenAsync(Guid id, int reihen, CancellationToken abbruch = default)
        => AendernAsync(id, s => s.FeldHinzufuegen(reihen), abbruch);

    public Task<Schaltschrank> FeldAendernAsync(Guid id, Guid feldId, string? bezeichnung, int reihen, CancellationToken abbruch = default)
        => AendernAsync(id, s => s.FeldAendern(feldId, bezeichnung, reihen), abbruch);

    public Task<Schaltschrank> FeldEntfernenAsync(Guid id, Guid feldId, CancellationToken abbruch = default)
        => AendernAsync(id, s => s.FeldEntfernen(feldId), abbruch);

    public async Task<(Schaltschrank Schrank, Guid GeraetId)> GeraetEinsetzenAsync(Guid id, Guid feldId, int reihe, int start, Geraetetyp typ,
        CancellationToken abbruch = default)
    {
        Guid neu = default;
        var s = await AendernAsync(id, x => neu = x.GeraetEinsetzen(feldId, reihe, start, typ).Id, abbruch);
        return (s, neu);
    }

    public Task<Schaltschrank> GeraetBeschriftenAsync(Guid id, Guid geraetId, GeraetAngaben a, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        return AendernAsync(id, s => s.GeraetBeschriften(geraetId, a.Typ, a.Breite, a.Kennzeichen, a.Text, a.Zusatz, a.Symbol, a.Hintergrund, a.Schrift), abbruch);
    }

    public Task<Schaltschrank> GeraetVerschiebenAsync(Guid id, Guid geraetId, Guid feldId, int reihe, int start, CancellationToken abbruch = default)
        => AendernAsync(id, s => s.GeraetVerschieben(geraetId, feldId, reihe, start), abbruch);

    public async Task<(Schaltschrank Schrank, Guid GeraetId)> GeraetKopierenAsync(Guid id, Guid geraetId, CancellationToken abbruch = default)
    {
        Guid neu = default;
        var s = await AendernAsync(id, x => neu = x.GeraetKopieren(geraetId).Id, abbruch);
        return (s, neu);
    }

    public Task<Schaltschrank> GeraetEntfernenAsync(Guid id, Guid geraetId, CancellationToken abbruch = default)
        => AendernAsync(id, s => s.GeraetEntfernen(geraetId), abbruch);

    public async Task LoeschenAsync(Guid id, CancellationToken abbruch = default)
    {
        var (wer, s) = await LadenMitRechtAsync(id, abbruch);
        daten.Entfernen(s);
        protokoll.Eintragen(wer, "Beschriftung.Geloescht", "Schaltschrank zum Beschriften gelöscht", s.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>PDF mit den gewählten Reihen (Reihenfolge: Feld für Feld, Reihe für Reihe).</summary>
    public async Task<(byte[] Inhalt, string Dateiname)> PdfAsync(Guid id, IReadOnlyCollection<ReiheWahl> auswahl, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(auswahl);
        var (_, s) = await LadenMitRechtAsync(id, abbruch);
        Drucken(s, auswahl).Sicherstellen();
        var streifen = new List<(string, Streifen)>();
        foreach (var feld in s.FelderSortiert)
        {
            for (var r = 1; r <= feld.Reihen; r++)
            {
                if (auswahl.Contains(new ReiheWahl(feld.Id, r)))
                {
                    streifen.Add(($"{s.Bezeichnung} · {FeldName(s, feld)} · Reihe {r}", layout.Reihe(s, feld, r)));
                }
            }
        }

        if (streifen.Count == 0)
        {
            throw new FachlicherFehler("Die gewählten Reihen gibt es nicht (mehr).");
        }

        return (pdf.Erzeugen($"Beschriftung {s.Bezeichnung}", streifen), "Beschriftung.pdf");
    }

    public static string FeldName(Schaltschrank s, Feld f)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(f);
        var nr = s.FelderSortiert.ToList().IndexOf(f) + 1;
        return f.Bezeichnung is { } b ? $"Feld {nr} ({b})" : $"Feld {nr}";
    }

    private async Task<Schaltschrank> AendernAsync(Guid id, Action<Schaltschrank> aenderung, CancellationToken abbruch)
    {
        var (_, s) = await LadenMitRechtAsync(id, abbruch);
        aenderung(s);
        await daten.SpeichernAsync(abbruch);
        return s;
    }

    private async Task<(Benutzer Wer, Schaltschrank Schrank)> LadenMitRechtAsync(Guid id, CancellationToken abbruch)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var s = await daten.FindenAsync<Schaltschrank>(id, abbruch) ?? throw new FachlicherFehler("Schaltschrank nicht gefunden.");
        return s.KontoId == wer.KontoId ? (wer, s) : throw new FachlicherFehler("Diesen Schaltschrank sieht nur, wer ihn angelegt hat.");
    }
}
