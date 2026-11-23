using System.Security.Cryptography;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Domain.Klausuren;

/// <summary>Eine Stufe des Notenschlüssels: ab <see cref="Mindestpunkte"/> gibt es <see cref="Note"/>.</summary>
public sealed record Notenstufe(decimal Mindestpunkte, decimal Note);

/// <summary>Notenschlüssel als Tabelle „ab Punkte → Note“.</summary>
public static class Notenschluessel
{
    public static readonly decimal[] ErlaubteNoten = [1.0m, 1.3m, 1.7m, 2.0m, 2.3m, 2.7m, 3.0m, 3.3m, 3.7m, 4.0m, 5.0m];

    /// <summary>Standard (bezogen auf 100 Punkte).</summary>
    public static IReadOnlyList<Notenstufe> Standard { get; } =
    [
        new(0m, 5.0m), new(49.5m, 4.0m), new(54.5m, 3.7m), new(59.5m, 3.3m), new(64.5m, 3.0m), new(69.5m, 2.7m),
        new(74.5m, 2.3m), new(79.5m, 2.0m), new(84.5m, 1.7m), new(89.5m, 1.3m), new(94.5m, 1.0m),
    ];

    public const decimal StandardBezug = 100m;

    /// <summary>„sehr gut“ … „nicht ausreichend“.</summary>
    public static string Wort(decimal note) => KlausurAuswertung.Ganz(note) switch
    {
        1 => "sehr gut",
        2 => "gut",
        3 => "befriedigend",
        4 => "ausreichend",
        _ => "nicht ausreichend",
    };

    /// <summary>Prüft und sortiert; liefert die erste Beanstandung oder <c>null</c>.</summary>
    public static string? Beanstandung(IReadOnlyList<Notenstufe> stufen)
    {
        ArgumentNullException.ThrowIfNull(stufen);
        if (stufen.Count < 2)
        {
            return "Der Notenschlüssel braucht mindestens zwei Stufen.";
        }

        if (stufen.Any(s => !ErlaubteNoten.Contains(s.Note)))
        {
            return "Erlaubt sind die Noten 1,0 1,3 1,7 2,0 2,3 2,7 3,0 3,3 3,7 4,0 und 5,0.";
        }

        var sortiert = stufen.OrderBy(s => s.Mindestpunkte).ToList();
        if (sortiert[0].Mindestpunkte != 0m || sortiert[0].Note != 5.0m)
        {
            return "Die erste Stufe lautet „ab 0 Punkte → 5,0“.";
        }

        for (var i = 1; i < sortiert.Count; i++)
        {
            if (sortiert[i].Mindestpunkte == sortiert[i - 1].Mindestpunkte)
            {
                return $"Die Punktgrenze {sortiert[i].Mindestpunkte} kommt doppelt vor.";
            }

            if (sortiert[i].Note >= sortiert[i - 1].Note)
            {
                return $"Mehr Punkte müssen eine bessere Note ergeben (bei {sortiert[i].Mindestpunkte} Punkten).";
            }
        }

        return null;
    }

    public static decimal NoteFuer(IReadOnlyList<Notenstufe> stufen, decimal punkte)
        => stufen.Where(s => s.Mindestpunkte <= punkte).OrderByDescending(s => s.Mindestpunkte).Select(s => s.Note).FirstOrDefault(5.0m);

    /// <summary>Grenzen von <paramref name="bezugAlt"/> auf <paramref name="bezugNeu"/> Punkte umrechnen (auf halbe Punkte gerundet).</summary>
    public static IReadOnlyList<Notenstufe> Umrechnen(IReadOnlyList<Notenstufe> stufen, decimal bezugAlt, decimal bezugNeu)
    {
        ArgumentNullException.ThrowIfNull(stufen);
        if (bezugAlt <= 0 || bezugNeu <= 0)
        {
            throw new FachlicherFehler("Die Höchstpunktzahl muss größer als 0 sein.");
        }

        return stufen.Select(s => s with { Mindestpunkte = Math.Round(s.Mindestpunkte * bezugNeu / bezugAlt * 2m, MidpointRounding.AwayFromZero) / 2m })
            .OrderBy(s => s.Mindestpunkte).ToList();
    }
}

/// <summary>Aufgabe der Klausur. Sonderpunkte zählen zur Summe, aber nicht zur Höchstpunktzahl.</summary>
public sealed class Aufgabe
{
    private Aufgabe()
    {
    }

    internal Aufgabe(Guid klausurId, int nr, string titel, decimal maxPunkte, bool sonderpunkte)
    {
        Id = Ids.Neu();
        KlausurId = klausurId;
        Nr = nr;
        Titel = titel;
        MaxPunkte = maxPunkte;
        Sonderpunkte = sonderpunkte;
    }

    public Guid Id { get; private set; }

    public Guid KlausurId { get; private set; }

    public int Nr { get; private set; }

    public string Titel { get; private set; } = string.Empty;

    public decimal MaxPunkte { get; private set; }

    public bool Sonderpunkte { get; private set; }

    internal void Aendern(int nr, string titel, decimal maxPunkte, bool sonderpunkte)
        => (Nr, Titel, MaxPunkte, Sonderpunkte) = (nr, titel, maxPunkte, sonderpunkte);
}

/// <summary>Punkte einer Teilnahme in einer Aufgabe.</summary>
public sealed class Bewertung
{
    private Bewertung()
    {
    }

    internal Bewertung(Guid teilnahmeId, Guid aufgabeId, decimal punkte)
    {
        TeilnahmeId = teilnahmeId;
        AufgabeId = aufgabeId;
        Punkte = punkte;
    }

    public Guid TeilnahmeId { get; private set; }

    public Guid AufgabeId { get; private set; }

    public decimal Punkte { get; internal set; }
}

/// <summary>Eine angemeldete Person in einer Klausur.</summary>
public sealed class Teilnahme
{
    private readonly List<Bewertung> bewertungen = [];

    private Teilnahme()
    {
    }

    internal Teilnahme(Guid klausurId, string code, string matrikelnummer, string nachname, string vorname, string? email, int zeile)
    {
        Id = Ids.Neu();
        KlausurId = klausurId;
        Code = code;
        Matrikelnummer = matrikelnummer;
        Angemeldet = true;
        Uebernehmen(nachname, vorname, email, zeile);
    }

    public Guid Id { get; private set; }

    public Guid KlausurId { get; private set; }

    /// <summary>Eindeutige Klausur-ID (steht auf dem Deckblatt und in der Veröffentlichung).</summary>
    public string Code { get; private set; } = string.Empty;

    public string Matrikelnummer { get; private set; } = string.Empty;

    public string Nachname { get; private set; } = string.Empty;

    public string Vorname { get; private set; } = string.Empty;

    public string? Email { get; private set; }

    /// <summary>Laufende Nummer in der Prüfungsliste des Hochschulsystems.</summary>
    public int Zeile { get; private set; }

    /// <summary>Im letzten Import enthalten; <c>false</c> = inzwischen abgemeldet (bleibt sichtbar).</summary>
    public bool Angemeldet { get; private set; }

    public Guid? RaumId { get; private set; }

    public int? Platz { get; private set; }

    /// <summary><c>null</c> = noch nicht erfasst.</summary>
    public bool? Anwesend { get; private set; }

    public IReadOnlyList<Bewertung> Bewertungen => bewertungen;

    public string Name => $"{Nachname}, {Vorname}";

    internal bool Uebernehmen(string nachname, string vorname, string? email, int zeile)
    {
        // Die laufende Nummer der Liste verschiebt sich bei Nachmeldungen; das ist keine Änderung der Person.
        var neu = (nachname.Trim(), vorname.Trim(), string.IsNullOrWhiteSpace(email) ? null : email.Trim());
        var geaendert = neu != (Nachname, Vorname, Email);
        (Nachname, Vorname, Email) = neu;
        Zeile = zeile;
        return geaendert;
    }

    internal void AngemeldetSetzen(bool angemeldet) => Angemeldet = angemeldet;

    internal void Setzen(Guid? raumId, int? platz) => (RaumId, Platz) = (raumId, platz);

    internal void AnwesendSetzen(bool? anwesend) => Anwesend = anwesend;

    internal void PunkteSetzen(Guid aufgabeId, decimal? punkte)
    {
        var b = bewertungen.FirstOrDefault(x => x.AufgabeId == aufgabeId);
        if (punkte is null)
        {
            if (b is not null)
            {
                bewertungen.Remove(b);
            }

            return;
        }

        if (b is null)
        {
            bewertungen.Add(new Bewertung(Id, aufgabeId, punkte.Value));
        }
        else
        {
            b.Punkte = punkte.Value;
        }

        Anwesend = true;
    }

    public decimal? PunkteIn(Guid aufgabeId) => bewertungen.FirstOrDefault(b => b.AufgabeId == aufgabeId)?.Punkte;

    public decimal Summe => bewertungen.Sum(b => b.Punkte);
}

/// <summary>
/// Klausur: Teilnehmende aus der Prüfungsliste des Hochschulsystems, Räume und Sitzplätze, Aufgaben, Notenschlüssel, Punkte.
/// </summary>
public sealed class Klausur
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private readonly List<Teilnahme> teilnahmen = [];
    private readonly List<Aufgabe> aufgaben = [];
    private readonly List<KlausurRaum> raeume = [];
    private readonly List<KlausurNotenstufe> notenstufen = [];

    private Klausur()
    {
    }

    public Klausur(Guid verantwortlichKontoId, string titel, string kuerzel, string? pruefungsnummer, string? pruefer, DateTimeOffset? termin)
    {
        Id = Ids.Neu();
        VerantwortlichKontoId = verantwortlichKontoId;
        Aendern(titel, kuerzel, pruefungsnummer, termin, 20m);
        Pruefer = pruefer;
        NotenschluesselSetzen(Notenschluessel.Standard);
    }

    public Guid Id { get; private set; }

    /// <summary>Wer die Klausur angelegt hat und verantwortet.</summary>
    public Guid VerantwortlichKontoId { get; private set; }

    public string Titel { get; private set; } = string.Empty;

    /// <summary>Kurzzeichen für die Klausur-IDs, z. B. „RT“ (2–6 Zeichen, A–Z/0–9).</summary>
    public string Kuerzel { get; private set; } = string.Empty;

    public string? Pruefungsnummer { get; private set; }

    /// <summary>Prüfer laut Prüfungsliste.</summary>
    public string? Pruefer { get; private set; }

    public DateTimeOffset? Termin { get; private set; }

    /// <summary>Wer mehr als so viele Punkte hat, zählt als „aktiv teilnehmend“ (Statistik).</summary>
    public decimal AktivSchwelle { get; private set; }

    /// <summary>Verteilung zufällig statt alphabetisch.</summary>
    public bool ZufaelligVerteilt { get; private set; }

    public DateTimeOffset? Verteilt { get; private set; }

    public IReadOnlyList<Teilnahme> Teilnahmen => teilnahmen;

    public IReadOnlyList<Aufgabe> Aufgaben => aufgaben;

    public IReadOnlyList<KlausurRaum> Raeume => raeume;

    public IReadOnlyList<Notenstufe> Notenstufen => notenstufen.OrderBy(s => s.Mindestpunkte).Select(s => new Notenstufe(s.Mindestpunkte, s.Note)).ToList();

    public IEnumerable<Teilnahme> Angemeldete => teilnahmen.Where(t => t.Angemeldet);

    public IEnumerable<Aufgabe> AufgabenSortiert => aufgaben.OrderBy(a => a.Nr);

    /// <summary>Höchstpunktzahl ohne Sonderpunkte.</summary>
    public decimal Hoechstpunktzahl => aufgaben.Where(a => !a.Sonderpunkte).Sum(a => a.MaxPunkte);

    public void Aendern(string titel, string kuerzel, string? pruefungsnummer, DateTimeOffset? termin, decimal aktivSchwelle)
    {
        if (string.IsNullOrWhiteSpace(titel))
        {
            throw new FachlicherFehler("Bitte einen Titel angeben.");
        }

        var k = (kuerzel ?? string.Empty).Trim().ToUpperInvariant();
        if (k.Length is < 2 or > 6 || !k.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
        {
            throw new FachlicherFehler("Das Kürzel hat 2 bis 6 Zeichen (A–Z, 0–9), z. B. „RT“.");
        }

        if (aktivSchwelle < 0)
        {
            throw new FachlicherFehler("Die Schwelle für „aktiv teilnehmend“ darf nicht negativ sein.");
        }

        if (Kuerzel.Length > 0 && Kuerzel != k && teilnahmen.Count > 0)
        {
            throw new FachlicherFehler("Das Kürzel steckt in den Klausur-IDs und lässt sich nach dem Import nicht mehr ändern.");
        }

        Titel = titel.Trim();
        Kuerzel = k;
        Pruefungsnummer = string.IsNullOrWhiteSpace(pruefungsnummer) ? null : pruefungsnummer.Trim();
        Termin = termin;
        AktivSchwelle = aktivSchwelle;
    }

    // ------------------------------------------------------------------ Teilnehmende
    /// <summary>Gleicht die Teilnehmenden mit einer Prüfungsliste ab (Schlüssel: Matrikelnummer). Nichts wird gelöscht.</summary>
    public ImportErgebnis Abgleichen(IReadOnlyList<Listenzeile> zeilen)
    {
        ArgumentNullException.ThrowIfNull(zeilen);
        var doppelt = zeilen.GroupBy(z => z.Matrikelnummer).FirstOrDefault(g => g.Count() > 1);
        if (doppelt is not null)
        {
            throw new FachlicherFehler($"Die Matrikelnummer {doppelt.Key} steht mehrfach in der Liste.");
        }

        int neu = 0, geaendert = 0, wieder = 0;
        var gesehen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var z in zeilen)
        {
            gesehen.Add(z.Matrikelnummer);
            var t = teilnahmen.FirstOrDefault(x => x.Matrikelnummer == z.Matrikelnummer);
            if (t is null)
            {
                teilnahmen.Add(new Teilnahme(Id, NeuerCode(), z.Matrikelnummer, z.Nachname, z.Vorname, z.Email, z.Zeile));
                neu++;
                continue;
            }

            if (t.Uebernehmen(z.Nachname, z.Vorname, z.Email, z.Zeile))
            {
                geaendert++;
            }

            if (!t.Angemeldet)
            {
                t.AngemeldetSetzen(true);
                wieder++;
            }
        }

        var abgemeldet = 0;
        foreach (var t in teilnahmen.Where(t => t.Angemeldet && !gesehen.Contains(t.Matrikelnummer)))
        {
            t.AngemeldetSetzen(false);
            t.Setzen(null, null);
            abgemeldet++;
        }

        if (neu + wieder + abgemeldet > 0)
        {
            Verteilt = null;   // Sitzplätze neu verteilen
        }

        return new ImportErgebnis(neu, geaendert, abgemeldet, wieder);
    }

    private string NeuerCode()
    {
        while (true)
        {
            var zufall = RandomNumberGenerator.GetBytes(4);
            var zeichen = new char[4];
            for (var i = 0; i < 4; i++)
            {
                zeichen[i] = Crockford[zufall[i] % 32];
            }

            var code = $"{Kuerzel}-{new string(zeichen)}{Pruefzeichen(new string(zeichen))}";
            if (teilnahmen.All(t => t.Code != code))
            {
                return code;
            }
        }
    }

    private static char Pruefzeichen(string teil)
    {
        var summe = 0;
        for (var i = 0; i < teil.Length; i++)
        {
            summe += Crockford.IndexOf(teil[i], StringComparison.Ordinal) * (i + 2);
        }

        return Crockford[summe % 32];
    }

    /// <summary>Klausur-ID tolerant vergleichen (Groß/klein, O→0, I/L→1, Leer- und Bindestriche egal).</summary>
    public Teilnahme? NachCode(string eingabe)
    {
        static string Norm(string s) => new(s.ToUpperInvariant().Replace('O', '0').Replace('I', '1').Replace('L', '1')
            .Where(char.IsAsciiLetterOrDigit).ToArray());

        if (string.IsNullOrWhiteSpace(eingabe))
        {
            return null;
        }

        var n = Norm(eingabe);
        return teilnahmen.FirstOrDefault(t => Norm(t.Code) == n || Norm(t.Code)[Kuerzel.Length..] == n);
    }

    /// <summary>Prüft eine Klausur-ID auf Tippfehler (Prüfzeichen).</summary>
    public static bool CodeGueltig(string code)
    {
        var teil = code.Split('-').LastOrDefault() ?? "";
        return teil.Length == 5 && teil.All(c => Crockford.Contains(c, StringComparison.Ordinal)) && Pruefzeichen(teil[..4]) == teil[4];
    }

    // ------------------------------------------------------------------ Räume und Sitzplätze
    public void RaeumeWaehlen(IReadOnlyList<Raum> gewaehlt)
    {
        ArgumentNullException.ThrowIfNull(gewaehlt);
        raeume.Clear();
        for (var i = 0; i < gewaehlt.Count; i++)
        {
            raeume.Add(new KlausurRaum(Id, gewaehlt[i].Id, i));
        }

        Verteilt = null;
    }

    public Freigabe KannVerteilen(IReadOnlyList<Raum> gewaehlteRaeume)
    {
        ArgumentNullException.ThrowIfNull(gewaehlteRaeume);
        var anzahl = Angemeldete.Count();
        var plaetze = gewaehlteRaeume.Sum(r => r.Plaetze);
        return anzahl == 0 ? Freigabe.Nein("Es sind noch keine Teilnehmenden importiert.")
            : gewaehlteRaeume.Count == 0 ? Freigabe.Nein("Bitte zuerst Räume auswählen.")
            : plaetze < anzahl ? Freigabe.Nein($"Die Plätze reichen nicht: {anzahl} Teilnehmende, {plaetze} Plätze. Bitte weitere Räume wählen.")
            : Freigabe.Ja;
    }

    /// <summary>
    /// Verteilt die Angemeldeten der Reihe nach auf die gewählten Räume (alphabetisch nach Name oder zufällig, aber reproduzierbar).
    /// Räume werden gleichmäßig nach Kapazität ausgelastet; innerhalb eines Raums folgen die Plätze der Tischnummerierung und
    /// werden gestreut, wenn der Raum nicht voll ist.
    /// </summary>
    public void Verteilen(IReadOnlyList<Raum> gewaehlteRaeume, bool zufaellig, DateTimeOffset jetzt)
    {
        ArgumentNullException.ThrowIfNull(gewaehlteRaeume);
        KannVerteilen(gewaehlteRaeume).Sicherstellen();
        var personen = zufaellig
            ? Angemeldete.OrderBy(t => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id.ToString("N") + t.Code))), StringComparer.Ordinal).ToList()
            : Angemeldete.OrderBy(t => t.Nachname, StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), true))
                .ThenBy(t => t.Vorname, StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), true)).ThenBy(t => t.Matrikelnummer).ToList();

        // Gleichmäßige Auslastung: Anteil je Raum nach Kapazität, Rest der Reihe nach
        var gesamt = gewaehlteRaeume.Sum(r => r.Plaetze);
        var anteile = gewaehlteRaeume.Select(r => (int)Math.Floor((decimal)personen.Count * r.Plaetze / gesamt)).ToArray();
        for (var i = 0; anteile.Sum() < personen.Count; i = (i + 1) % anteile.Length)
        {
            if (anteile[i] < gewaehlteRaeume[i].Plaetze)
            {
                anteile[i]++;
            }
        }

        var p = 0;
        for (var r = 0; r < gewaehlteRaeume.Count; r++)
        {
            // Plätze gestreut nach der Tischnummerierung des Raums: freie Plätze verteilen sich gleichmäßig.
            foreach (var platz in Raum.Streuen(gewaehlteRaeume[r].Sitzplaetze(), anteile[r]))
            {
                personen[p++].Setzen(gewaehlteRaeume[r].Id, platz.Nummer);
            }
        }

        foreach (var t in teilnahmen.Where(t => !t.Angemeldet))
        {
            t.Setzen(null, null);
        }

        ZufaelligVerteilt = zufaellig;
        Verteilt = jetzt;
    }

    // ------------------------------------------------------------------ Aufgaben, Notenschlüssel, Punkte
    public void AufgabenSetzen(IReadOnlyList<(string Titel, decimal MaxPunkte, bool Sonderpunkte)> liste)
    {
        ArgumentNullException.ThrowIfNull(liste);
        if (liste.Any(a => string.IsNullOrWhiteSpace(a.Titel)))
        {
            throw new FachlicherFehler("Jede Aufgabe braucht einen Titel.");
        }

        if (liste.Any(a => a.MaxPunkte <= 0 || a.MaxPunkte > 1000))
        {
            throw new FachlicherFehler("Die Höchstpunktzahl je Aufgabe liegt zwischen 0 und 1000.");
        }

        for (var i = 0; i < liste.Count; i++)
        {
            var (titel, max, sonder) = liste[i];
            if (i < aufgaben.Count)
            {
                var a = AufgabenSortiert.ElementAt(i);
                if (max < teilnahmen.Select(t => t.PunkteIn(a.Id) ?? 0).DefaultIfEmpty(0).Max())
                {
                    throw new FachlicherFehler($"„{titel}“: Es sind schon mehr als {max} Punkte eingetragen.");
                }

                a.Aendern(i + 1, titel.Trim(), max, sonder);
            }
            else
            {
                aufgaben.Add(new Aufgabe(Id, i + 1, titel.Trim(), max, sonder));
            }
        }

        foreach (var weg in AufgabenSortiert.Skip(liste.Count).ToList())
        {
            if (teilnahmen.Any(t => t.PunkteIn(weg.Id) is not null))
            {
                throw new FachlicherFehler($"„{weg.Titel}“ hat schon Punkte und lässt sich nicht entfernen.");
            }

            aufgaben.Remove(weg);
        }
    }

    public void NotenschluesselSetzen(IReadOnlyList<Notenstufe> stufen)
    {
        if (Notenschluessel.Beanstandung(stufen) is { } fehler)
        {
            throw new FachlicherFehler(fehler);
        }

        notenstufen.Clear();
        notenstufen.AddRange(stufen.OrderBy(s => s.Mindestpunkte).Select(s => new KlausurNotenstufe(Id, s.Mindestpunkte, s.Note)));
    }

    public void PunkteSetzen(Guid teilnahmeId, Guid aufgabeId, decimal? punkte)
    {
        var t = teilnahmen.FirstOrDefault(x => x.Id == teilnahmeId) ?? throw new FachlicherFehler("Teilnahme nicht gefunden.");
        var a = aufgaben.FirstOrDefault(x => x.Id == aufgabeId) ?? throw new FachlicherFehler("Aufgabe nicht gefunden.");
        if (punkte is { } p && (p < 0 || p > a.MaxPunkte))
        {
            throw new FachlicherFehler($"{a.Titel}: zwischen 0 und {a.MaxPunkte} Punkte.");
        }

        if (punkte is { } q && q * 4 != Math.Floor(q * 4))
        {
            throw new FachlicherFehler("Punkte bitte in Schritten von höchstens 0,25.");
        }

        t.PunkteSetzen(a.Id, punkte);
    }

    public void AnwesendSetzen(Guid teilnahmeId, bool? anwesend)
    {
        var t = teilnahmen.FirstOrDefault(x => x.Id == teilnahmeId) ?? throw new FachlicherFehler("Teilnahme nicht gefunden.");
        if (anwesend == false && t.Bewertungen.Count > 0)
        {
            throw new FachlicherFehler("Für diese Person sind Punkte eingetragen; bitte zuerst die Punkte löschen.");
        }

        t.AnwesendSetzen(anwesend);
    }

    /// <summary>Note einer anwesenden Person (<c>null</c> = nicht anwesend bzw. nicht erfasst).</summary>
    public decimal? NoteVon(Teilnahme t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return t.Anwesend == true ? Notenschluessel.NoteFuer(Notenstufen, t.Summe) : null;
    }
}

public sealed class KlausurRaum
{
    private KlausurRaum()
    {
    }

    internal KlausurRaum(Guid klausurId, Guid raumId, int reihenfolge) => (Id, KlausurId, RaumId, Reihenfolge) = (Ids.Neu(), klausurId, raumId, reihenfolge);

    public Guid Id { get; private set; }

    public Guid KlausurId { get; private set; }

    public Guid RaumId { get; private set; }

    public int Reihenfolge { get; private set; }
}

public sealed class KlausurNotenstufe
{
    private KlausurNotenstufe()
    {
    }

    internal KlausurNotenstufe(Guid klausurId, decimal mindestpunkte, decimal note) => (Id, KlausurId, Mindestpunkte, Note) = (Ids.Neu(), klausurId, mindestpunkte, note);

    public Guid Id { get; private set; }

    public Guid KlausurId { get; private set; }

    public decimal Mindestpunkte { get; private set; }

    public decimal Note { get; private set; }
}

/// <summary>Zeile der Prüfungsliste.</summary>
public sealed record Listenzeile(int Zeile, string Matrikelnummer, string Nachname, string Vorname, string? Email);

public sealed record ImportErgebnis(int Neu, int Geaendert, int Abgemeldet, int Wieder)
{
    public override string ToString() => $"{Neu} neu, {Geaendert} geändert, {Abgemeldet} abgemeldet, {Wieder} wieder angemeldet";
}
