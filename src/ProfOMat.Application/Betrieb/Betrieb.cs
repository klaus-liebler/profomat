using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Betrieb;

/// <summary>Protokoll: jede fachliche Aktion, gespeichert in derselben Arbeitseinheit wie die Änderung.</summary>
public sealed class Protokoll(IDaten daten, TimeProvider uhr)
{
    public void Eintragen(Benutzer wer, string aktion, string text, Guid? bezug = null)
    {
        ArgumentNullException.ThrowIfNull(wer);
        daten.Hinzufuegen(new Protokolleintrag(uhr.GetUtcNow(), wer.Angemeldet ? wer.Name : wer.Kennung, aktion, text, bezug));
    }

    public async Task<IReadOnlyList<Protokolleintrag>> LetzteAsync(Benutzer wer, int anzahl, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(wer);
        Rechte.Verwaltung(wer).Sicherstellen();
        return await daten.ListeAsync(daten.Menge<Protokolleintrag>().OrderByDescending(p => p.Zeitpunkt).ThenByDescending(p => p.Id).Take(anzahl), abbruch);
    }
}

/// <summary>Postausgang: Mails werden eingereiht und von einem Hintergrundjob zugestellt (mit Wiederholung).</summary>
public sealed class Postausgang(IDaten daten, TimeProvider uhr)
{
    public Nachricht Einreihen(string an, string betreff, string text, Guid? antragId = null, Guid? anhangDokumentId = null)
    {
        var nachricht = new Nachricht(an, betreff, text, uhr.GetUtcNow(), antragId, anhangDokumentId);
        daten.Hinzufuegen(nachricht);
        return nachricht;
    }

    public async Task<IReadOnlyList<Nachricht>> LetzteAsync(Benutzer wer, int anzahl, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(wer);
        Rechte.Verwaltung(wer).Sicherstellen();
        return await daten.ListeAsync(daten.Menge<Nachricht>().OrderByDescending(n => n.Erstellt).ThenByDescending(n => n.Id).Take(anzahl), abbruch);
    }

    /// <summary>Stellt fällige Nachrichten zu (Hintergrundjob).</summary>
    public async Task<int> ZustellenAsync(IMailZustellung zustellung, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(zustellung);
        var jetzt = uhr.GetUtcNow();
        var faellig = (await daten.ListeAsync(daten.Menge<Nachricht>().Where(n => n.Versandt == null && n.Fehlversuche < 8)
                .OrderBy(n => n.Erstellt).ThenBy(n => n.Id).Take(50), abbruch))
            .Where(n => n.NaechsterVersuch is null || n.NaechsterVersuch <= jetzt)
            .ToList();
        foreach (var n in faellig)
        {
            try
            {
                (string, string, byte[])? anhang = null;
                if (n.AnhangDokumentId is { } dokId && await daten.FindenAsync<Dokument>(dokId, abbruch) is { } dok)
                {
                    anhang = (dok.Dateiname, dok.Inhaltstyp, dok.Inhalt);
                }

                var echt = await zustellung.ZustellenAsync(n.An, n.Betreff, n.Text, anhang, abbruch);
                n.AlsVersandtMarkieren(uhr.GetUtcNow(), simuliert: !echt);
            }
#pragma warning disable CA1031 // Jeder Zustellfehler wird gemerkt und später wiederholt.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                n.FehlschlagMerken(uhr.GetUtcNow(), ex.Message);
            }
        }

        await daten.SpeichernAsync(abbruch);
        return faellig.Count;
    }
}

/// <summary>Rechte – immer im Dienst geprüft, die Oberfläche fragt dieselben Regeln ab.</summary>
public static class Rechte
{
    public static Domain.Gemeinsam.Freigabe Angemeldet(Benutzer wer) => wer.Angemeldet
        ? Domain.Gemeinsam.Freigabe.Ja
        : Domain.Gemeinsam.Freigabe.Nein("Bitte zuerst anmelden.");

    public static Domain.Gemeinsam.Freigabe Verwaltung(Benutzer wer) => wer.Hat(Rolle.Systemverwaltung)
        ? Domain.Gemeinsam.Freigabe.Ja
        : Domain.Gemeinsam.Freigabe.Nein("Nur für die Systemverwaltung.");

    public static Domain.Gemeinsam.Freigabe KatalogPflegen(Benutzer wer) => wer.Hat(Rolle.Systemverwaltung) || wer.Hat(Rolle.Studiendekanat)
        ? Domain.Gemeinsam.Freigabe.Ja
        : Domain.Gemeinsam.Freigabe.Nein("Den Katalog pflegen Systemverwaltung und Studiendekanat.");

    /// <summary>Revision: Konfiguration, Rollen und Rechte, Sicherheitsprotokoll und Sicherheitsbericht – nur lesend, keine Inhalte.</summary>
    public static Domain.Gemeinsam.Freigabe Revision(Benutzer wer) => wer.Hat(Rolle.Revision) || wer.Hat(Rolle.Systemverwaltung)
        ? Domain.Gemeinsam.Freigabe.Ja
        : Domain.Gemeinsam.Freigabe.Nein("Nur für Revision und Systemverwaltung.");

    /// <summary>Alle Anträge sehen (nicht nur die eigenen).</summary>
    public static bool AlleAntraegeSehen(Benutzer wer)
        => wer.Hat(Rolle.Systemverwaltung) || wer.Hat(Rolle.Studiendekanat) || wer.Hat(Rolle.Studierendensekretariat);
}
