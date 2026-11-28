using System.Net;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProfOMat.Application;
using ProfOMat.Application.Betrieb;
using ProfOMat.Application.Drittpruefung;
using ProfOMat.Application.Katalog;
using ProfOMat.Infrastructure.Datenbank;
using ProfOMat.Infrastructure.Dokumente;
using ProfOMat.Infrastructure.Sicherheit;
using Quartz;

namespace ProfOMat.Infrastructure;

public static class Registrierung
{
    public static IServiceCollection AddProfOMatInfrastruktur(this IServiceCollection dienste, IConfiguration konfiguration, string inhaltsordner,
        bool hintergrundjobs)
    {
        ArgumentNullException.ThrowIfNull(konfiguration);
        var verbindung = Datenbankverbindung(konfiguration.GetConnectionString("ProfOMat"));
        dienste.AddDbContext<ProfOMatDbContext>(o => o.UseProfOMatPostgres(verbindung.ConnectionString));
        dienste.AddScoped<IDaten, Daten>();
        // Feldverschlüsselung (AES-256-GCM): Schlüssel aus systemd-Credential, Docker Secret oder dem Schlüsselordner
        Feldverschluesselung.Einrichten(Feldverschluesselung.Laden(konfiguration, Path.Combine(inhaltsordner, konfiguration["Speicher:Schluessel"] ?? "data/schluessel")));
        dienste.AddScoped<ISicherheitsprotokoll, Sicherheitsprotokoll>();
        dienste.AddProfOMatIdentitaet(konfiguration);
        dienste.AddSingleton<ISystemsiegel>(new Systemsiegel(Path.Combine(inhaltsordner, konfiguration["Speicher:Schluessel"] ?? "data/schluessel")));
        dienste.AddSingleton<IAntragPdf, AntragPdf>();
        dienste.AddSingleton<ProfOMat.Application.Muendlich.IMuendlichProtokollPdf, MuendlichProtokollPdf>();
        dienste.AddSingleton<ProfOMat.Application.Beschriftung.ITextmass, Textmass>();
        dienste.AddSingleton<ProfOMat.Application.Beschriftung.IStreifenPdf, StreifenPdf>();
        dienste.AddSingleton<ProfOMat.Application.Klausuren.IKlausurDokumente, KlausurDokumente>();
        dienste.AddSingleton<ProfOMat.Application.Finanzen.IFinanzDokumente, FinanzDokumente>();
        dienste.AddSingleton<ProfOMat.Application.Finanzen.ISqmListenLeser, SqmListenLeser>();
        dienste.Configure<SmtpOptionen>(konfiguration.GetSection(SmtpOptionen.Abschnitt));
        dienste.AddSingleton<IMailZustellung, SmtpZustellung>();

        if (hintergrundjobs)
        {
            var takt = konfiguration.GetValue("Postausgang:TaktSekunden", 20);
            dienste.AddQuartz(q =>
            {
                q.ScheduleJob<PostausgangJob>(t => t.WithIdentity("postausgang").StartNow()
                    .WithSimpleSchedule(x => x.WithInterval(TimeSpan.FromSeconds(takt)).RepeatForever()));
                q.ScheduleJob<KatalogAbgleichJob>(t => t.WithIdentity("katalog").WithCronSchedule(konfiguration["Katalog:AbgleichCron"] ?? "0 30 3 * * ?"));
                q.ScheduleJob<VerfallJob>(t => t.WithIdentity("verfall").WithCronSchedule("0 15 * * * ?"));
                q.ScheduleJob<FinanzErinnerungJob>(t => t.WithIdentity("finanzerinnerung").WithCronSchedule(konfiguration["Finanzen:ErinnerungCron"] ?? "0 0 7 * * ?"));
            });
            dienste.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
        }

        return dienste;
    }

    /// <summary>Verbindung zum lokalen Entwicklungscontainer (deploy/docker-compose.yml); nur in appsettings.Development.json und Tests.</summary>
    public const string Entwicklungsverbindung = "Host=127.0.0.1;Port=5443;Database=profomat;Username=profomat;Password=entwicklung";

    /// <summary>Prüft die Verbindungszeichenfolge „ConnectionStrings:ProfOMat“ (PostgreSQL) mit verständlicher Meldung.</summary>
    public static Npgsql.NpgsqlConnectionStringBuilder Datenbankverbindung(string? verbindung)
    {
        if (string.IsNullOrWhiteSpace(verbindung))
        {
            throw new InvalidOperationException("ConnectionStrings:ProfOMat fehlt. Entwicklung: docker compose -f deploy/docker-compose.yml up -d "
                                                + "(appsettings.Development.json kennt die Verbindung); Server: /etc/profomat/profomat.env (Schritt postgresql.sh).");
        }

        if (verbindung.TrimStart().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ConnectionStrings:ProfOMat zeigt noch auf eine SQLite-Datei – ProfOMat nutzt seit 11/2026 PostgreSQL. "
                                                + "Alte Daten einmalig übernehmen: ProfOMat.Web --sqlite-uebernehmen <datei> (docs/deploy.md).");
        }

        try
        {
            return new Npgsql.NpgsqlConnectionStringBuilder(verbindung);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("ConnectionStrings:ProfOMat ist keine gültige PostgreSQL-Verbindung (Host=…;Database=…). "
                                                + "Bei systemd den ganzen Wert in Anführungszeichen setzen.", ex);
        }
    }

    /// <summary>PostgreSQL mit den Einstellungen der Anwendung (auch für Werkzeuge und Tests).</summary>
    public static DbContextOptionsBuilder UseProfOMatPostgres(this DbContextOptionsBuilder o, string verbindung)
    {
        ArgumentNullException.ThrowIfNull(o);
        return o.UseNpgsql(verbindung, n => n.MigrationsHistoryTable("__EFMigrationsHistory"))
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, ModellJeAnbieter>();
    }

    /// <inheritdoc cref="UseProfOMatPostgres(DbContextOptionsBuilder, string)"/>
    public static DbContextOptionsBuilder<ProfOMatDbContext> UseProfOMatPostgres(this DbContextOptionsBuilder<ProfOMatDbContext> o, string verbindung)
        => (DbContextOptionsBuilder<ProfOMatDbContext>)UseProfOMatPostgres((DbContextOptionsBuilder)o, verbindung);
}

/// <summary>SMTP-Einstellungen (Abschnitt „Mail“). Ohne Host wird nur simuliert (Entwicklung). Passwort nur per Umgebungsvariable.</summary>
public sealed class SmtpOptionen
{
    public const string Abschnitt = "Mail";

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public bool StartTls { get; set; } = true;

    public string? Benutzer { get; set; }

    public string? Passwort { get; set; }

    public string Absender { get; set; } = "profomat@example.invalid";

    public string Absendername { get; set; } = "ProfOMat";
}

internal sealed partial class SmtpZustellung(IOptions<SmtpOptionen> optionen, ILogger<SmtpZustellung> log) : IMailZustellung
{
    public async Task<bool> ZustellenAsync(string an, string betreff, string text, (string Name, string Typ, byte[] Inhalt)? anhang, CancellationToken abbruch = default)
    {
        var o = optionen.Value;
        if (string.IsNullOrWhiteSpace(o.Host))
        {
            LogSimuliert(log, betreff);
            return false;
        }

        using var nachricht = new MailMessage(new MailAddress(o.Absender, o.Absendername), new MailAddress(an))
        {
            Subject = betreff,
            Body = text,
            BodyEncoding = System.Text.Encoding.UTF8,
            SubjectEncoding = System.Text.Encoding.UTF8,
        };
        using var strom = anhang is { } a ? new MemoryStream(a.Inhalt) : null;
        if (anhang is { } x && strom is not null)
        {
            nachricht.Attachments.Add(new Attachment(strom, x.Name, x.Typ));
        }

        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.StartTls };
        if (!string.IsNullOrEmpty(o.Benutzer))
        {
            client.Credentials = new NetworkCredential(o.Benutzer, o.Passwort);
        }

        await client.SendMailAsync(nachricht, abbruch);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Mail simuliert (kein SMTP-Host): {Betreff}")]
    private static partial void LogSimuliert(ILogger logger, string betreff);
}

[DisallowConcurrentExecution]
internal sealed class PostausgangJob(IServiceScopeFactory bereiche) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        await using var bereich = bereiche.CreateAsyncScope();
        var d = bereich.ServiceProvider;
        await d.GetRequiredService<Postausgang>().ZustellenAsync(d.GetRequiredService<IMailZustellung>(), cancellationToken);
    }
}

[DisallowConcurrentExecution]
internal sealed class KatalogAbgleichJob(IServiceScopeFactory bereiche) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        await using var bereich = bereiche.CreateAsyncScope();
        await bereich.ServiceProvider.GetRequiredService<KatalogDienst>().AbgleichenAsync(alsAutomatik: true, cancellationToken);
    }
}

[DisallowConcurrentExecution]
internal sealed class VerfallJob(IServiceScopeFactory bereiche) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        await using var bereich = bereiche.CreateAsyncScope();
        await bereich.ServiceProvider.GetRequiredService<DrittpruefungDienst>().VerfallenLassenAsync(cancellationToken);
    }
}

/// <summary>Täglich: Fristerinnerungen der Laborteams (FA-712).</summary>
[DisallowConcurrentExecution]
internal sealed class FinanzErinnerungJob(IServiceScopeFactory bereiche) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        await using var bereich = bereiche.CreateAsyncScope();
        await bereich.ServiceProvider.GetRequiredService<ProfOMat.Application.Finanzen.FinanzDienst>().ErinnerungenVersendenAsync(cancellationToken);
    }
}
