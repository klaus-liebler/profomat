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
