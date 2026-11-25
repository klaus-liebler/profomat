using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Betrieb;

/// <summary>
/// Vorformulierte Mail, die eine Person selbst aus ihrem Mailprogramm verschickt (persönlicher Postausgang): ProfOMat liefert sie als
/// .eml-Datei, das Desktop-Outlook öffnet sie als neue Mail. Anders als <see cref="Nachricht"/> verschickt ProfOMat sie nicht.
/// </summary>
public sealed class Mailentwurf
{
    public const int BetreffMaxLaenge = 300;
    public const int TextMaxLaenge = 20_000;

    private Mailentwurf()
    {
    }

    public Mailentwurf(Guid kontoId, string an, string? anName, string betreff, string text, DateTimeOffset erstellt, string anlass, Guid? bezugId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(an);
        ArgumentException.ThrowIfNullOrWhiteSpace(betreff);
        ArgumentNullException.ThrowIfNull(text);
        Id = Ids.Neu();
        KontoId = kontoId;
        An = an.Trim();
        AnName = string.IsNullOrWhiteSpace(anName) ? null : anName.Trim();
        Betreff = betreff.Length <= BetreffMaxLaenge ? betreff : betreff[..BetreffMaxLaenge];
        Text = text.Length <= TextMaxLaenge ? text : text[..TextMaxLaenge];
        Erstellt = erstellt;
        Anlass = anlass;
        BezugId = bezugId;
    }

    public Guid Id { get; private set; }

    /// <summary>Wem der Entwurf gehört (nur diese Person sieht und öffnet ihn).</summary>
    public Guid KontoId { get; private set; }

    public string An { get; private set; } = string.Empty;

    public string? AnName { get; private set; }

    public string Betreff { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    public DateTimeOffset Erstellt { get; private set; }

    /// <summary>Wofür, z. B. „Hausarbeit: Themenbestätigung“.</summary>
    public string Anlass { get; private set; } = string.Empty;

    /// <summary>Fachlicher Bezug (z. B. Teammitglied), damit ein neuer Entwurf den alten ersetzen kann.</summary>
    public Guid? BezugId { get; private set; }

    /// <summary>Wann der Entwurf zuletzt im Mailprogramm geöffnet wurde.</summary>
    public DateTimeOffset? Geoeffnet { get; private set; }

    /// <summary>Als versandt bzw. erledigt abgehakt.</summary>
    public DateTimeOffset? Erledigt { get; private set; }

    public void AlsGeoeffnetMarkieren(DateTimeOffset jetzt) => Geoeffnet = jetzt;

    public void ErledigtSetzen(bool erledigt, DateTimeOffset jetzt) => Erledigt = erledigt ? Erledigt ?? jetzt : null;
}
