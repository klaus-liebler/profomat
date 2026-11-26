using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using ProfOMat.Application.Betrieb;
using ProfOMat.Application.Signaturen;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;

namespace ProfOMat.Application.Konten;

/// <summary>Angaben zu einem neuen Konto (Ersteinrichtung, Einladung).</summary>
public sealed record Kontoangaben(string Kennung, string? Titel, string Vorname, string Nachname, string Email);

/// <summary>Frisch erzeugter Einmal-Link: das Token gibt es nur jetzt (gespeichert wird sein SHA-256).</summary>
public sealed record EinladungsLink(Einladung Einladung, string Token);

/// <summary>Ergebnis der Passwortprüfung (erste Stufe der Anmeldung).</summary>
public enum Passwortergebnis
{
    Erfolg = 1,

    /// <summary>Kennung unbekannt, Konto gesperrt oder Passwort falsch – bewusst ohne Unterscheidung.</summary>
    Falsch = 2,

    /// <summary>Vorübergehend gesperrt nach zu vielen Fehlversuchen.</summary>
    VoruebergehendGesperrt = 3,
}

public sealed record Passwortpruefung(Passwortergebnis Ergebnis, Konto? Konto, DateTimeOffset? GesperrtBis = null)
{
    public bool Erfolg => Ergebnis == Passwortergebnis.Erfolg;
}

/// <summary>Was ein (noch gültiger) Link anbietet – für die öffentliche Einlöseseite.</summary>
public sealed record EinladungsInfo(EinladungsArt Art, string? Notiz, DateTimeOffset Ablauf, IReadOnlyList<Rolle> Rollen, string? Kennung, string? Name);

/// <summary>
/// Konten der Lehrenden und Verwaltung auf Grundlage von ASP.NET Core Identity: Ersteinrichtung des ersten Kontos über die Oberfläche,
/// weitere Konten nur per Einladungslink (mit Passwort oder über einen Identitätsdienst), vergessene Passwörter per Passwort-Link.
/// Studierende brauchen kein Konto.
/// </summary>
public sealed partial class KontoDienst(IDaten daten, IAktuellerBenutzer aktuell, Protokoll protokoll, TimeProvider uhr, UserManager<Konto> identitaet,
    ISicherheitsprotokoll sicherheit)
{
    public const int MinPasswortLaenge = 12;
    public const int EinladungTageStandard = 7;
    public const int PasswortLinkTage = 3;

    /// <summary>PBKDF2-Iterationen (OWASP); dieselben Optionen gelten für ASP.NET Core Identity (<c>PasswordHasherOptions</c>).</summary>
    public const int PasswortIterationen = 210_000;

    /// <summary>PBKDF2-HMAC-SHA512 im Format von ASP.NET Core Identity; ältere Hashes werden bei der Anmeldung erneuert.</summary>
    private static readonly PasswordHasher<Konto> Hasher = new(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = PasswortIterationen }));

    /// <summary>Ersteinrichtung nur einmal gleichzeitig (zwei Anfragen könnten sonst beide „kein Konto“ sehen).</summary>
    private static readonly SemaphoreSlim EinrichtungSperre = new(1, 1);

    /// <summary>Auch bei unbekannter Kennung einmal prüfen, damit die Antwortzeit nichts verrät.</summary>
    private static readonly Konto Attrappe = new("attrappe", null, "", "Attrappe", "attrappe@example.invalid");
    private static readonly string AttrappenHash = Hasher.HashPassword(Attrappe, Guid.NewGuid().ToString());

    public static Benutzer AlsBenutzer(Konto konto)
    {
        ArgumentNullException.ThrowIfNull(konto);
        return new Benutzer(konto.Kennung, konto.Anzeigename, konto.Id, konto.Rollen.Select(r => r.Rolle).Order().ToList());
    }

    /// <summary>
    /// Erste Stufe der Passwortanmeldung über ASP.NET Core Identity: Kennung und Passwort prüfen, Fehlversuche zählen, nach
    /// <see cref="AnmeldeOptionen.SperreNachFehlversuchen"/> Fehlversuchen vorübergehend sperren. Angemeldet wird erst danach
    /// (gegebenenfalls nach dem zweiten Faktor). Ohne Hinweis, ob Kennung oder Passwort falsch war.
    /// </summary>
    public async Task<Passwortpruefung> PasswortPruefenAsync(string kennung, string passwort, CancellationToken abbruch = default)
    {
        if (string.IsNullOrWhiteSpace(kennung) || string.IsNullOrEmpty(passwort))
        {
            return new Passwortpruefung(Passwortergebnis.Falsch, null);
        }

        var konto = await identitaet.FindByNameAsync(kennung.Trim());
        if (konto is not { Aktiv: true, PasswortHash: not null })
        {
            Hasher.VerifyHashedPassword(Attrappe, AttrappenHash, passwort);   // gleiche Antwortzeit wie bei bekannter Kennung
            await sicherheit.EintragenAsync("Anmeldung.Fehlversuch", konto?.Id, konto is null ? "unbekannte Kennung" : "Konto gesperrt oder ohne Passwort", abbruch);
            return new Passwortpruefung(Passwortergebnis.Falsch, null);
        }

        if (await identitaet.IsLockedOutAsync(konto))
        {
            return new Passwortpruefung(Passwortergebnis.VoruebergehendGesperrt, null, konto.GesperrtBis);
        }

        if (await identitaet.CheckPasswordAsync(konto, passwort))
        {
            await identitaet.ResetAccessFailedCountAsync(konto);
            return new Passwortpruefung(Passwortergebnis.Erfolg, konto);
        }

        await identitaet.AccessFailedAsync(konto);
        if (await identitaet.IsLockedOutAsync(konto))
        {
            protokoll.Eintragen(AlsBenutzer(konto), "Anmeldung.Sperre", "Vorübergehend gesperrt nach zu vielen Fehlversuchen", konto.Id);
            await sicherheit.EintragenAsync("Anmeldung.Sperre", konto.Id, "vorübergehend gesperrt nach zu vielen Fehlversuchen", abbruch);
            await daten.SpeichernAsync(abbruch);
            return new Passwortpruefung(Passwortergebnis.VoruebergehendGesperrt, null, konto.GesperrtBis);
        }

        await sicherheit.EintragenAsync("Anmeldung.Fehlversuch", konto.Id, "Passwort falsch", abbruch);
        return new Passwortpruefung(Passwortergebnis.Falsch, null);
    }

    /// <summary>Erfolgreiche Anmeldung ins Protokoll (Art, zweiter Faktor) – ohne Personendaten im Betriebslog.</summary>
    public async Task AnmeldungProtokollierenAsync(Konto konto, Anmeldeart art, bool zweiterFaktor, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(konto);
        protokoll.Eintragen(AlsBenutzer(konto), "Anmeldung", $"Anmeldung ({art}{(zweiterFaktor ? ", zweiter Faktor" : "")})", konto.Id);
        await sicherheit.EintragenAsync("Anmeldung", konto.Id, $"{art}{(zweiterFaktor ? ", zweiter Faktor" : "")}", abbruch);
        await daten.SpeichernAsync(abbruch);
    }

    /// <summary>Aktives Konto für die Anmeldung (Testanmeldung, Passkey, Identitätsdienst); <c>null</c>, wenn unbekannt oder gesperrt.</summary>
    public async Task<Konto?> AktivesKontoAsync(Guid kontoId, CancellationToken abbruch = default)
        => await daten.FindenAsync<Konto>(kontoId, abbruch) is { Aktiv: true } konto ? konto : null;

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
        await sicherheit.EintragenAsync("Rechte", wer.KontoId, $"Konto {konto.Id:D} angelegt, Rollen: {string.Join(", ", konto.Rollen.Select(r => r.Rolle.Anzeigename()))}", abbruch);
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
        await sicherheit.EintragenAsync("Rechte", wer.KontoId, $"Konto {konto.Id:D}: Rollen {string.Join(", ", rollen.Select(r => r.Anzeigename()))}, {(aktiv ? "aktiv" : "gesperrt")}", abbruch);
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
        await sicherheit.EintragenAsync("Konto.Passwort", wer.KontoId, selbst ? "eigenes Passwort geändert" : $"Passwort für Konto {konto.Id:D} gesetzt", abbruch);
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

    // ================================================================================================ Sitzungen

    /// <summary>
    /// Aktueller Sicherheitsstempel (ASP.NET Core Identity) eines aktiven Kontos; <c>null</c>, wenn es das Konto nicht (mehr) gibt oder es
    /// gesperrt ist. Steht im Anmelde-Cookie; passt er nicht mehr (Passwort, Rollen, Sperre, zweiter Faktor, Verknüpfungen geändert),
    /// endet die Sitzung. Eine schmale Abfrage, weil sie bei jeder Anfrage läuft.
    /// </summary>
    public async Task<string?> StempelAsync(Guid kontoId, CancellationToken abbruch = default)
        => await daten.ErsterAsync(daten.Menge<Konto>().Where(k => k.Id == kontoId && k.Aktiv).Select(k => k.SicherheitsStempel), abbruch);

    /// <summary>Systemverwaltung: zweiten Faktor eines Kontos zurücksetzen (Gerät verloren). Bei Pflicht richtet die Person ihn bei der nächsten Anmeldung neu ein.</summary>
    public async Task ZweitenFaktorZuruecksetzenAsync(Guid kontoId, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Verwaltung(wer).Sicherstellen();
        var konto = await daten.FindenAsync<Konto>(kontoId, abbruch) ?? throw new FachlicherFehler("Konto nicht gefunden.");
        if (konto.Id == wer.KontoId)
        {
            throw new FachlicherFehler("Den eigenen zweiten Faktor ändern Sie unter Profil → Anmeldung und Sicherheit.");
        }

        konto.ZweitenFaktorZuruecksetzen();
        protokoll.Eintragen(wer, "Konto.ZweiterFaktor", $"Zweiter Faktor für {konto.Kennung} zurückgesetzt", konto.Id);
        await sicherheit.EintragenAsync("Konto.ZweiterFaktor", wer.KontoId, $"zweiter Faktor von Konto {konto.Id:D} zurückgesetzt", abbruch);
        await daten.SpeichernAsync(abbruch);
    }

    // ================================================================================================ Ersteinrichtung

    /// <summary>Solange es kein einziges Konto gibt, legt man das erste Konto der Systemverwaltung über die Anmeldeseite an.</summary>
    public async Task<bool> ErsteinrichtungNoetigAsync(CancellationToken abbruch = default)
        => await daten.AnzahlAsync(daten.Menge<Konto>(), abbruch) == 0;

    /// <summary>Erstes Konto (Systemverwaltung, auf Wunsch zugleich Prüfende). Danach ist die Ersteinrichtung gesperrt.</summary>
    public async Task<Benutzer> ErsteinrichtenAsync(Kontoangaben angaben, string passwort, bool auchPruefende, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(angaben);
        await EinrichtungSperre.WaitAsync(abbruch);
        try
        {
            if (!await ErsteinrichtungNoetigAsync(abbruch))
            {
                throw new FachlicherFehler("ProfOMat ist bereits eingerichtet – bitte anmelden.");
            }

            var konto = NeuesKonto(angaben, passwort, auchPruefende ? [Rolle.Systemverwaltung, Rolle.Pruefende] : [Rolle.Systemverwaltung]);
            daten.Hinzufuegen(konto);
            var benutzer = AlsBenutzer(konto);
            protokoll.Eintragen(benutzer, "Konto.Ersteinrichtung", $"Ersteinrichtung: Konto {konto.Kennung} (Systemverwaltung) angelegt", konto.Id);
            await sicherheit.EintragenAsync("Rechte", konto.Id, "Ersteinrichtung: erstes Konto (Systemverwaltung) angelegt", abbruch);
            await daten.SpeichernAsync(abbruch);
            return benutzer;
        }
        finally
        {
            EinrichtungSperre.Release();
        }
    }

    // ================================================================================================ Einladungen und Passwort-Links

    /// <summary>Einladungslink für ein neues Konto mit festen Rollen (1–30 Tage gültig, einmal verwendbar).</summary>
    public async Task<EinladungsLink> EinladungErzeugenAsync(IReadOnlyCollection<Rolle> rollen, string? notiz, int tage, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(rollen);
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Verwaltung(wer).Sicherstellen();
        if (rollen.Count == 0)
        {
            throw new FachlicherFehler("Bitte mindestens eine Rolle für das neue Konto wählen.");
        }

        var (token, hash) = NeuesToken();
        var jetzt = uhr.GetUtcNow();
        var e = new Einladung(EinladungsArt.Konto, hash, rollen, null, notiz, wer.KontoIdPflicht, jetzt, jetzt.AddDays(Math.Clamp(tage, 1, 30)));
        daten.Hinzufuegen(e);
        protokoll.Eintragen(wer, "Konto.Einladung", $"Einladungslink erzeugt ({string.Join(", ", e.RollenListe.Select(r => r.Anzeigename()))})", e.Id);
        await sicherheit.EintragenAsync("Rechte", wer.KontoId, $"Einladungslink erzeugt, Rollen: {string.Join(", ", e.RollenListe.Select(r => r.Anzeigename()))}", abbruch);
        await daten.SpeichernAsync(abbruch);
        return new EinladungsLink(e, token);
    }

    /// <summary>Passwort-Link für ein bestehendes Konto (3 Tage, einmal verwendbar).</summary>
    public async Task<EinladungsLink> PasswortLinkErzeugenAsync(Guid kontoId, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Verwaltung(wer).Sicherstellen();
        var konto = await daten.FindenAsync<Konto>(kontoId, abbruch) ?? throw new FachlicherFehler("Konto nicht gefunden.");
        var (token, hash) = NeuesToken();
        var jetzt = uhr.GetUtcNow();
        var e = new Einladung(EinladungsArt.Passwort, hash, [], konto.Id, $"Passwort für {konto.Kennung}", wer.KontoIdPflicht, jetzt, jetzt.AddDays(PasswortLinkTage));
        daten.Hinzufuegen(e);
        protokoll.Eintragen(wer, "Konto.PasswortLink", $"Passwort-Link für {konto.Kennung} erzeugt", konto.Id);
        await sicherheit.EintragenAsync("Konto.Passwort", wer.KontoId, $"Passwort-Link für Konto {konto.Id:D} erzeugt", abbruch);
        await daten.SpeichernAsync(abbruch);
        return new EinladungsLink(e, token);
    }

    /// <summary>Noch nicht eingelöste, nicht abgelaufene Links (Übersicht der Systemverwaltung).</summary>
    public async Task<IReadOnlyList<Einladung>> OffeneEinladungenAsync(CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Verwaltung(wer).Sicherstellen();
        var jetzt = uhr.GetUtcNow();
        return (await daten.ListeAsync(daten.Menge<Einladung>().Where(e => e.Eingeloest == null), abbruch))
            .Where(e => e.Ablauf > jetzt).OrderByDescending(e => e.Erstellt).ToList();
    }

    public async Task EinladungZurueckziehenAsync(Guid einladungId, CancellationToken abbruch = default)
    {
        var wer = await aktuell.ErmittelnAsync(abbruch);
        Rechte.Verwaltung(wer).Sicherstellen();
        if (await daten.FindenAsync<Einladung>(einladungId, abbruch) is { } e)
        {
            daten.Entfernen(e);
            protokoll.Eintragen(wer, "Konto.EinladungZurueck", "Link zurückgezogen", e.Id);
            await daten.SpeichernAsync(abbruch);
        }
    }

    /// <summary>Öffentlich: Was bietet dieser Link? <c>null</c>, wenn ungültig, abgelaufen oder schon verwendet.</summary>
    public async Task<EinladungsInfo?> EinladungPruefenAsync(string? token, CancellationToken abbruch = default)
    {
        if (await GueltigeEinladungAsync(token, abbruch) is not { } e)
        {
            return null;
        }

        var konto = e.KontoId is { } k ? await daten.FindenAsync<Konto>(k, abbruch) : null;
        return new EinladungsInfo(e.Art, e.Notiz, e.Ablauf, e.RollenListe, konto?.Kennung, konto?.Anzeigename);
    }

    /// <summary>Öffentlich: Link einlösen – neues Konto anlegen bzw. Passwort setzen. Liefert das Konto zum direkten Anmelden.</summary>
    public async Task<Benutzer> EinladungEinloesenAsync(string? token, Kontoangaben? angaben, string passwort, CancellationToken abbruch = default)
    {
        var e = await GueltigeEinladungAsync(token, abbruch) ?? throw new FachlicherFehler("Der Link ist ungültig, abgelaufen oder wurde schon verwendet.");
        Konto konto;
        if (e.Art == EinladungsArt.Passwort)
        {
            konto = await daten.FindenAsync<Konto>(e.KontoId!.Value, abbruch) ?? throw new FachlicherFehler("Das Konto gibt es nicht mehr.");
            konto.PasswortSetzen(Hash(konto, passwort));
        }
        else
        {
            konto = NeuesKonto(angaben ?? throw new FachlicherFehler("Bitte die Angaben zum Konto ausfüllen."), passwort, e.RollenListe);
            if (await daten.AnzahlAsync(daten.Menge<Konto>().Where(x => x.Kennung == konto.Kennung), abbruch) > 0)
            {
                throw new FachlicherFehler("Diese Kennung ist schon vergeben – bitte eine andere wählen.");
            }

            daten.Hinzufuegen(konto);
        }

        e.Einloesen(konto.Id, uhr.GetUtcNow());
        protokoll.Eintragen(AlsBenutzer(konto), e.Art == EinladungsArt.Konto ? "Konto.Angelegt" : "Konto.Passwort",
            e.Art == EinladungsArt.Konto ? $"Konto {konto.Kennung} per Einladung angelegt" : $"Passwort für {konto.Kennung} per Link gesetzt", konto.Id);
        await sicherheit.EintragenAsync(e.Art == EinladungsArt.Konto ? "Rechte" : "Konto.Passwort", konto.Id, e.Art == EinladungsArt.Konto ? $"Konto per Einladung angelegt, Rollen: {string.Join(", ", e.RollenListe.Select(r => r.Anzeigename()))}" : "Passwort per Link gesetzt", abbruch);
        await daten.SpeichernAsync(abbruch);
        return AlsBenutzer(konto);
    }

    /// <summary>
    /// Öffentlich: Einladungslink über einen Identitätsdienst (SAML/OIDC) einlösen. Das neue Konto hat kein Passwort und ist mit der dauerhaften
    /// externen Kennung verknüpft; Name und Mail kommen vom Identitätsdienst. Passwort-Links lassen sich so nicht einlösen.
    /// </summary>
    public async Task<Konto> EinladungMitIdentitaetsdienstEinloesenAsync(string? token, ExterneIdentitaet ext, CancellationToken abbruch = default)
    {
        ArgumentNullException.ThrowIfNull(ext);
        var e = await GueltigeEinladungAsync(token, abbruch) ?? throw new FachlicherFehler("Der Link ist ungültig, abgelaufen oder wurde schon verwendet.");
        if (e.Art != EinladungsArt.Konto)
        {
            throw new FachlicherFehler("Ein Passwort-Link lässt sich nicht über einen Identitätsdienst einlösen.");
        }

        if (await identitaet.FindByLoginAsync(ext.Anbieter, ext.Schluessel) is not null)
        {
            throw new FachlicherFehler("Mit diesem Konto des Identitätsdienstes ist schon ein ProfOMat-Konto verknüpft – bitte einfach anmelden.");
        }

        if (string.IsNullOrWhiteSpace(ext.Email) || !ext.Email.Contains('@', StringComparison.Ordinal))
        {
            throw new FachlicherFehler("Der Identitätsdienst hat keine Mailadresse übermittelt. Bitte beim Rechenzentrum melden.");
        }

        var basis = ext.KennungsVorschlag();
        var kennung = basis;
        for (var i = 2; await daten.AnzahlAsync(daten.Menge<Konto>().Where(x => x.Kennung == kennung), abbruch) > 0; i++)
        {
            kennung = basis + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var nachname = ext.Nachname ?? ext.Anzeigename ?? kennung;
        var konto = new Konto(kennung, null, ext.Vorname ?? "", nachname, ext.Email, KontoHerkunft.Hochschule);
        foreach (var r in e.RollenListe)
        {
            konto.RolleGeben(r);
        }

        var jetzt = uhr.GetUtcNow();
        konto.AnmeldungVerknuepfen(ext.Anbieter, ext.Schluessel, ext.Anbieter == ExterneIdentitaet.Saml ? "Hochschulkonto" : "Firmenkonto", jetzt);
        daten.Hinzufuegen(konto);
        e.Einloesen(konto.Id, jetzt);
        protokoll.Eintragen(AlsBenutzer(konto), "Konto.Angelegt", $"Konto {konto.Kennung} per Einladung über {ext.Anbieter} angelegt", konto.Id);
        await sicherheit.EintragenAsync("Rechte", konto.Id, $"Konto per Einladung über {ext.Anbieter} angelegt, Rollen: {string.Join(", ", e.RollenListe.Select(r => r.Anzeigename()))}", abbruch);
        await daten.SpeichernAsync(abbruch);
        return konto;
    }

    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<Einladung?> GueltigeEinladungAsync(string? token, CancellationToken abbruch)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            return null;
        }

        var hash = TokenHash(token);
        var e = await daten.ErsterAsync(daten.Menge<Einladung>().Where(x => x.TokenHash == hash), abbruch);
        return e is not null && e.Gueltig(uhr.GetUtcNow()) ? e : null;
    }

    private static (string Token, string Hash) NeuesToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, TokenHash(token));
    }

    private static Konto NeuesKonto(Kontoangaben a, string passwort, IEnumerable<Rolle> rollen)
    {
        var kennung = a.Kennung?.Trim().ToLowerInvariant() ?? "";
        if (!KennungMuster().IsMatch(kennung))
        {
            throw new FachlicherFehler("Kennung: 3–40 Zeichen, nur a–z, 0–9, Punkt, Bindestrich, Unterstrich.");
        }

        if (string.IsNullOrWhiteSpace(a.Nachname))
        {
            throw new FachlicherFehler("Bitte den Nachnamen angeben.");
        }

        if (string.IsNullOrWhiteSpace(a.Email) || !a.Email.Contains('@', StringComparison.Ordinal))
        {
            throw new FachlicherFehler("Bitte eine gültige Mailadresse angeben (für Benachrichtigungen).");
        }

        var konto = new Konto(kennung, a.Titel, a.Vorname?.Trim() ?? "", a.Nachname.Trim(), a.Email.Trim());
        foreach (var r in rollen)
        {
            konto.RolleGeben(r);
        }

        konto.PasswortSetzen(Hash(konto, passwort));
        return konto;
    }

    [GeneratedRegex("^[a-z0-9._-]{3,40}$")]
    private static partial Regex KennungMuster();

    private static string Hash(Konto konto, string passwort)
    {
        if (string.IsNullOrEmpty(passwort) || passwort.Length < MinPasswortLaenge)
        {
            throw new FachlicherFehler($"Das Passwort braucht mindestens {MinPasswortLaenge} Zeichen.");
        }

        return Hasher.HashPassword(konto, passwort);
    }
}
