using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Finanzen;

/// <summary>
/// Lehrende, die gemeinsam die Finanzen eines Labors planen (z. B. zwei Professoren mit einer gemeinsamen Laborkostenstelle).
/// Alle Mitglieder sind gleichberechtigt; Finanzdaten sieht nur, wer Mitglied ist.
/// </summary>
public sealed class Laborteam
{
    public const int NameMaxLaenge = 120;

    private readonly List<Labormitglied> mitglieder = [];

    private Laborteam()
    {
    }

    public Laborteam(string name, Guid gruenderKontoId, DateTimeOffset angelegt)
    {
        Id = Ids.Neu();
        Umbenennen(name);
        Angelegt = angelegt;
        mitglieder.Add(new Labormitglied(Id, gruenderKontoId, angelegt));
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset Angelegt { get; private set; }

    public IReadOnlyList<Labormitglied> Mitglieder => mitglieder;

    /// <summary>Tage vor einer Frist, an denen die Mitglieder per Mail erinnert werden (0 = keine Erinnerungen).</summary>
    public int ErinnerungVorlaufTage { get; private set; } = StandardVorlaufTage;

    public const int StandardVorlaufTage = 30;

    public void ErinnerungEinstellen(int tage)
    {
        if (tage is < 0 or > 180)
        {
            throw new FachlicherFehler("Der Vorlauf liegt zwischen 0 (aus) und 180 Tagen.");
        }

        ErinnerungVorlaufTage = tage;
    }

    public bool IstMitglied(Guid? kontoId) => kontoId is { } id && mitglieder.Any(m => m.KontoId == id);

    public void Umbenennen(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new FachlicherFehler("Bitte einen Namen für das Laborteam angeben.");
        }

        var n = name.Trim();
        Name = n.Length > NameMaxLaenge ? n[..NameMaxLaenge] : n;
    }

    public void Aufnehmen(Guid kontoId, DateTimeOffset jetzt)
    {
        if (IstMitglied(kontoId))
        {
            throw new FachlicherFehler("Diese Person gehört schon zum Team.");
        }

        mitglieder.Add(new Labormitglied(Id, kontoId, jetzt));
    }

    /// <summary>Austreten bzw. entfernen; das letzte Mitglied bleibt (sonst sähe niemand mehr die Daten).</summary>
    public Freigabe Entfernbar(Guid kontoId)
        => !IstMitglied(kontoId) ? Freigabe.Nein("Diese Person gehört nicht zum Team.")
            : mitglieder.Count == 1 ? Freigabe.Nein("Das letzte Mitglied kann nicht austreten – sonst sähe niemand mehr die Finanzdaten. Erst eine weitere Person aufnehmen.")
            : Freigabe.Ja;

    public void Entfernen(Guid kontoId)
    {
        Entfernbar(kontoId).Sicherstellen();
        mitglieder.RemoveAll(m => m.KontoId == kontoId);
    }
}

public sealed class Labormitglied
{
    private Labormitglied()
    {
    }

    internal Labormitglied(Guid teamId, Guid kontoId, DateTimeOffset seit)
    {
        Id = Ids.Neu();
        TeamId = teamId;
        KontoId = kontoId;
        Seit = seit;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public Guid KontoId { get; private set; }

    public DateTimeOffset Seit { get; private set; }
}
