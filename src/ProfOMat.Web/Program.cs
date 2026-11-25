using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using ProfOMat.Adapters.Mocks;
using ProfOMat.Application;
using ProfOMat.Application.Drittpruefung;
using ProfOMat.Application.Katalog;
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
        // Gesperrte Konten, neues Passwort oder geänderte Rollen beenden bestehende Sitzungen beim nächsten Aufruf (wie Podium).
        o.Events.OnValidatePrincipal = async ctx =>
        {
            if (!await Sitzung.GueltigAsync(ctx.Principal, ctx.HttpContext.RequestServices, ctx.HttpContext.RequestAborted))
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, Sitzungspruefung>();
builder.Services.AddSingleton<Anmeldebremse>();
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
    else if (await bereich.ServiceProvider.GetRequiredService<KontoDienst>().ErsteinrichtungNoetigAsync())
    {
        // Wie Podium: kein Passwort in Konfiguration oder Umgebung – das erste Konto legt man auf der Anmeldeseite an.
        app.Logger.LogWarning("Noch kein Konto vorhanden – das erste Konto der Systemverwaltung bitte JETZT auf der Anmeldeseite anlegen (Ersteinrichtung).");
    }

    if (await bereich.ServiceProvider.GetRequiredService<KatalogDienst>().ErstabgleichAsync() is { } erstabgleich)
    {
        app.Logger.LogInformation("Katalog war leer – erster Abgleich: {Ergebnis}", erstabgleich);
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

// Beschriftungsstreifen (nur wer den Schrank angelegt hat; prüft der Dienst). r=<FeldId ohne Bindestriche>-<Reihe>,…
app.MapGet("/beschriftung/{id:guid}/streifen.pdf", async (Guid id, string? r, ProfOMat.Application.Beschriftung.BeschriftungDienst dienst, HttpContext http) =>
{
    var auswahl = (r ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(t => t.Split('-') is [var feld, var reihe] && Guid.TryParseExact(feld, "N", out var f) && int.TryParse(reihe, out var n)
            ? new ProfOMat.Application.Beschriftung.ReiheWahl(f, n)
            : null)
        .OfType<ProfOMat.Application.Beschriftung.ReiheWahl>().ToHashSet();
    try
    {
        var (inhalt, name) = await dienst.PdfAsync(id, auswahl);
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { Inline = true, FileName = name }.ToString();
        return Results.Bytes(inhalt, "application/pdf");
    }
    catch (FachlicherFehler ex)
    {
        return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status409Conflict);
    }
}).RequireAuthorization();

// Schnittvorlagen der Gefache: platte-<n>.svg, platte-<n>.dxf, schnittvorlagen.zip (nur wer das Gefach angelegt hat; prüft der Dienst)
app.MapGet("/gefache/{id:guid}/{datei}", async (Guid id, string datei, ProfOMat.Application.Gefache.GefachDienst dienst, HttpContext http) =>
{
    try
    {
        var d = await dienst.DateiAsync(id, datei);
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { Inline = false, FileName = d.Dateiname }.ToString();
        return Results.Bytes(d.Inhalt, d.Typ);
    }
    catch (FachlicherFehler ex)
    {
        return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status409Conflict);
    }
}).RequireAuthorization();

// Aufstellung einer Sammel-Auslagenabrechnung (nur Mitglieder des Laborteams; prüft der Dienst)
app.MapGet("/finanzen/{team:guid}/auslagen/{id:guid}/aufstellung.xlsx", async (Guid team, Guid id, ProfOMat.Application.Finanzen.FinanzDienst dienst, HttpContext http) =>
{
    try
    {
        var d = await dienst.AuslagenXlsxAsync(team, id);
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(d.Inhalt, d.Typ, d.Name);
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

// Mailentwürfe als .eml: das Desktop-Outlook öffnet sie als neue Mail (nur der eigene Entwurf; prüft der Dienst)
app.MapGet("/entwuerfe/{id:guid}/mail.eml", async (Guid id, ProfOMat.Application.Betrieb.Mailentwuerfe dienst, HttpContext http) =>
{
    try
    {
        var (inhalt, name) = await dienst.EmlAsync(id);
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(inhalt, "message/rfc822", name);
    }
    catch (FachlicherFehler ex)
    {
        return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status409Conflict);
    }
}).RequireAuthorization();

app.MapGet("/profil/testmail.eml", async (ProfOMat.Application.Betrieb.Mailsignaturen dienst, HttpContext http) =>
{
    var (inhalt, name) = await dienst.TestmailAsync();
    http.Response.Headers.CacheControl = "no-store";
    return Results.File(inhalt, "message/rfc822", name);
}).RequireAuthorization();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();

/// <summary>Für Tests mit dem echten Host.</summary>
public partial class Program;
