namespace ProfOMat.Domain.Gemeinsam;

/// <summary>Ids: UUIDv7 (zeitlich sortierbar), wie in VibeVHS.</summary>
public static class Ids
{
    public static Guid Neu() => Guid.CreateVersion7();
}

/// <summary>
/// Ob eine Aktion gerade erlaubt ist. Ist sie es nicht, gibt es immer eine verständliche <see cref="Begruendung"/>,
/// die die Oberfläche als Tooltip zeigt („Gesperrte Aktionen erklären“). Liegt im Fachmodell, weil auch die
/// Zustandsübergänge des Antrags damit antworten.
/// </summary>
public sealed record Freigabe
{
    private Freigabe(bool erlaubt, string? begruendung)
    {
        Erlaubt = erlaubt;
        Begruendung = begruendung;
    }

    public bool Erlaubt { get; }

    public string? Begruendung { get; }

    public static Freigabe Ja { get; } = new(true, null);

    public static Freigabe Nein(string begruendung)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(begruendung);
        return new Freigabe(false, begruendung);
    }

    /// <summary>Funktion ist geplant, aber noch nicht umgesetzt.</summary>
    public static Freigabe Geplant(string funktion, string iteration)
        => Nein($"{funktion} folgt in Iteration {iteration} (siehe docs/spezifikation/fahrplan.md).");

    /// <summary>Verknüpft Bedingungen; die erste nicht erfüllte liefert die Begründung.</summary>
    public Freigabe Und(Func<Freigabe> weitere)
    {
        ArgumentNullException.ThrowIfNull(weitere);
        return Erlaubt ? weitere() : this;
    }

    /// <summary>Wirft mit der Begründung, wenn nicht erlaubt (für Dienste: Rechte nie nur in der Oberfläche prüfen).</summary>
    public void Sicherstellen()
    {
        if (!Erlaubt)
        {
            throw new FachlicherFehler(Begruendung!);
        }
    }
}

/// <summary>Fachlich nicht zulässige Aktion; die Meldung ist für Menschen geschrieben und darf angezeigt werden.</summary>
public sealed class FachlicherFehler : Exception
{
    public FachlicherFehler()
    {
    }

    public FachlicherFehler(string message) : base(message)
    {
    }

    public FachlicherFehler(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Pseudonyme für Logs und Protokoll (keine Personendaten in Logs).</summary>
public static class Pseudonym
{
    public static string Id(Guid id) => id.ToString("N")[^8..];
}
