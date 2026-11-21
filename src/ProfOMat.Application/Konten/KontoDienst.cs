using Microsoft.AspNetCore.Identity;
using ProfOMat.Application.Betrieb;
using ProfOMat.Application.Signaturen;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Konten;

/// <summary>Lokale Konten: Anmeldung mit Passwort, Rollen, Signaturschlüssel. Später zusätzlich Anmeldung über das Hochschulsystem.</summary>
public sealed class KontoDienst(IDaten daten, IAktuellerBenutzer aktuell, Protokoll protokoll, TimeProvider uhr)
{
    public const int MinPasswortLaenge = 12;

    private static readonly PasswordHasher<Konto> Hasher = new();

    public static Benutzer AlsBenutzer(Konto konto)
    {
        ArgumentNullException.ThrowIfNull(konto);
        return new Benutzer(konto.Kennung, konto.Anzeigename, konto.Id, konto.Rollen.Select(r => r.Rolle).Order().ToList());
    }

    /// <summary>Prüft Kennung und Passwort; <c>null</c> bei Fehlschlag (ohne Hinweis, was falsch war).</summary>
    public async Task<Benutzer?> PasswortAnmeldenAsync(string kennung, string passwort, CancellationToken abbruch = default)
    {
        if (string.IsNullOrWhiteSpace(kennung) || string.IsNullOrEmpty(passwort))
        {
            return null;
        }

        var k = kennung.Trim().ToLowerInvariant();
        var konto = await daten.ErsterAsync(daten.Menge<Konto>().Where(x => x.Kennung == k), abbruch);
        if (konto is not { Aktiv: true, PasswortHash: { } hash })
        {
            return null;
        }

        var ergebnis = Hasher.VerifyHashedPassword(konto, hash, passwort);
        if (ergebnis == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (ergebnis == PasswordVerificationResult.SuccessRehashNeeded)
        {
            konto.PasswortSetzen(Hasher.HashPassword(konto, passwort));
        }

        var benutzer = AlsBenutzer(konto);
        protokoll.Eintragen(benutzer, "Anmeldung", "Anmeldung mit Passwort");
        await daten.SpeichernAsync(abbruch);
        return benutzer;
    }

    /// <summary>Testanmeldung (nur Entwicklung): Konto ohne Passwort wählen.</summary>
    public async Task<Benutzer?> TestanmeldungAsync(Guid kontoId, CancellationToken abbruch = default)
        => await daten.FindenAsync<Konto>(kontoId, abbruch) is { Aktiv: true } konto ? AlsBenutzer(konto) : null;

    /// <summary>Aktuelle Rechte eines angemeldeten Kontos (Rollen können sich seit der Anmeldung geändert haben).</summary>
    public async Task<Benutzer?> AktualisierenAsync(Guid kontoId, CancellationToken abbruch = default)
        => await daten.FindenAsync<Konto>(kontoId, abbruch) is { Aktiv: true } konto ? AlsBenutzer(konto) : null;

    public async Task<IReadOnlyList<Konto>> AlleAsync(CancellationToken abbruch = default)
        => await daten.ListeAsync(daten.Menge<Konto>().OrderBy(k => k.Nachname).ThenBy(k => k.Vorname).ThenBy(k => k.Id), abbruch);

    /// <summary>Aktive Prüfende für Auswahlen.</summary>
    public async Task<IReadOnlyList<Konto>> PruefendeAsync(CancellationToken abbruch = default)
        => (await AlleAsync(abbruch)).Where(k => k.Aktiv && k.Hat(Rolle.Pruefende)).ToList();

    public async Task<Konto> AnlegenAsync(string kennung, string? titel, string vorname, string nachname, string email, IEnumerable<Rolle> rollen,
        string? passwort, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Verwaltung(wer).Sicherstellen();
        var k = kennung?.Trim().ToLowerInvariant() ?? string.Empty;
        if (k.Length < 3)
        {
            throw new FachlicherFehler("Die Kennung braucht mindestens drei Zeichen.");
        }

        if (await daten.AnzahlAsync(daten.Menge<Konto>().Where(x => x.Kennung == k), abbruch) > 0)
        {
            throw new FachlicherFehler("Diese Kennung ist schon vergeben.");
        }

        var konto = new Konto(k, titel, vorname, nachname, email);
        foreach (var r in rollen ?? [])
        {
            konto.RolleGeben(r);
        }

        if (!string.IsNullOrEmpty(passwort))
        {
            konto.PasswortSetzen(Hash(konto, passwort));
        }

        daten.Hinzufuegen(konto);
        protokoll.Eintragen(wer, "Konto.Angelegt", $"Konto {k} angelegt ({string.Join(", ", konto.Rollen.Select(r => r.Rolle.Anzeigename()))})", konto.Id);
        await daten.SpeichernAsync(abbruch);
        return konto;
    }

    public async Task AendernAsync(Guid kontoId, string? titel, string vorname, string nachname, string email, IReadOnlyCollection<Rolle> rollen, bool aktiv,
        CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(rollen);
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Verwaltung(wer).Sicherstellen();
        var konto = await daten.FindenAsync<Konto>(kontoId, abbruch) ?? throw new FachlicherFehler("Konto nicht gefunden.");
        if (konto.Id == wer.KontoId && (!aktiv || !rollen.Contains(Rolle.Systemverwaltung)))
        {
            throw new FachlicherFehler("Das eigene Konto lässt sich nicht sperren und verliert nicht selbst die Systemverwaltung.");
        }

        konto.Aendern(titel, vorname, nachname, email);
        foreach (var r in Enum.GetValues<Rolle>())
        {
            if (rollen.Contains(r))
            {
                konto.RolleGeben(r);
            }
            else
            {
                konto.RolleNehmen(r);
            }
        }

        if (aktiv)
        {
            konto.Entsperren();
        }
        else
        {
            konto.Sperren();
        }

        protokoll.Eintragen(wer, "Konto.Geaendert", $"Konto {konto.Kennung} geändert ({string.Join(", ", rollen.Select(r => r.Anzeigename()))}{(aktiv ? "" : ", gesperrt")})", konto.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Passwort setzen: für sich selbst (mit altem Passwort) oder als Systemverwaltung für andere.</summary>
    public async Task PasswortSetzenAsync(Guid kontoId, string? altesPasswort, string neuesPasswort, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Angemeldet(wer).Sicherstellen();
        var konto = await daten.FindenAsync<Konto>(kontoId, abbruch) ?? throw new FachlicherFehler("Konto nicht gefunden.");
        var selbst = konto.Id == wer.KontoId;
        if (!selbst)
        {
            Rechte.Verwaltung(wer).Sicherstellen();
        }
        else if (konto.PasswortHash is { } alt && Hasher.VerifyHashedPassword(konto, alt, altesPasswort ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            throw new FachlicherFehler("Das bisherige Passwort stimmt nicht.");
        }

        konto.PasswortSetzen(Hash(konto, neuesPasswort));
        protokoll.Eintragen(wer, "Konto.Passwort", selbst ? "Eigenes Passwort geändert" : $"Passwort für {konto.Kennung} gesetzt", konto.Id);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Text, den der Browser zum Nachweis des Schlüsselbesitzes signiert.</summary>
    public static string Besitznachweis(Guid kontoId, DateTimeOffset zeitpunkt)
        => $"profomat-schluessel-hinterlegen\n{kontoId:D}\n{Signaturinhalt.Kappen(zeitpunkt):yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\n";

    /// <summary>Öffentlichen Schlüssel hinterlegen; der Browser hat <see cref="Besitznachweis"/> mit dem privaten Schlüssel signiert.</summary>
    public async Task<HinterlegterSchluessel> SchluesselHinterlegenAsync(DateTimeOffset nachweisZeitpunkt, Unterschrift nachweis, string bezeichnung,
        CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(nachweis);
        var wer = await aktuell.ErmittelnAsync(abbruch);
        var konto = await daten.FindenAsync<Konto>(wer.KontoIdPflicht, abbruch) ?? throw new FachlicherFehler("Konto nicht gefunden.");
        var jetzt = uhr.GetUtcNow();
        if (nachweisZeitpunkt > jetzt.AddMinutes(1) || nachweisZeitpunkt < jetzt.AddMinutes(-30))
        {
            throw new FachlicherFehler("Der Nachweis ist abgelaufen. Bitte die Seite neu laden.");
        }

        if (nachweis is not { Art: Domain.Drittpruefung.Signaturart.Schluessel, Algorithmus: { } alg, SignaturWertBase64: { } sig, OeffentlicherSchluesselJwk: { } jwk })
        {
            throw new FachlicherFehler("Bitte den privaten Schlüssel in das Feld ziehen.");
        }

        SignaturPruefung.OeffentlicherSchluessel schluessel;
        try
        {
            schluessel = SignaturPruefung.SchluesselSignaturPruefen(Besitznachweis(konto.Id, nachweisZeitpunkt), alg, sig, jwk);
        }
        catch (SignaturFehler fehler)
        {
            throw new FachlicherFehler(fehler.Message, fehler);
        }

        var neu = konto.SchluesselHinterlegen(schluessel.Fingerabdruck, schluessel.Kanonisch, alg, bezeichnung, jetzt);
        protokoll.Eintragen(wer, "Konto.Schluessel", $"Signaturschlüssel {SignaturPruefung.OeffentlicherSchluessel.Kurz(schluessel.Fingerabdruck)} hinterlegt", konto.Id);
        await daten.SpeichernAsync(abbruch);
        return neu;
    }

    public async Task SchluesselWiderrufenAsync(Guid schluesselId, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        var konto = await daten.FindenAsync<Konto>(wer.KontoIdPflicht, abbruch) ?? throw new FachlicherFehler("Konto nicht gefunden.");
        konto.SchluesselWiderrufen(schluesselId, uhr.GetUtcNow());
        protokoll.Eintragen(wer, "Konto.Schluessel", "Signaturschlüssel widerrufen", konto.Id);
        await daten.SpeichernAsync(abbruch);
    }

    public async Task<Konto?> EigenesKontoAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        return wer.KontoId is { } id ? await daten.FindenAsync<Konto>(id, abbruch) : null;
    }

    /// <summary>Erstes Konto der Systemverwaltung beim allerersten Start (Passwort nur aus der Umgebung, nie aus dem Repository).</summary>
    public async Task<bool> StartkontoAnlegenAsync(string kennung, string email, string passwort, CancellationToken abbruch = default)
    {
        if (await daten.AnzahlAsync(daten.Menge<Konto>(), abbruch) > 0)
        {
            return false;
        }

        var konto = new Konto(kennung, null, "", "Systemverwaltung", email);
        konto.RolleGeben(Rolle.Systemverwaltung);
        konto.PasswortSetzen(Hash(konto, passwort));
        daten.Hinzufuegen(konto);
        protokoll.Eintragen(Benutzer.Automatik, "Konto.Angelegt", $"Startkonto {konto.Kennung} angelegt", konto.Id);
        await daten.SpeichernAsync(abbruch);
        return true;
    }

    private static string Hash(Konto konto, string passwort)
    {
        if (string.IsNullOrEmpty(passwort) || passwort.Length < MinPasswortLaenge)
        {
            throw new FachlicherFehler($"Das Passwort braucht mindestens {MinPasswortLaenge} Zeichen.");
        }

        return Hasher.HashPassword(konto, passwort);
    }
}
