using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Betrieb;

/// <summary>Eine Zeile der Rollen-Rechte-Matrix: welcher Bereich für welche Rollen offen ist (aus den Regeln in <see cref="Rechte"/>).</summary>
public sealed record Rechtezeile(string Bereich, IReadOnlyList<Rolle> Rollen, string Hinweis);

/// <summary>
/// Zugang für Prüfende (Rolle „Revision“, Zielbild Abschnitt 6): Sicherheitsprotokoll mit Kettenprüfung und Rollen-Rechte-Matrix –
/// nur lesend, keine fachlichen Inhalte. Die Systemverwaltung sieht dasselbe.
/// </summary>
public sealed class RevisionDienst(IAktuellerBenutzer aktuell, ISicherheitsprotokoll protokoll)
{
    /// <summary>Rollen-Rechte-Matrix. Bei neuen Rechten in <see cref="Rechte"/> bzw. in den Diensten hier nachziehen.</summary>
    public static readonly IReadOnlyList<Rechtezeile> Matrix =
    [
        new("Eigene Anträge, Klausuren, mündliche Prüfungen, Hausarbeiten", [Rolle.Pruefende], "nur selbst angelegte bzw. zugewiesene"),
        new("Alle Anträge der Drittprüfung sehen", [Rolle.Systemverwaltung, Rolle.Studiendekanat, Rolle.Studierendensekretariat], "Rechte.AlleAntraegeSehen"),
        new("Anträge als Studiendekanat unterschreiben", [Rolle.Studiendekanat], "zugeordnete Fakultät bzw. Vertretung"),
        new("Katalog (Studiengänge, Prüfungen) pflegen", [Rolle.Systemverwaltung, Rolle.Studiendekanat], "Rechte.KatalogPflegen"),
        new("Konten, Einladungen, Passwort-Links, zweiten Faktor zurücksetzen", [Rolle.Systemverwaltung], "Rechte.Verwaltung"),
        new("Postausgang und fachliches Protokoll", [Rolle.Systemverwaltung], "Rechte.Verwaltung"),
        new("Sicherheitsprotokoll, Rechte-Matrix, Sicherheitsbericht", [Rolle.Revision, Rolle.Systemverwaltung], "Rechte.Revision – nur lesend, keine Inhalte"),
        new("Zweiter Faktor (TOTP) Pflicht", [.. ProfOMat.Application.Konten.Anmelderegeln.RollenMitPflichtZumZweitenFaktor], "Anmelderegeln"),
    ];

    public async Task<IReadOnlyList<Sicherheitsereignis>> ProtokollAsync(int anzahl = 200, CancellationToken abbruch = default)
    {
        Rechte.Revision(await aktuell.ErmittelnAsync(abbruch)).Sicherstellen();
        return await protokoll.LetzteAsync(Math.Clamp(anzahl, 1, 5000), abbruch);
    }

    public async Task<Kettenpruefung> PruefenAsync(CancellationToken abbruch = default)
    {
        Rechte.Revision(await aktuell.ErmittelnAsync(abbruch)).Sicherstellen();
        return await protokoll.PruefenAsync(abbruch);
    }

    /// <summary>Für Bericht und Exporte: Recht prüfen und den Abruf protokollieren.</summary>
    public async Task<Benutzer> SicherstellenAsync(string export, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Revision(wer).Sicherstellen();
        await protokoll.EintragenAsync("Export", wer.KontoId, export, abbruch);
        return wer;
    }
}
