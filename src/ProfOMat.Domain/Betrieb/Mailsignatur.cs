using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Betrieb;

/// <summary>
/// Optische Mailsignatur eines Kontos (aus der Outlook-Signaturdatei übernommen und bereinigt). Bilder werden als eingebettete
/// Anhänge (cid) mitgeschickt. Die kryptografische Signatur setzt Outlook beim Senden, nie ProfOMat.
/// </summary>
public sealed class Mailsignatur
{
    public const int HtmlMaxLaenge = 200_000;
    public const int TextMaxLaenge = 4000;
    public const int MaxBilder = 12;
    public const int BildMaxBytes = 300 * 1024;

    private readonly List<MailsignaturBild> bilder = [];

    private Mailsignatur()
    {
    }

    public Mailsignatur(Guid kontoId)
    {
        Id = Ids.Neu();
        KontoId = kontoId;
    }

    public Guid Id { get; private set; }

    public Guid KontoId { get; private set; }

    /// <summary>Bereinigtes HTML (Inhalt für den Mailkörper); Bilder als <c>cid:</c>-Verweise.</summary>
    public string Html { get; private set; } = string.Empty;

    /// <summary>Signatur als reiner Text (für den Textteil der Mail).</summary>
    public string Text { get; private set; } = string.Empty;

    public DateTimeOffset Aktualisiert { get; private set; }

    public IReadOnlyList<MailsignaturBild> Bilder => bilder;

    public void Setzen(string html, string text, IReadOnlyList<(string ContentId, string Dateiname, string Typ, byte[] Inhalt)> neueBilder, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(neueBilder);
        if (html.Length > HtmlMaxLaenge)
        {
            throw new FachlicherFehler("Die Signatur ist zu groß.");
        }

        if (neueBilder.Count > MaxBilder || neueBilder.Any(b => b.Inhalt.Length > BildMaxBytes))
        {
            throw new FachlicherFehler($"Höchstens {MaxBilder} Bilder mit je höchstens {BildMaxBytes / 1024} KB.");
        }

        Html = html;
        Text = text.Length <= TextMaxLaenge ? text : text[..TextMaxLaenge];
        bilder.Clear();
        foreach (var b in neueBilder)
        {
            bilder.Add(new MailsignaturBild(Id, b.ContentId, b.Dateiname, b.Typ, b.Inhalt));
        }

        Aktualisiert = jetzt;
    }
}

/// <summary>Eingebettetes Bild der Mailsignatur.</summary>
public sealed class MailsignaturBild
{
    private MailsignaturBild()
    {
    }

    internal MailsignaturBild(Guid signaturId, string contentId, string dateiname, string typ, byte[] inhalt)
    {
        Id = Ids.Neu();
        SignaturId = signaturId;
        ContentId = contentId;
        Dateiname = dateiname;
        Typ = typ;
        Inhalt = inhalt;
    }

    public Guid Id { get; private set; }

    public Guid SignaturId { get; private set; }

    /// <summary>Verweis im HTML: <c>cid:&lt;ContentId&gt;</c>.</summary>
    public string ContentId { get; private set; } = string.Empty;

    public string Dateiname { get; private set; } = string.Empty;

    public string Typ { get; private set; } = string.Empty;

    public byte[] Inhalt { get; private set; } = [];
}
