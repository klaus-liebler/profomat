using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using ProfOMat.Adapters.Mocks;
using ProfOMat.Application;
using ProfOMat.Application.Drittpruefung;
using ProfOMat.Application.Katalog;
using ProfOMat.Application.Konten;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Infrastructure;
using ProfOMat.Infrastructure.Datenbank;
using ProfOMat.Web.Betrieb;
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
    // Betrieb:Logformat=json: eine JSON-Zeile je Ereignis auf stdout (journald nimmt sie strukturiert auf, Weiterleitung per rsyslog/OTLP)
    .WriteTo.Logger(l => _ = string.Equals(builder.Configuration["Betrieb:Logformat"], "json", StringComparison.OrdinalIgnoreCase)
        ? l.WriteTo.Console(new Serilog.Formatting.Compact.RenderedCompactJsonFormatter())
        : l.WriteTo.Console(formatProvider: deutsch))
    .WriteTo.File(Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Speicher:Logs"] ?? "logs", "profomat-.log"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, formatProvider: deutsch));

// Unterschriftsbilder (PNG, Base64) laufen über die Blazor-Verbindung: Nachrichtengröße anheben.
builder.Services.AddRazorComponents().AddInteractiveServerComponents().AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024);
builder.Services.AddRadzenComponents();

var schluesselOrdner = Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Speicher:Schluessel"] ?? "data/schluessel");

// Anmeldung über ASP.NET Core Identity; die Verfahren (SAML, OIDC, Passwort + TOTP, Passkey) schaltet die Konfiguration je Instanz frei.
builder.Services.AddProfOMatAnmeldung(builder.Configuration, schluesselOrdner, builder.Environment.IsDevelopment());
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, Sitzungspruefung>();
builder.Services.AddSingleton<Anmeldebremse>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<BenutzerKontext>();
builder.Services.AddScoped<IAktuellerBenutzer, AktuellerBenutzer>();
builder.Services.AddScoped<Vorgang>();
builder.Services.AddRateLimits(builder.Configuration);
builder.Services.AddProfOMatBetrieb(builder.Configuration, Betriebsdienste.DatenOrdner(builder.Configuration, builder.Environment.ContentRootPath), schluesselOrdner);

builder.Services
    .AddProfOMatAnwendung(builder.Configuration)
    .AddProfOMatInfrastruktur(builder.Configuration, builder.Environment.ContentRootPath, builder.Configuration.GetValue("Hintergrundjobs:Aktiv", true))
    .AddProfOMatMocks();

builder.Services.AddDataProtection()
    .SetApplicationName("ProfOMat")
    .PersistKeysToFileSystem(new DirectoryInfo(schluesselOrdner));

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

        // Modus docker: nginx erreicht die App über das Docker-Netz statt über Loopback – nur diese Netze dürfen Köpfe setzen.
        foreach (var netz in builder.Configuration.GetSection("ReverseProxy:Netze").Get<string[]>() ?? [])
        {
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(netz));
        }
    });
}

var app = builder.Build();

if (args.Contains("--datenbankstand"))
{
    // Vorabprüfung beim Ausrollen (Deploy-Kit): Was würde DIESE Version mit der Datenbank tun? Liest nur.
    // Je Datenbank eine Zeile „DATENBANKSTAND|rolle|datenbank|zustand|eingespielt|offen|hinweis“, Exitcode siehe Datenbankstand.Exitcode.
    var zustaende = await Datenbankstand.PruefenAsync(app.Services);
    foreach (var z in zustaende)
    {
        Console.WriteLine(z.Zeile);
    }

    Environment.ExitCode = Datenbankstand.Exitcode(zustaende);
    return;
}

if (Array.IndexOf(args, "--sqlite-uebernehmen") is var stelle and >= 0)
{
    // Einmalige Übernahme der alten SQLite-Datenbank (bis 11/2026) nach PostgreSQL, mit Vergleich je Tabelle; --probe = Trockenlauf
    // in eine Wegwerf-Datenbank. Ausgabe: je Tabelle „UEBERNAHME|tabelle|quelle|ziel|gleich“, zuletzt „UEBERNAHME_ERGEBNIS|ok|…“.
    if (stelle + 1 >= args.Length || args[stelle + 1].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Aufruf: ProfOMat.Web --sqlite-uebernehmen <datei.db> [--probe]");
        Environment.ExitCode = 2;
        return;
    }

    try
    {
        var bericht = await SqliteUebernahme.UebernehmenAsync(args[stelle + 1], app.Services, args.Contains("--probe"), Console.WriteLine);
        Console.WriteLine(FormattableString.Invariant(
            $"UEBERNAHME_ERGEBNIS|{(bericht.Erfolgreich ? "ok" : "fehler")}|{bericht.Tabellen.Count} Tabellen|{bericht.Zeilen} Zeilen|{bericht.Entitaeten} Datensätze per EF gelesen|{(bericht.Probe ? "Probe" : "übernommen")}"));
        Environment.ExitCode = bericht.Erfolgreich ? 0 : 1;
    }
    catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException or Microsoft.Data.Sqlite.SqliteException or NotSupportedException)
    {
        Console.WriteLine("UEBERNAHME_ERGEBNIS|fehler|" + ex.Message.ReplaceLineEndings(" "));
        Environment.ExitCode = 1;
    }

    return;
}

await DatenbankInitialisierung.InitialisierenAsync(app.Services);
await Sicherheitsereignisse.KonfigurationPruefenAsync(app.Services, app.Configuration);

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

app.UseNetzbeschraenkung(app.Configuration);
app.UseRouting();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseSicherheitsheader();
app.UseRateLimiter();
// Tokens in Pfaden (Studierendenlinks, Kalender-Abo) erscheinen nie im Log
app.UseSerilogRequestLogging(o =>
{
    o.MessageTemplate = "HTTP {RequestMethod} {Pfad} responded {StatusCode} in {Elapsed:0.0000} ms";
    o.EnrichDiagnosticContext = (d, http) => d.Set("Pfad", Pfadmaske.Maskieren(http.Request.Path.Value));
});
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseRequestLocalization("de-DE");
if (app.Services.GetRequiredService<Anmeldeangebot>().Hat(ProfOMat.Application.Konten.Anmeldeverfahren.Saml))
{
    app.UseSamlFehler();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapProfOMatBetrieb();

app.MapAnmeldung();

// Sicherheitsbericht für Prüfende (Revision, Systemverwaltung); der Abruf steht im Sicherheitsprotokoll
app.MapGet("/revision/sicherheitsbericht.json", async (HttpContext http, ProfOMat.Application.Betrieb.RevisionDienst revision) =>
{
    try
    {
        await revision.SicherstellenAsync("Sicherheitsbericht");
    }
    catch (FachlicherFehler)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    http.Response.Headers.CacheControl = "no-store";
    http.Response.Headers.ContentDisposition = $"attachment; filename=\"profomat-sicherheitsbericht-{DateTime.UtcNow:yyyyMMdd}.json\"";
    return Results.Json(await Sicherheitsereignisse.BerichtAsync(http.RequestServices, app.Configuration, app.Environment, schluesselOrdner),
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}).RequireAuthorization();

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
}).RequireAuthorization().Exportprotokoll();

// PDF für Studierende über ihren geheimen Link (auch alte Links „…/status/…“)
async Task<IResult> StudierendenPdfAsync(Guid id, string token, DrittpruefungDienst dienst, HttpContext http)
{
    var dok = await dienst.PdfFuerStudierendeAsync(id, token);
    if (dok is null)
    {
        return Results.NotFound();
    }

    http.Response.Headers.CacheControl = "no-store";
    return Results.File(dok.Inhalt, dok.Inhaltstyp, dok.Dateiname);
}

app.MapGet("/drittpruefung/{id:guid}/{token}/pdf", StudierendenPdfAsync).AllowAnonymous();
app.MapGet("/drittpruefung/status/{id:guid}/{token}/pdf", StudierendenPdfAsync).AllowAnonymous();

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
}).RequireAuthorization().Exportprotokoll();

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
}).RequireAuthorization().Exportprotokoll();

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
}).RequireAuthorization().Exportprotokoll();

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
}).RequireAuthorization().Exportprotokoll();

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
}).RequireAuthorization().Exportprotokoll();

// Alle offenen eigenen Mailentwürfe (optional eines Anlasses) als ZIP mit .eml-Dateien
app.MapGet("/entwuerfe/mails.zip", async (string? anlass, ProfOMat.Application.Betrieb.Mailentwuerfe dienst, HttpContext http) =>
{
    try
    {
        var (inhalt, name, _) = await dienst.ZipAsync(anlass);
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(inhalt, "application/zip", name);
    }
    catch (FachlicherFehler ex)
    {
        return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status409Conflict);
    }
}).RequireAuthorization().Exportprotokoll();

// Gesprächstermine einer Hausarbeit als Kalenderdatei (nur Betreuende; prüft der Dienst)
app.MapGet("/hausarbeiten/{id:guid}/gespraeche.ics", async (Guid id, ProfOMat.Application.Hausarbeiten.HausarbeitDienst dienst, HttpContext http) =>
{
    try
    {
        var (inhalt, name) = await dienst.KalenderAsync(id);
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(inhalt, "text/calendar; charset=utf-8", name);
    }
    catch (FachlicherFehler ex)
    {
        return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status409Conflict);
    }
}).RequireAuthorization().Exportprotokoll();

// Kalender-Abo ohne Anmeldung über den geheimen Link (Outlook: „Kalender aus dem Internet“); falscher Link → 404
app.MapGet("/hausarbeit/kalender/{id:guid}/{token}.ics", async (Guid id, string token, ProfOMat.Application.Hausarbeiten.HausarbeitDienst dienst, HttpContext http) =>
{
    var inhalt = await dienst.KalenderAboAsync(id, token);
    if (inhalt is null)
    {
        return Results.NotFound();
    }

    http.Response.Headers.CacheControl = "no-store";
    http.Response.Headers["X-Robots-Tag"] = "noindex";
    return Results.File(inhalt, "text/calendar; charset=utf-8");
}).AllowAnonymous();

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
}).RequireAuthorization().Exportprotokoll();

app.MapGet("/profil/testmail.eml", async (ProfOMat.Application.Betrieb.Mailsignaturen dienst, HttpContext http) =>
{
    var (inhalt, name) = await dienst.TestmailAsync();
    http.Response.Headers.CacheControl = "no-store";
    return Results.File(inhalt, "message/rfc822", name);
}).RequireAuthorization().Exportprotokoll();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();

/// <summary>Für Tests mit dem echten Host.</summary>
public partial class Program;
