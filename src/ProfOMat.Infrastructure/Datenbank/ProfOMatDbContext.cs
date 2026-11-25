using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Drittpruefung;
using ProfOMat.Domain.Katalog;
using ProfOMat.Domain.Klausuren;
using ProfOMat.Domain.Konten;
using ProfOMat.Domain.Muendlich;
using ProfOMat.Domain.Beschriftung;
using ProfOMat.Domain.Hausarbeiten;
using ProfOMat.Domain.Gefache;
using ProfOMat.Domain.Finanzen;

namespace ProfOMat.Infrastructure.Datenbank;

/// <summary>
/// EF Core + SQLite. Entwicklungsphase: Schema per EnsureCreated (wie VibeVHS); vor dem ersten Produktivbetrieb werden
/// EF-Migrationen eingeführt. Zeitpunkte als UTC-Millisekunden (sortier- und vergleichbar in SQLite), Enums als Text.
/// </summary>
public sealed class ProfOMatDbContext(DbContextOptions<ProfOMatDbContext> optionen) : DbContext(optionen)
{
    /// <summary>Erhöhen, wenn sich das Modell ändert: eine ältere Datei wird dann beim Start abgelehnt (statt still falsch zu laufen).</summary>
    public const int Schemaversion = 17;

    /// <summary>Tabellen, die mit Version 10 dazugekommen sind (Aufwertung 9 → 10 legt nur diese an).</summary>
    internal static readonly string[] TabellenAbVersion10 = ["Hausarbeiten", "HausarbeitThemen", "HausarbeitPersonen", "HausarbeitTeams", "Teammitglieder", "Mailentwuerfe", "Mailsignaturen", "MailsignaturBilder"];

    /// <summary>Tabellen, die mit Version 11 dazugekommen sind (Gefache, Nerd Tools).</summary>
    internal static readonly string[] TabellenAbVersion11 = ["Gefache"];

    /// <summary>Tabellen, die mit Version 13 dazugekommen sind (Finanzen, Iteration 6).</summary>
    internal static readonly string[] TabellenAbVersion13 = ["Laborteams", "Labormitglieder", "FinanzToepfe", "FinanzBuchungen", "Kostenstellenstaende", "Massnahmenstaende"];

    /// <summary>Tabellen, die mit Version 14 dazugekommen sind (Finanzen: Planung).</summary>
    internal static readonly string[] TabellenAbVersion14 = ["GeplanteAusgaben", "Ausgabenanteile", "Auslagenabrechnungen", "Auslagenpositionen", "Hilfskraefte", "Hilfskraftanteile", "Stundensaetze"];

    /// <summary>Tabellen, die mit Version 15 dazugekommen sind (Buchungsnotizen, Einzelbuchungen, Budgetprognose, Erinnerungen).</summary>
    internal static readonly string[] TabellenAbVersion15 = ["Buchungsnotizen", "Einzelbuchungen", "Budgetprognosen", "Prognoseveranstaltungen", "Finanzerinnerungen"];

    protected override void ConfigureConventions(ModelConfigurationBuilder konventionen)
    {
        ArgumentNullException.ThrowIfNull(konventionen);
        konventionen.Properties<DateTimeOffset>().HaveConversion<UtcMillisekunden>();
        konventionen.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        ArgumentNullException.ThrowIfNull(m);

        m.Entity<Konto>(e =>
        {
            e.ToTable("Konten");
            e.HasKey(k => k.Id);
            e.HasIndex(k => k.Kennung).IsUnique();
            e.Ignore(k => k.Anzeigename);
            e.Ignore(k => k.Sortiername);
            e.HasMany(k => k.Rollen).WithOne().HasForeignKey(r => r.KontoId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(k => k.Rollen).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
            e.HasMany(k => k.Schluessel).WithOne().HasForeignKey(s => s.KontoId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(k => k.Schluessel).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<KontoRolle>(e =>
        {
            e.ToTable("KontoRollen");
            e.HasKey(r => new { r.KontoId, r.Rolle });
        });
        m.Entity<HinterlegterSchluessel>(e =>
        {
            e.ToTable("Signaturschluessel");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Fingerabdruck);
        });

        m.Ignore<Katalogeintrag>();   // keine gemeinsame Tabelle: Studiengänge und Prüfungen sind je eigene Wurzeln
        m.Entity<Fakultaet>(e =>
        {
            e.ToTable("Fakultaeten");
            e.HasKey(f => f.Id);
            e.HasIndex(f => f.Kuerzel).IsUnique();
        });
        m.Entity<Studiengang>(e =>
        {
            e.ToTable("Studiengaenge");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Schluessel).IsUnique();
            e.HasIndex(s => s.AltSeit);
            e.Ignore(s => s.Anzeige);
            e.Ignore(s => s.IstAlt);
            e.HasOne<Fakultaet>().WithMany().HasForeignKey(s => s.FakultaetId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Pruefung>(e =>
        {
            e.ToTable("Pruefungen");
            e.HasKey(p => p.Id);
            e.HasIndex(p => new { p.StudiengangId, p.Schluessel }).IsUnique();
            e.Ignore(p => p.Anzeige);
            e.Ignore(p => p.IstAlt);
            e.HasOne<Studiengang>().WithMany().HasForeignKey(p => p.StudiengangId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<Antrag>(e =>
        {
            e.ToTable("Drittpruefungen");
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.Nummer).IsUnique();
            e.HasIndex(a => a.Status);
            e.HasIndex(a => a.ErstpruefendeKontoId);
            e.Ignore(a => a.Name);
            e.Ignore(a => a.GueltigeSignaturen);
            e.Ignore(a => a.LetzteGueltigeSignatur);
            e.Ignore(a => a.AktuelleFestlegung);
            e.HasOne<Studiengang>().WithMany().HasForeignKey(a => a.StudiengangId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Pruefung>().WithMany().HasForeignKey(a => a.PruefungId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Konto>().WithMany().HasForeignKey(a => a.ErstpruefendeKontoId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.Signaturen).WithOne().HasForeignKey(s => s.AntragId).OnDelete(DeleteBehavior.Restrict);
            e.Navigation(a => a.Signaturen).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<Signatur>(e =>
        {
            e.ToTable("Signaturen");
            e.HasKey(s => s.Id);
            e.Ignore(s => s.SiegelText);
        });

        m.Entity<Raum>(e =>
        {
            e.ToTable("Raeume");
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Kuerzel).IsUnique();
            e.Ignore(r => r.Anzeige);
            e.Ignore(r => r.HatGrundriss);
            e.HasMany(r => r.Elemente).WithOne().HasForeignKey(x => x.RaumId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(r => r.Elemente).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<Raumelement>(e =>
        {
            e.ToTable("Raumelemente");
            e.HasKey(x => x.Id);
        });
        m.Entity<Klausur>(e =>
        {
            e.ToTable("Klausuren");
            e.HasKey(k => k.Id);
            e.HasIndex(k => k.VerantwortlichKontoId);
            e.Ignore(k => k.Notenstufen);
            e.Ignore(k => k.Angemeldete);
            e.Ignore(k => k.AufgabenSortiert);
            e.Ignore(k => k.Hoechstpunktzahl);
            e.HasOne<Konto>().WithMany().HasForeignKey(k => k.VerantwortlichKontoId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(k => k.Teilnahmen).WithOne().HasForeignKey(t => t.KlausurId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(k => k.Aufgaben).WithOne().HasForeignKey(a => a.KlausurId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(k => k.Raeume).WithOne().HasForeignKey(r => r.KlausurId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany<KlausurNotenstufe>("notenstufen").WithOne().HasForeignKey(s => s.KlausurId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(k => k.Teilnahmen).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(k => k.Aufgaben).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(k => k.Raeume).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation("notenstufen").AutoInclude();
        });
        m.Entity<Teilnahme>(e =>
        {
            e.ToTable("Teilnahmen");
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.KlausurId, t.Matrikelnummer }).IsUnique();
            e.HasIndex(t => new { t.KlausurId, t.Code }).IsUnique();
            e.Ignore(t => t.Name);
            e.Ignore(t => t.Summe);
            e.HasMany(t => t.Bewertungen).WithOne().HasForeignKey(b => b.TeilnahmeId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(t => t.Bewertungen).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<Aufgabe>(e =>
        {
            e.ToTable("Aufgaben");
            e.HasKey(a => a.Id);
        });
        m.Entity<Bewertung>(e =>
        {
            e.ToTable("Bewertungen");
            e.HasKey(b => new { b.TeilnahmeId, b.AufgabeId });
        });
        m.Entity<KlausurRaum>(e =>
        {
            e.ToTable("KlausurRaeume");
            e.HasKey(r => r.Id);
        });
        m.Entity<KlausurNotenstufe>(e =>
        {
            e.ToTable("Notenstufen");
            e.HasKey(s => s.Id);
        });

        m.Entity<MuendlichePruefung>(e =>
        {
            e.ToTable("MuendlichePruefungen");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.KontoId);
            e.Ignore(p => p.FragenSortiert);
            e.Ignore(p => p.Erreicht);
            e.Ignore(p => p.MoeglichBewertet);
            e.Ignore(p => p.MoeglichGesamt);
            e.Ignore(p => p.Aktuell);
            e.Ignore(p => p.OhneOffene);
            e.Ignore(p => p.Notenvorschlag);
            e.Ignore(p => p.WeichtAb);
            e.Property(p => p.Bezeichnung).HasMaxLength(MuendlichePruefung.BezeichnungMaxLaenge);
            e.Property(p => p.Pruefling).HasMaxLength(MuendlichePruefung.NameMaxLaenge);
            e.Property(p => p.Matrikelnummer).HasMaxLength(MuendlichePruefung.MatrikelnummerMaxLaenge);
            e.Property(p => p.Studiengang).HasMaxLength(MuendlichePruefung.NameMaxLaenge);
            e.Property(p => p.ErstpruefendeName).HasMaxLength(MuendlichePruefung.NameMaxLaenge);
            e.Property(p => p.ZweitpruefendeName).HasMaxLength(MuendlichePruefung.NameMaxLaenge);
            e.Ignore(p => p.HatErstpruefung);
            e.HasOne<Konto>().WithMany().HasForeignKey(p => p.ErstpruefendeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Konto>().WithMany().HasForeignKey(p => p.ZweitpruefendeId).OnDelete(DeleteBehavior.Restrict);
            e.Property(p => p.Abweichungsbegruendung).HasMaxLength(MuendlichePruefung.BegruendungMaxLaenge);
            e.HasOne<Konto>().WithMany().HasForeignKey(p => p.KontoId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(p => p.Fragen).WithOne().HasForeignKey(f => f.PruefungId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(p => p.Fragen).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<GestellteFrage>(e =>
        {
            e.ToTable("GestellteFragen");
            e.HasKey(f => f.Id);
            e.Ignore(f => f.Stand);
            e.Property(f => f.Nummer).HasMaxLength(GestellteFrage.NummerMaxLaenge);
            e.Property(f => f.Bemerkung).HasMaxLength(GestellteFrage.BemerkungMaxLaenge);
        });

        m.Entity<Einladung>(e =>
        {
            e.ToTable("Einladungen");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Ignore(x => x.RollenListe);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Rollen).HasMaxLength(200);
            e.Property(x => x.Notiz).HasMaxLength(Einladung.NotizMaxLaenge);
            e.HasOne<Konto>().WithMany().HasForeignKey(x => x.KontoId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<Schaltschrank>(e =>
        {
            e.ToTable("Schaltschraenke");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.KontoId);
            e.Ignore(s => s.FelderSortiert);
            e.Ignore(s => s.AlleGeraete);
            e.Ignore(s => s.ReiheBreiteMm);
            e.Property(s => s.Bezeichnung).HasMaxLength(Schaltschrank.BezeichnungMaxLaenge);
            e.HasOne<Konto>().WithMany().HasForeignKey(s => s.KontoId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(s => s.Felder).WithOne().HasForeignKey(f => f.SchrankId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(s => s.Felder).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<Feld>(e =>
        {
            e.ToTable("SchrankFelder");
            e.HasKey(f => f.Id);
            e.Property(f => f.Bezeichnung).HasMaxLength(Feld.BezeichnungMaxLaenge);
            e.HasMany(f => f.Geraete).WithOne().HasForeignKey(g => g.FeldId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(f => f.Geraete).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<Geraet>(e =>
        {
            e.ToTable("SchrankGeraete");
            e.HasKey(g => g.Id);
            e.Ignore(g => g.Ende);
            e.Property(g => g.Kennzeichen).HasMaxLength(Geraet.KennzeichenMaxLaenge);
            e.Property(g => g.Text).HasMaxLength(Geraet.TextMaxLaenge);
            e.Property(g => g.Zusatz).HasMaxLength(Geraet.TextMaxLaenge);
            e.Property(g => g.Hintergrund).HasMaxLength(7);
            e.Property(g => g.Schrift).HasMaxLength(7);
        });

        m.Entity<Gefach>(e =>
        {
            e.ToTable("Gefache");
            e.HasKey(g => g.Id);
            e.HasIndex(g => g.KontoId);
            e.Ignore(g => g.Angaben);
            e.Property(g => g.Bezeichnung).HasMaxLength(Gefach.BezeichnungMaxLaenge);
            e.Property(g => g.LaengeMasse).HasMaxLength(Teilung.MasseMaxLaenge);
            e.Property(g => g.BreiteMasse).HasMaxLength(Teilung.MasseMaxLaenge);
            e.HasOne<Konto>().WithMany().HasForeignKey(g => g.KontoId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<Hausarbeit>(e =>
        {
            e.ToTable("Hausarbeiten");
            e.HasKey(h => h.Id);
            e.HasIndex(h => h.KontoId);
            e.Ignore(h => h.ThemenSortiert);
            e.Ignore(h => h.TeamsSortiert);
            e.Ignore(h => h.PersonenSortiert);
            e.Ignore(h => h.OhneTeam);
            e.Ignore(h => h.NaechsteThemennummer);
            e.Ignore(h => h.Gespraechstage);
            e.Ignore(h => h.BetreffVorlage);
            e.Ignore(h => h.TextVorlage);
            e.Ignore(h => h.MitTermin);
            e.Ignore(h => h.OhneTermin);
            e.Ignore(h => h.BestaetigungBetreffVorlage);
            e.Ignore(h => h.BestaetigungTextVorlage);
            e.Property(h => h.GespraechOrt).HasMaxLength(Hausarbeit.OrtMaxLaenge);
            e.Property(h => h.Titel).HasMaxLength(Hausarbeit.TitelMaxLaenge);
            e.HasOne<Konto>().WithMany().HasForeignKey(h => h.KontoId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(h => h.Themen).WithOne().HasForeignKey(t => t.HausarbeitId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(h => h.Personen).WithOne().HasForeignKey(p => p.HausarbeitId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(h => h.Teams).WithOne().HasForeignKey(t => t.HausarbeitId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(h => h.Themen).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(h => h.Personen).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(h => h.Teams).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<HausarbeitThema>(e =>
        {
            e.ToTable("HausarbeitThemen");
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.HausarbeitId, t.Nr }).IsUnique();
            e.Ignore(t => t.Anzeige);
            e.Property(t => t.Titel).HasMaxLength(HausarbeitThema.TitelMaxLaenge);
            e.Property(t => t.Beschreibung).HasMaxLength(HausarbeitThema.BeschreibungMaxLaenge);
        });
        m.Entity<Bearbeitende>(e =>
        {
            e.ToTable("HausarbeitPersonen");
            e.HasKey(p => p.Id);
            e.HasIndex(p => new { p.HausarbeitId, p.Matrikelnummer }).IsUnique();
            e.Ignore(p => p.Name);
            e.Ignore(p => p.VollerName);
        });
        m.Entity<Team>(e =>
        {
            e.ToTable("HausarbeitTeams");
            e.HasKey(t => t.Id);
            e.Ignore(t => t.Wuensche);
            e.Property(t => t.Notiz).HasMaxLength(Team.NotizMaxLaenge);
            e.HasMany(t => t.Mitglieder).WithOne().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(t => t.Mitglieder).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<Teammitglied>(e =>
        {
            e.ToTable("Teammitglieder");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PersonId).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasOne<Bearbeitende>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Mailentwurf>(e =>
        {
            e.ToTable("Mailentwuerfe");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.KontoId, x.Erledigt });
            e.HasIndex(x => x.BezugId);
            e.Property(x => x.Betreff).HasMaxLength(Mailentwurf.BetreffMaxLaenge);
            e.Property(x => x.Text).HasMaxLength(Mailentwurf.TextMaxLaenge);
            e.HasOne<Konto>().WithMany().HasForeignKey(x => x.KontoId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<Mailsignatur>(e =>
        {
            e.ToTable("Mailsignaturen");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.KontoId).IsUnique();
            e.Property(x => x.Html).HasMaxLength(Mailsignatur.HtmlMaxLaenge);
            e.Property(x => x.Text).HasMaxLength(Mailsignatur.TextMaxLaenge);
            e.HasOne<Konto>().WithMany().HasForeignKey(x => x.KontoId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Bilder).WithOne().HasForeignKey(b => b.SignaturId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Bilder).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<MailsignaturBild>(e =>
        {
            e.ToTable("MailsignaturBilder");
            e.HasKey(x => x.Id);
            e.Property(x => x.ContentId).HasMaxLength(100);
            e.Property(x => x.Dateiname).HasMaxLength(200);
            e.Property(x => x.Typ).HasMaxLength(40);
        });

        m.Entity<Laborteam>(e =>
        {
            e.ToTable("Laborteams");
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).HasMaxLength(Laborteam.NameMaxLaenge);
            e.HasMany(t => t.Mitglieder).WithOne().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(t => t.Mitglieder).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<Labormitglied>(e =>
        {
            e.ToTable("Labormitglieder");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TeamId, x.KontoId }).IsUnique();
            e.HasIndex(x => x.KontoId);
            e.HasOne<Konto>().WithMany().HasForeignKey(x => x.KontoId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Topf>(e =>
        {
            e.ToTable("FinanzToepfe");
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.TeamId, t.Art, t.Kennung }).IsUnique();
            e.Ignore(t => t.Angaben);
            e.Ignore(t => t.Anzeige);
            e.Ignore(t => t.Bewilligt);
            e.Ignore(t => t.Zweckgebunden);
            e.Ignore(t => t.Stichtag);
            e.Property(t => t.Kennung).HasMaxLength(Topf.KennungMaxLaenge);
            e.Property(t => t.Bezeichnung).HasMaxLength(Topf.TextMaxLaenge);
            e.Property(t => t.Notiz).HasMaxLength(Topf.NotizMaxLaenge);
            e.Property(t => t.Zweck).HasMaxLength(Topf.ZweckMaxLaenge);
            e.Property(t => t.Begruendung).HasMaxLength(Topf.BegruendungMaxLaenge);
            e.HasOne<Laborteam>().WithMany().HasForeignKey(t => t.TeamId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Buchung>(e =>
        {
            e.ToTable("FinanzBuchungen");
            e.HasKey(b => b.Id);
            e.HasIndex(b => new { b.TopfId, b.Jahr, b.Nr });
            e.Ignore(b => b.IstZugang);
            e.Ignore(b => b.Fingerabdruck);
            e.Property(b => b.Text).HasMaxLength(Buchung.TextMaxLaenge);
            e.HasOne<Topf>().WithMany().HasForeignKey(b => b.TopfId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Kostenstellenstand>(e =>
        {
            e.ToTable("Kostenstellenstaende");
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.TopfId, s.Jahr }).IsUnique();
            e.Ignore(s => s.Rest);
            e.HasOne<Topf>().WithMany().HasForeignKey(s => s.TopfId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Massnahmenstand>(e =>
        {
            e.ToTable("Massnahmenstaende");
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.TopfId, s.Stichtag });
            e.HasOne<Topf>().WithMany().HasForeignKey(s => s.TopfId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<GeplanteAusgabe>(e =>
        {
            e.ToTable("GeplanteAusgaben");
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.TeamId, a.Stand });
            e.Ignore(a => a.Betrag);
            e.Ignore(a => a.Offen);
            e.Property(a => a.Bezeichnung).HasMaxLength(GeplanteAusgabe.TextMaxLaenge);
            e.HasOne<Laborteam>().WithMany().HasForeignKey(a => a.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.Anteile).WithOne().HasForeignKey(x => x.AusgabeId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(a => a.Anteile).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<Ausgabenanteil>(e =>
        {
            e.ToTable("Ausgabenanteile");
            e.HasKey(x => x.Id);
            e.HasOne<Topf>().WithMany().HasForeignKey(x => x.TopfId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Auslagenabrechnung>(e =>
        {
            e.ToTable("Auslagenabrechnungen");
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.TeamId, a.Stand });
            e.Ignore(a => a.Summe);
            e.Ignore(a => a.Offen);
            e.Ignore(a => a.Aenderbar);
            e.Ignore(a => a.PositionenSortiert);
            e.HasOne<Laborteam>().WithMany().HasForeignKey(a => a.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Konto>().WithMany().HasForeignKey(a => a.KontoId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.Positionen).WithOne().HasForeignKey(x => x.AbrechnungId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(a => a.Positionen).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<Auslagenposition>(e =>
        {
            e.ToTable("Auslagenpositionen");
            e.HasKey(x => x.Id);
            e.HasOne<Topf>().WithMany().HasForeignKey(x => x.TopfId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Hilfskraft>(e =>
        {
            e.ToTable("Hilfskraefte");
            e.HasKey(h => h.Id);
            e.HasIndex(h => h.TeamId);
            e.Ignore(h => h.Monate);
            e.HasOne<Laborteam>().WithMany().HasForeignKey(h => h.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(h => h.Anteile).WithOne().HasForeignKey(x => x.HilfskraftId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(h => h.Anteile).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<Hilfskraftanteil>(e =>
        {
            e.ToTable("Hilfskraftanteile");
            e.HasKey(x => x.Id);
            e.HasOne<Topf>().WithMany().HasForeignKey(x => x.TopfId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Stundensatz>(e =>
        {
            e.ToTable("Stundensaetze");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TeamId, x.Jahr, x.Art }).IsUnique();
            e.HasOne<Laborteam>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<Buchungsnotiz>(e =>
        {
            e.ToTable("Buchungsnotizen");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TopfId, x.Fingerabdruck }).IsUnique();
            e.Ignore(x => x.Offen);
            e.Ignore(x => x.Leer);
            e.Property(x => x.Fingerabdruck).HasMaxLength(400);
            e.Property(x => x.Kommentar).HasMaxLength(Buchungsnotiz.KommentarMaxLaenge);
            e.Property(x => x.Markierung).HasConversion<int>();
            e.HasOne<Topf>().WithMany().HasForeignKey(x => x.TopfId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Einzelbuchung>(e =>
        {
            e.ToTable("Einzelbuchungen");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TopfId);
            e.HasOne<Topf>().WithMany().HasForeignKey(x => x.TopfId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Budgetprognose>(e =>
        {
            e.ToTable("Budgetprognosen");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TopfId, x.Jahr }).IsUnique();
            e.Ignore(x => x.Lehre);
            e.Ignore(x => x.Verfallen);
            e.Ignore(x => x.Abschlussarbeiten);
            e.Ignore(x => x.Personen);
            e.Ignore(x => x.Summe);
            e.HasOne<Topf>().WithMany().HasForeignKey(x => x.TopfId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Veranstaltungen).WithOne().HasForeignKey(v => v.PrognoseId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Veranstaltungen).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        m.Entity<Prognoseveranstaltung>(e =>
        {
            e.ToTable("Prognoseveranstaltungen");
            e.HasKey(x => x.Id);
        });
        m.Entity<Finanzerinnerung>(e =>
        {
            e.ToTable("Finanzerinnerungen");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TeamId, x.Schluessel }).IsUnique();
            e.Property(x => x.Schluessel).HasMaxLength(300);
            e.HasOne<Laborteam>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<Nachricht>(e =>
        {
            e.ToTable("Postausgang");
            e.HasKey(n => n.Id);
            e.HasIndex(n => n.Versandt);
            e.Ignore(n => n.Aufgegeben);
        });
        m.Entity<Dokument>(e =>
        {
            e.ToTable("Dokumente");
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.AntragId);
        });
        m.Entity<Protokolleintrag>(e =>
        {
            e.ToTable("Protokoll");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Zeitpunkt);
        });
        m.Entity<Schemainfo>(e =>
        {
            e.ToTable("Schemainfo");
            e.HasKey(s => s.Id);
        });

        // Ids vergibt die Anwendung (UUIDv7): neue Einträge in Sammlungen sonst fälschlich als „geändert“ statt „neu“ erkannt.
        foreach (var typ in m.Model.GetEntityTypes())
        {
            if (typ.FindProperty("Id") is { } id && id.ClrType == typeof(Guid))
            {
                id.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }
    }

    /// <summary>DateTimeOffset ↔ UTC-Millisekunden seit 1970.</summary>
    private sealed class UtcMillisekunden() : ValueConverter<DateTimeOffset, long>(
        v => v.ToUnixTimeMilliseconds(),
        v => DateTimeOffset.FromUnixTimeMilliseconds(v));
}

/// <summary>Eine Zeile: Schemaversion der Datei.</summary>
public sealed class Schemainfo
{
    public int Id { get; set; } = 1;

    public int Version { get; set; }
}
