using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Betrieb;

/// <summary>Ausgehende Mail. Jede Mail läuft über den Postausgang (nachvollziehbar, Wiederholung bei Fehlern).</summary>
public sealed class Nachricht
{
    private Nachricht()
    {
    }

    public Nachricht(string an, string betreff, string text, DateTimeOffset erstellt, Guid? antragId = null, Guid? anhangDokumentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(an);
        ArgumentException.ThrowIfNullOrWhiteSpace(betreff);
        Id = Ids.Neu();
        An = an.Trim();
        Betreff = betreff;
        Text = text ?? string.Empty;
        Erstellt = erstellt;
        AntragId = antragId;
        AnhangDokumentId = anhangDokumentId;
    }

    public Guid Id { get; private set; }

    public string An { get; private set; } = string.Empty;

    public string Betreff { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    public DateTimeOffset Erstellt { get; private set; }

    public Guid? AntragId { get; private set; }

    public Guid? AnhangDokumentId { get; private set; }

    public DateTimeOffset? Versandt { get; private set; }

    /// <summary>Nur simuliert (kein Mailserver konfiguriert, Entwicklung).</summary>
    public bool Simuliert { get; private set; }

    public int Fehlversuche { get; private set; }

    public string? LetzterFehler { get; private set; }

    public DateTimeOffset? NaechsterVersuch { get; private set; }

    public bool Aufgegeben => Fehlversuche >= 8;

    public void AlsVersandtMarkieren(DateTimeOffset jetzt, bool simuliert)
    {
        Versandt = jetzt;
        Simuliert = simuliert;
        LetzterFehler = null;
        NaechsterVersuch = null;
    }

    public void FehlschlagMerken(DateTimeOffset jetzt, string fehler)
    {
        Fehlversuche++;
        LetzterFehler = fehler.Length > 500 ? fehler[..500] : fehler;
        NaechsterVersuch = jetzt.AddMinutes(Math.Pow(2, Math.Min(Fehlversuche, 8)));
    }
}

/// <summary>Erzeugtes Dokument (PDF), unveränderlich, mit SHA-256.</summary>
public sealed class Dokument
{
    private Dokument()
    {
    }

    public Dokument(Guid? antragId, string dateiname, string inhaltstyp, byte[] inhalt, string sha256, DateTimeOffset erstellt)
    {
        ArgumentNullException.ThrowIfNull(inhalt);
        Id = Ids.Neu();
        AntragId = antragId;
        Dateiname = dateiname;
        Inhaltstyp = inhaltstyp;
        Inhalt = inhalt;
        Sha256 = sha256;
        Erstellt = erstellt;
    }

    public Guid Id { get; private set; }

    public Guid? AntragId { get; private set; }

    public string Dateiname { get; private set; } = string.Empty;

    public string Inhaltstyp { get; private set; } = string.Empty;

    public byte[] Inhalt { get; private set; } = [];

    public string Sha256 { get; private set; } = string.Empty;

    public DateTimeOffset Erstellt { get; private set; }
}

/// <summary>Protokolleintrag: wer hat wann was getan. Keine Namen von Studierenden, nur Antragsnummern.</summary>
public sealed class Protokolleintrag
{
    private Protokolleintrag()
    {
    }

    public Protokolleintrag(DateTimeOffset zeitpunkt, string wer, string aktion, string text, Guid? bezug)
    {
        Id = Ids.Neu();
        Zeitpunkt = zeitpunkt;
        Wer = wer;
        Aktion = aktion;
        Text = text;
        Bezug = bezug;
    }

    public Guid Id { get; private set; }

    public DateTimeOffset Zeitpunkt { get; private set; }

    public string Wer { get; private set; } = string.Empty;

    public string Aktion { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    public Guid? Bezug { get; private set; }
}

/// <summary>
/// Eintrag im Sicherheitsprotokoll (getrennt vom Betriebslog und vom fachlichen Protokoll): Anmeldungen und Fehlversuche, Rechte- und
/// Rollenänderungen, Exporte, Konfigurationsänderungen. Nur anfügen (Trigger in der Datenbank), hash-verkettet: Jeder Eintrag enthält den
/// Hash des vorigen; eine nachträgliche Änderung oder Lücke fällt bei der Prüfung auf. Personen nur als Konto-Id (Pseudonym).
/// </summary>
public sealed class Sicherheitsereignis
{
    private Sicherheitsereignis()
    {
    }

    public Sicherheitsereignis(long nummer, DateTimeOffset zeitpunkt, string art, Guid? kontoId, string text, string vorgaengerHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(art);
        Id = Ids.Neu();
        Nummer = nummer;
        Zeitpunkt = zeitpunkt;
        Art = art;
        KontoId = kontoId;
        Text = text ?? string.Empty;
        VorgaengerHash = vorgaengerHash ?? string.Empty;
        Hash = Berechnen(Nummer, Zeitpunkt, Art, KontoId, Text, VorgaengerHash);
    }

    public Guid Id { get; private set; }

    /// <summary>Fortlaufend ab 1, ohne Lücken.</summary>
    public long Nummer { get; private set; }

    public DateTimeOffset Zeitpunkt { get; private set; }

    /// <summary>z. B. Anmeldung, Anmeldung.Fehlversuch, Rechte, Export, Konfiguration.</summary>
    public string Art { get; private set; } = string.Empty;

    public Guid? KontoId { get; private set; }

    public string Text { get; private set; } = string.Empty;

    /// <summary>Hash des vorigen Eintrags; beim ersten leer.</summary>
    public string VorgaengerHash { get; private set; } = string.Empty;

    /// <summary>SHA-256 (hex) über Nummer, Zeitpunkt, Art, Konto, Text und Vorgänger.</summary>
    public string Hash { get; private set; } = string.Empty;

    public bool Stimmt => Hash == Berechnen(Nummer, Zeitpunkt, Art, KontoId, Text, VorgaengerHash);

    public static string Berechnen(long nummer, DateTimeOffset zeitpunkt, string art, Guid? kontoId, string text, string vorgaenger)
    {
        var roh = string.Join('\n', nummer.ToString(System.Globalization.CultureInfo.InvariantCulture),
            zeitpunkt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), art, kontoId?.ToString("D") ?? "-", text, vorgaenger);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(roh))).ToLowerInvariant();
    }
}
