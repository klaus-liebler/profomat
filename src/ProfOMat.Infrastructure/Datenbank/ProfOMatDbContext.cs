using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ProfOMat.Domain.Betrieb;
using ProfOMat.Domain.Drittpruefung;
using ProfOMat.Domain.Katalog;
using ProfOMat.Domain.Klausuren;
using ProfOMat.Domain.Konten;
using ProfOMat.Domain.Muendlich;
using ProfOMat.Domain.Beschriftung;

namespace ProfOMat.Infrastructure.Datenbank;

/// <summary>
/// EF Core + SQLite. Entwicklungsphase: Schema per EnsureCreated (wie VibeVHS); vor dem ersten Produktivbetrieb werden
/// EF-Migrationen eingeführt. Zeitpunkte als UTC-Millisekunden (sortier- und vergleichbar in SQLite), Enums als Text.
/// </summary>
public sealed class ProfOMatDbContext(DbContextOptions<ProfOMatDbContext> optionen) : DbContext(optionen)
{
    /// <summary>Erhöhen, wenn sich das Modell ändert: eine ältere Datei wird dann beim Start abgelehnt (statt still falsch zu laufen).</summary>
    public const int Schemaversion = 8;

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
