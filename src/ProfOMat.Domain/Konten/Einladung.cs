using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Konten;

public enum EinladungsArt
{
    /// <summary>Neues Konto anlegen (mit den vorgegebenen Rollen).</summary>
    Konto,

    /// <summary>Passwort eines bestehenden Kontos neu setzen.</summary>
    Passwort,
}

/// <summary>
/// Einmal-Link für ein neues Konto oder ein neues Passwort (wie in Podium). Der Link enthält ein Zufallstoken hinter „#“,
/// gespeichert wird nur dessen SHA-256. Gültig bis <see cref="Ablauf"/>, nur einmal verwendbar.
/// </summary>
public sealed class Einladung
{
    public const int NotizMaxLaenge = 200;

    private Einladung()
    {
    }

    public Einladung(EinladungsArt art, string tokenHash, IEnumerable<Rolle> rollen, Guid? kontoId, string? notiz, Guid erstelltVon,
        DateTimeOffset erstellt, DateTimeOffset ablauf)
    {
        if (art == EinladungsArt.Passwort && kontoId is null)
        {
            throw new FachlicherFehler("Ein Passwort-Link gehört zu einem Konto.");
        }

        Id = Ids.Neu();
        Art = art;
        TokenHash = tokenHash;
        Rollen = string.Join(',', (rollen ?? []).Distinct().Order());
        KontoId = kontoId;
        var n = notiz?.Trim() ?? "";
        Notiz = n.Length == 0 ? null : n.Length > NotizMaxLaenge ? n[..NotizMaxLaenge] : n;
        ErstelltVon = erstelltVon;
        Erstellt = erstellt;
        Ablauf = ablauf;
    }

    public Guid Id { get; private set; }

    public EinladungsArt Art { get; private set; }

    /// <summary>SHA-256 des Tokens (Hex) – das Token selbst wird nie gespeichert.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    /// <summary>Rollen des neuen Kontos, kommagetrennt (nur bei <see cref="EinladungsArt.Konto"/>).</summary>
    public string Rollen { get; private set; } = string.Empty;

    /// <summary>Konto, dessen Passwort neu gesetzt wird (nur bei <see cref="EinladungsArt.Passwort"/>).</summary>
    public Guid? KontoId { get; private set; }

    /// <summary>Für wen der Link gedacht ist (nur zur Übersicht).</summary>
    public string? Notiz { get; private set; }

    public Guid ErstelltVon { get; private set; }

    public DateTimeOffset Erstellt { get; private set; }

    public DateTimeOffset Ablauf { get; private set; }

    public DateTimeOffset? Eingeloest { get; private set; }

    public Guid? EingeloestVon { get; private set; }

    public IReadOnlyList<Rolle> RollenListe => Rollen.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(r => Enum.TryParse<Rolle>(r, out var x) ? x : (Rolle?)null).OfType<Rolle>().ToList();

    public bool Gueltig(DateTimeOffset jetzt) => Eingeloest is null && Ablauf > jetzt;

    public void Einloesen(Guid kontoId, DateTimeOffset jetzt)
    {
        if (!Gueltig(jetzt))
        {
            throw new FachlicherFehler("Der Link ist ungültig, abgelaufen oder wurde schon verwendet.");
        }

        (Eingeloest, EingeloestVon) = (jetzt, kontoId);
    }
}
