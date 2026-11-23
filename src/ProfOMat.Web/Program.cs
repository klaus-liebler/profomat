using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using ProfOMat.Adapters.Mocks;
using ProfOMat.Application;
using ProfOMat.Application.Drittpruefung;
using ProfOMat.Application.Konten;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Infrastructure;
using ProfOMat.Infrastructure.Datenbank;
using ProfOMat.Web.Components;
using ProfOMat.Web.Sicherheit;
using Radzen;
using Serilog;

var deutsch = CultureInfo.GetCultureInfo("de-DE");
CultureInfo.DefaultThreadCurrentCulture = deutsch;
CultureInfo.DefaultThreadCurrentUICulture = deutsch;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((dienste, log) => log
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(dienste)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: deutsch)
    .WriteTo.File(Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Speicher:Logs"] ?? "logs", "profomat-.log"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, formatProvider: deutsch));

// Unterschriftsbilder (PNG, Base64) laufen über die Blazor-Verbindung: Nachrichtengröße anheben.
builder.Services.AddRazorComponents().AddInteractiveServerComponents().AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024);
builder.Services.AddRadzenComponents();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/anmelden";
        o.AccessDeniedPath = "/anmelden";
        o.Cookie.Name = "profomat.anmeldung";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromHours(10);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<BenutzerKontext>();
builder.Services.AddScoped<IAktuellerBenutzer, AktuellerBenutzer>();
builder.Services.AddScoped<Vorgang>();
builder.Services.AddRateLimits(builder.Configuration);

builder.Services
    .AddProfOMatAnwendung(builder.Configuration)
    .AddProfOMatInfrastruktur(builder.Configuration, builder.Environment.ContentRootPath, builder.Configuration.GetValue("Hintergrundjobs:Aktiv", true))
    .AddProfOMatMocks();

builder.Services.AddDataProtection()
    .SetApplicationName("ProfOMat")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Speicher:Schluessel"] ?? "data/schluessel")));

var hinterProxy = builder.Configuration.GetValue("ReverseProxy:Aktiv", false);
if (hinterProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
        if (builder.Configuration["ReverseProxy:Host"] is { Length: > 0 } host)
        {
            o.AllowedHosts = [host];
        }
    });
}

var app = builder.Build();

await DatenbankInitialisierung.InitialisierenAsync(app.Services);

await using (var bereich = app.Services.CreateAsyncScope())
{
    if (app.Configuration.GetValue("Demodaten:Anlegen", false))
    {
        if (await bereich.ServiceProvider.GetRequiredService<Demodaten>().AnlegenAsync())
        {
            app.Logger.LogInformation("Demodaten angelegt (Konten zur Testanmeldung, Katalog aus dem Mock-Hochschulsystem)");
        }
    }
    else if (app.Configuration["Startkonto:Passwort"] is { Length: > 0 } passwort)
    {
        // Erstes Konto der Systemverwaltung nur aus der Umgebung (Startkonto__Passwort), nie aus dem Repository.
        if (await bereich.ServiceProvider.GetRequiredService<KontoDienst>().StartkontoAnlegenAsync(
                app.Configuration["Startkonto:Kennung"] ?? "verwaltung", app.Configuration["Startkonto:Email"] ?? "verwaltung@example.invalid", passwort))
        {
            app.Logger.LogInformation("Startkonto der Systemverwaltung angelegt");
        }
    }
}

if (hinterProxy)
{
    app.UseForwardedHeaders();
}

if (app.Configuration["PathBase"] is { Length: > 1 } pfadBasis)
{
    app.UsePathBase(pfadBasis);
    // Außerhalb der Pfad-Basis nichts rendern: Sonst setzte z. B. /favicon.ico über die 404-Seite ein zweites Antiforgery-Cookie
    // mit Path=/ und gleichem Namen, und Formulare unter der Pfad-Basis scheiterten mit 400.
    app.Use(async (kontext, weiter) =>
    {
        if (!kontext.Request.PathBase.HasValue)
        {
            kontext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await weiter(kontext);
    });
}

app.UseRouting();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseSicherheitsheader();
app.UseRateLimiter();
app.UseSerilogRequestLogging();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseRequestLocalization("de-DE");
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/gesund", async (ProfOMatDbContext db) => await db.Database.CanConnectAsync()
    ? Results.Text("ok")
    : Results.Text("Datenbank nicht erreichbar", statusCode: StatusCodes.Status503ServiceUnavailable)).AllowAnonymous().DisableRateLimiting();

app.MapPost("/abmelden", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("~/");
});

// PDF für Beschäftigte (Rechte prüft der Dienst)
app.MapGet("/antraege/{id:guid}/pdf", async (Guid id, DrittpruefungDienst dienst, HttpContext http) =>
{
    try
    {
        var dok = await dienst.PdfAsync(id);
        if (dok is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.File(dok.Inhalt, dok.Inhaltstyp, dok.Dateiname);
    }
    catch (FachlicherFehler)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
}).RequireAuthorization();

// PDF für Studierende über ihren geheimen Link
app.MapGet("/drittpruefung/status/{id:guid}/{token}/pdf", async (Guid id, string token, DrittpruefungDienst dienst, HttpContext http) =>
{
    var dok = await dienst.PdfFuerStudierendeAsync(id, token);
    if (dok is null)
    {
        return Results.NotFound();
    }

    http.Response.Headers.CacheControl = "no-store";
    return Results.File(dok.Inhalt, dok.Inhaltstyp, dok.Dateiname);
}).AllowAnonymous();

// Protokoll der mündlichen Prüfung (nur wer es angelegt hat; prüft der Dienst)
app.MapGet("/muendlich/{id:guid}/protokoll.pdf", async (Guid id, ProfOMat.Application.Muendlich.MuendlichDienst dienst, HttpContext http) =>
{
    try
    {
        var (inhalt, name) = await dienst.ProtokollPdfAsync(id);
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { Inline = true, FileName = name }.ToString();
        return Results.Bytes(inhalt, "application/pdf");
    }
    catch (FachlicherFehler ex)
    {
        return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status409Conflict);
    }
}).RequireAuthorization();

// Klausur-Ausgaben (Rechte prüft der Dienst)
app.MapGet("/klausuren/{id:guid}/{datei}", async (Guid id, string datei, ProfOMat.Application.Klausuren.KlausurDienst dienst, HttpContext http) =>
{
    try
    {
        var ergebnis = datei switch
        {
            "serienbrief.csv" => await dienst.SerienbriefAsync(id),
            "noten.csv" => await dienst.NotenCsvAsync(id),
            "sitzlisten.pdf" => await dienst.SitzlistenAsync(id),
            "aushang.pdf" => await dienst.AushangAsync(id),
            "ergebnis.pdf" => await dienst.ErgebnisPdfAsync(id),
            "ergebnis.xlsx" => await dienst.ErgebnisXlsxAsync(id),
            _ => null,
        };
        if (ergebnis is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.File(ergebnis.Inhalt, ergebnis.Typ, ergebnis.Name);
    }
    catch (FachlicherFehler ex)
    {
        return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status409Conflict);
    }
}).RequireAuthorization();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();

/// <summary>Für Tests mit dem echten Host.</summary>
public partial class Program;
