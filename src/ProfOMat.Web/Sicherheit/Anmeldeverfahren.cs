using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using ProfOMat.Application;
using ProfOMat.Application.Konten;
using ProfOMat.Domain.Gemeinsam;
using ProfOMat.Domain.Konten;
using Sustainsys.Saml2.AspNetCore2;

namespace ProfOMat.Web.Sicherheit;

/// <summary>Welche Anmeldeverfahren diese Instanz anbietet (aus der Konfiguration, beim Start festgelegt).</summary>
internal sealed class Anmeldeangebot(IReadOnlySet<Anmeldeverfahren> verfahren, string samlName, string oidcName, bool testanmeldung)
{
    public IReadOnlySet<Anmeldeverfahren> Verfahren { get; } = verfahren;

    public string SamlName { get; } = samlName;

    public string OidcName { get; } = oidcName;

    public bool Testanmeldung { get; } = testanmeldung;

    public bool Hat(Anmeldeverfahren v) => Verfahren.Contains(v);

    public bool Sso => Hat(Anmeldeverfahren.Saml) || Hat(Anmeldeverfahren.Oidc);
}

/// <summary>Feste Fehlermeldungen der Anmeldung (per Code in der Adresse, damit niemand fremden Text auf die Anmeldeseite bringt).</summary>
internal static class Anmeldefehler
{
    public const string NichtErreichbar = "nicht-erreichbar";
    public const string Antwort = "antwort";
    public const string Abgebrochen = "abgebrochen";
    public const string KeineKennung = "keine-kennung";
    public const string Zugehoerigkeit = "zugehoerigkeit";
    public const string Unbekannt = "unbekannt";
    public const string Gesperrt = "gesperrt";
    public const string Einladung = "einladung";
    public const string Verfahren = "verfahren";
    public const string Verknuepfung = "verknuepfung";

    public static string? Text(string? code) => code switch
    {
        NichtErreichbar => "Der Identitätsdienst ist gerade nicht erreichbar. Bitte später erneut versuchen.",
        Antwort => "Die Antwort des Identitätsdienstes konnte nicht verarbeitet werden. Bitte erneut versuchen; besteht das Problem weiter, die Systemverwaltung informieren.",
        Abgebrochen => "Die Anmeldung über den Identitätsdienst ist fehlgeschlagen oder wurde abgebrochen.",
        KeineKennung => "Der Identitätsdienst hat keine dauerhafte Kennung übermittelt. Bitte beim Rechenzentrum melden.",
        Zugehoerigkeit => "Die Anmeldung ist Beschäftigten vorbehalten. Studierende brauchen für ihre Anträge keine Anmeldung.",
        Unbekannt => "Mit diesem Konto ist noch kein ProfOMat-Konto verknüpft. Neu hier? Die Systemverwaltung schickt Ihnen einen Einladungslink. "
                     + "Schon ein Konto? Dann mit Passwort oder Passkey anmelden und unter Profil → Anmeldung und Sicherheit verknüpfen.",
        Gesperrt => "Dieses Konto ist gesperrt oder nach zu vielen Fehlversuchen vorübergehend gesperrt.",
        Einladung => "Der Einladungslink ist ungültig, abgelaufen oder schon verwendet – oder dieses Konto des Identitätsdienstes ist schon verknüpft.",
        Verfahren => "Dieses Anmeldeverfahren ist hier nicht freigeschaltet.",
        Verknuepfung => "Die Verknüpfung war nicht möglich – vermutlich ist dieses Konto des Identitätsdienstes schon mit einem anderen ProfOMat-Konto verknüpft.",
        _ => null,
    };
}

/// <summary>Registrierung: ASP.NET Core Identity (Cookies, SignInManager, Passkeys) und die freigeschalteten Verfahren.</summary>
internal static class AnmeldeRegistrierung
{
    public const string OidcSchema = "oidc";
    public const string EinladungCookie = "profomat.einladung";
    public const string EinladungZweck = "ProfOMat.Einladung.Sso.v1";

    /// <param name="entwicklung">Nur in der Entwicklungsumgebung gibt es die Testanmeldung (auch wenn sie anderswo konfiguriert ist).</param>
    public static IServiceCollection AddProfOMatAnmeldung(this IServiceCollection dienste, IConfiguration k, string schluesselOrdner, bool entwicklung)
    {
        var optionen = k.GetSection(AnmeldeOptionen.Abschnitt).Get<AnmeldeOptionen>() ?? new AnmeldeOptionen();
        var oidc = !string.IsNullOrWhiteSpace(k["Oidc:Authority"]) && !string.IsNullOrWhiteSpace(k["Oidc:ClientId"]);
        var verfahren = optionen.Wirksam(Saml.Konfiguriert(k), oidc);
        dienste.AddSingleton(new Anmeldeangebot(verfahren, k["Saml:Anzeigename"] ?? "Hochschulkonto", k["Oidc:Anzeigename"] ?? "Firmenkonto",
            optionen.TestanmeldungErlaubt && entwicklung));

        new IdentityBuilder(typeof(Konto), dienste).AddSignInManager();
        dienste.Configure<IdentityPasskeyOptions>(o =>
        {
            o.UserVerificationRequirement = "required";   // Gerät prüft PIN/Biometrie: Passkey gilt als zweiter Faktor
            o.ResidentKeyRequirement = "required";         // Anmeldung ohne Kennung (auffindbarer Passkey)
            if (k["Anmeldung:PasskeyDomain"] is { Length: > 0 } domain)
            {
                o.ServerDomain = domain;
            }
        });

        var anmeldung = dienste.AddAuthentication(o =>
        {
            o.DefaultScheme = IdentityConstants.ApplicationScheme;
            o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        });
        anmeldung.AddIdentityCookies(c =>
        {
            c.ApplicationCookie!.Configure(o =>
            {
                o.LoginPath = "/anmelden";
                o.AccessDeniedPath = "/anmelden";
                o.Cookie.Name = "profomat.anmeldung";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromHours(10);
                o.SlidingExpiration = true;
                // Gesperrte Konten, neues Passwort, geänderte Rollen oder zweiter Faktor beenden bestehende Sitzungen beim nächsten Aufruf.
                o.Events.OnValidatePrincipal = async ctx =>
                {
                    if (!await Sitzung.GueltigAsync(ctx.Principal, ctx.HttpContext.RequestServices, ctx.HttpContext.RequestAborted))
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                    }
                };
            });
            c.ExternalCookie!.Configure(o =>
            {
                o.Cookie.Name = "profomat.extern";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;   // kommt per Weiterleitung vom Identitätsdienst zurück
                o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            });
            c.TwoFactorUserIdCookie!.Configure(o =>
            {
                o.Cookie.Name = "profomat.zweiterfaktor";
                o.Cookie.HttpOnly = true;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            });
        });

        var oeffentlich = k["ProfOMat:OeffentlicheAdresse"] ?? "http://localhost:5310/";
        if (verfahren.Contains(Anmeldeverfahren.Saml))
        {
            anmeldung.AddProfOMatSaml(k, oeffentlich, schluesselOrdner);
        }

        if (verfahren.Contains(Anmeldeverfahren.Oidc))
        {
            anmeldung.AddOpenIdConnect(OidcSchema, o =>
            {
                o.SignInScheme = IdentityConstants.ExternalScheme;
                o.Authority = k["Oidc:Authority"];
                o.ClientId = k["Oidc:ClientId"];
                o.ClientSecret = k["Oidc:ClientSecret"] is { Length: > 0 } geheim ? geheim : null;   // nur per Umgebung/Geheimnisablage
                o.ResponseType = "code";
                o.UsePkce = true;
                o.CallbackPath = "/signin-oidc";
                o.MapInboundClaims = false;
                o.SaveTokens = false;
                o.GetClaimsFromUserInfoEndpoint = k.GetValue("Oidc:UserInfo", false);
                o.RequireHttpsMetadata = k.GetValue("Oidc:HttpsMetadata", true);
                o.Scope.Clear();
                foreach (var s in ("openid profile email " + k["Oidc:Scopes"]).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
                {
                    o.Scope.Add(s);
                }

                o.TokenValidationParameters.NameClaimType = "name";
                o.Events.OnTicketReceived = ctx =>
                {
                    // Zwischen-Cookie klein halten: nur die Claims, die ProfOMat auswertet
                    if (ctx.Principal?.Identity is ClaimsIdentity quelle)
                    {
                        var schmal = new ClaimsIdentity(quelle.AuthenticationType, "name", "role");
                        schmal.AddClaims(quelle.Claims.Where(c => ExterneAntwort.OidcBenoetigt.Contains(c.Type)).Select(c => new Claim(c.Type, c.Value)));
                        ctx.Principal = new ClaimsPrincipal(schmal);
                    }

                    return Task.CompletedTask;
                };
                o.Events.OnRemoteFailure = ctx =>
                {
                    ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ProfOMat.Oidc")
                        .LogWarning(ctx.Failure, "OIDC: Anmeldung fehlgeschlagen");
                    ctx.Response.Redirect($"{ctx.Request.PathBase}/anmelden?fehler={Anmeldefehler.Abgebrochen}");
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                };
            });
        }

        dienste.AddScoped<Anmeldeabschluss>();
        return dienste;
    }

    public static string Schema(Anmeldeverfahren v) => v == Anmeldeverfahren.Saml ? Saml.Schema : OidcSchema;
}

/// <summary>
/// Letzter Schritt jeder Anmeldung (Passwort, Passkey, SAML, OIDC, Test): Konto aktiv und nicht vorübergehend gesperrt? Zweiter Faktor nötig
/// (TOTP-Pflicht für Systemverwaltung und Studiendekanat)? Dann Zwischen-Cookie von Identity und weiter zur Code-Abfrage, sonst Sitzung.
/// </summary>
internal sealed class Anmeldeabschluss(SignInManager<Konto> anmeldung, KontoDienst konten, IOptions<AnmeldeOptionen> optionen, ILogger<Anmeldeabschluss> log)
{
    /// <returns>Ziel relativ zur Pfad-Basis (ohne führenden Schrägstrich).</returns>
    public async Task<string> AbschliessenAsync(HttpContext http, Konto konto, Anmeldeart art, bool mfaNachgewiesen, string? ruecksprung)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(konto);
        if (!await anmeldung.CanSignInAsync(konto) || await anmeldung.UserManager.IsLockedOutAsync(konto))
        {
            return $"anmelden?fehler={Anmeldefehler.Gesperrt}";
        }

        var ziel = Ruecksprung.Lokal(ruecksprung, http.Request.PathBase.HasValue ? http.Request.PathBase.Value + "/" : "/");
        if (Anmelderegeln.BrauchtZweitenFaktor(konto, art, mfaNachgewiesen, optionen.Value))
        {
            await ZwischenCookieAsync(http, konto, art);
            return "anmelden/zweiter-faktor" + (ziel == "./" ? "" : "?ReturnUrl=" + Uri.EscapeDataString("/" + ziel));
        }

        await anmeldung.SignInWithClaimsAsync(konto, isPersistent: false, Claims(art, mfaNachgewiesen));
        await konten.AnmeldungProtokollierenAsync(konto, art, mfaNachgewiesen);
        log.LogInformation("Anmeldung ({Art}{Mfa}) Konto {Konto}", art, mfaNachgewiesen ? ", MFA" : "", Pseudonym.Id(konto.Id));
        return ziel;
    }

    /// <summary>
    /// Wie SignInManager.StoreTwoFactorInfo: Konto-Id, Anmeldeart und Sicherheitsstempel im kurzlebigen Zwischen-Cookie (5 min). Identity prüft
    /// den Stempel; ändert er sich während der Einrichtung (neuer TOTP-Schlüssel), wird das Cookie neu ausgestellt.
    /// </summary>
    public async Task ZwischenCookieAsync(HttpContext http, Konto konto, Anmeldeart art)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(konto);
        var zwischen = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
        zwischen.AddClaim(new Claim(ClaimTypes.Name, konto.Id.ToString("D")));
        zwischen.AddClaim(new Claim(ClaimTypes.AuthenticationMethod, art.ToString()));
        zwischen.AddClaim(new Claim(anmeldung.Options.ClaimsIdentity.SecurityStampClaimType, konto.SicherheitsStempel));
        await http.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(zwischen));
    }

    /// <summary>Nach dem zweiten Faktor (Identity hat schon angemeldet): protokollieren.</summary>
    public async Task ZweiterFaktorErledigtAsync(Konto konto, Anmeldeart art)
    {
        await konten.AnmeldungProtokollierenAsync(konto, art, zweiterFaktor: true);
        log.LogInformation("Anmeldung ({Art}, zweiter Faktor) Konto {Konto}", art, Pseudonym.Id(konto.Id));
    }

    public static IEnumerable<Claim> Claims(Anmeldeart art, bool mfa)
    {
        yield return new Claim(ClaimTypes.AuthenticationMethod, art.ToString());
        if (mfa)
        {
            yield return new Claim("amr", "mfa");
        }
    }
}

/// <summary>Endpunkte der Anmeldung außerhalb der Razor-Seiten: Identitätsdienste, Passkey-Optionen, Abmelden.</summary>
internal static class AnmeldeEndpunkte
{
    public static void MapAnmeldung(this WebApplication app)
    {
        // Start beim Identitätsdienst (GET-Link statt Formular: die CSP form-action 'self' verböte die Weiterleitung nach außen)
        app.MapGet("/anmelden/extern/{verfahren}", async (string verfahren, string? returnUrl, string? zweck, HttpContext http, Anmeldeangebot angebot,
            IOptionsMonitor<Saml2Options> samlOptionen, IConfiguration k, IDataProtectionProvider schutz, ILogger<Anmeldeangebot> log) =>
        {
            if (!Enum.TryParse<Anmeldeverfahren>(verfahren, ignoreCase: true, out var v) || v is not (Anmeldeverfahren.Saml or Anmeldeverfahren.Oidc) || !angebot.Hat(v))
            {
                return Zurueck(http, "anmelden", Anmeldefehler.Verfahren);
            }

            if (v == Anmeldeverfahren.Saml && !await Saml.IdpSicherstellenAsync(samlOptionen.Get(Saml.Schema), k, log))
            {
                return Zurueck(http, "anmelden", Anmeldefehler.NichtErreichbar);
            }

            var eigenschaften = new AuthenticationProperties { RedirectUri = http.Request.PathBase + "/anmelden/extern/fertig" };
            eigenschaften.Items["verfahren"] = v.ToString();
            eigenschaften.Items["ruecksprung"] = returnUrl;
            if (zweck == "verknuepfen")
            {
                if (Anmeldung.AlsBenutzer(http.User)?.KontoId is not { } id)
                {
                    return Results.Redirect(http.Request.PathBase + "/anmelden");
                }

                eigenschaften.Items["zweck"] = "verknuepfen";
                eigenschaften.Items["konto"] = id.ToString("D");
            }
            else if (zweck == "einladung" && http.Request.Cookies[AnmeldeRegistrierung.EinladungCookie] is { Length: > 0 } geschuetzt)
            {
                http.Response.Cookies.Delete(AnmeldeRegistrierung.EinladungCookie, new CookieOptions { Path = CookiePfad(http) });
                try
                {
                    eigenschaften.Items["einladung"] = schutz.CreateProtector(AnmeldeRegistrierung.EinladungZweck).ToTimeLimitedDataProtector().Unprotect(geschuetzt);
                    eigenschaften.Items["zweck"] = "einladung";
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    return Zurueck(http, "anmelden", Anmeldefehler.Einladung);
                }
            }

            return Results.Challenge(eigenschaften, [AnmeldeRegistrierung.Schema(v)]);
        }).AllowAnonymous();

        // Rückkehr vom Identitätsdienst: Kennung und MFA-Nachweis auswerten, Konto finden, verknüpfen oder per Einladung anlegen
        app.MapGet("/anmelden/extern/fertig", async (HttpContext http, IConfiguration k, UserManager<Konto> identitaet, SignInManager<Konto> anmeldung,
            KontoDienst konten, SicherheitDienst sicherheit, Anmeldeabschluss abschluss, ILogger<Anmeldeangebot> log) =>
        {
            var ergebnis = await http.AuthenticateAsync(IdentityConstants.ExternalScheme);
            await http.SignOutAsync(IdentityConstants.ExternalScheme);
            if (!ergebnis.Succeeded || ergebnis.Principal is not { } p || ergebnis.Properties?.Items is not { } items
                || !Enum.TryParse<Anmeldeverfahren>(Wert(items, "verfahren"), out var v))
            {
                return Zurueck(http, "anmelden", Anmeldefehler.Abgebrochen);
            }

            var ext = v == Anmeldeverfahren.Saml
                ? ExterneAntwort.AusSaml(p.Claims, Saml.MfaKontexte(k))
                : ExterneAntwort.AusOidc(p.Claims, (k["Oidc:MfaAcr"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (ext is null)
            {
                log.LogWarning("{Verfahren}-Antwort ohne verwendbare Kennung; erhaltene Attribute: {Attribute}", v, string.Join(", ", p.Claims.Select(c => c.Type).Distinct()));
                return Zurueck(http, "anmelden", Anmeldefehler.KeineKennung);
            }

            if (v == Anmeldeverfahren.Saml && !Anmelderegeln.ZugehoerigkeitErlaubt(Saml.Zugehoerigkeiten(k), ext.Zugehoerigkeiten))
            {
                log.LogInformation("SAML-Anmeldung abgelehnt: keine erlaubte Zugehörigkeit ({Zugehoerigkeiten})", string.Join(", ", ext.Zugehoerigkeiten.Select(z => z.Split('@')[0])));
                return Zurueck(http, "anmelden", Anmeldefehler.Zugehoerigkeit);
            }

            var art = v == Anmeldeverfahren.Saml ? Anmeldeart.Saml : Anmeldeart.Oidc;
            switch (Wert(items, "zweck"))
            {
                case "verknuepfen":
                    var eigenes = await identitaet.GetUserAsync(http.User);
                    if (eigenes is null || eigenes.Id.ToString("D") != Wert(items, "konto"))
                    {
                        return Results.Redirect(http.Request.PathBase + "/anmelden");
                    }

                    try
                    {
                        await sicherheit.AnmeldungVerknuepfenAsync(eigenes, ext);
                        await anmeldung.RefreshSignInAsync(eigenes);   // eigener Stempel hat sich geändert: Sitzung behalten
                        return Results.Redirect(http.Request.PathBase + "/profil/sicherheit?ok=verknuepft");
                    }
                    catch (FachlicherFehler)
                    {
                        return Zurueck(http, "profil/sicherheit", Anmeldefehler.Verknuepfung);
                    }

                case "einladung":
                    Konto neu;
                    try
                    {
                        neu = await konten.EinladungMitIdentitaetsdienstEinloesenAsync(Wert(items, "einladung"), ext);
                    }
                    catch (FachlicherFehler)
                    {
                        return Zurueck(http, "anmelden", Anmeldefehler.Einladung);
                    }

                    log.LogInformation("Einladung über {Verfahren} eingelöst, Konto {Konto}", v, Pseudonym.Id(neu.Id));
                    return Results.Redirect(http.Request.PathBase + "/" + await abschluss.AbschliessenAsync(http, neu, art, ext.MfaNachgewiesen, null));

                default:
                    var konto = await identitaet.FindByLoginAsync(ext.Anbieter, ext.Schluessel);
                    if (konto is null)
                    {
                        return Zurueck(http, "anmelden", Anmeldefehler.Unbekannt);
                    }

                    return Results.Redirect(http.Request.PathBase + "/" + await abschluss.AbschliessenAsync(http, konto, art, ext.MfaNachgewiesen, Wert(items, "ruecksprung")));
            }
        }).AllowAnonymous();

        // Passkey: Optionen für die Anmeldung (auffindbarer Passkey, ohne Kennung). Der Browser schickt das Ergebnis per Formular an /anmelden.
        app.MapPost("/anmelden/passkey/optionen", async (HttpContext http, IAntiforgery antiforgery, Anmeldeangebot angebot, SignInManager<Konto> anmeldung) =>
        {
            if (!angebot.Hat(Anmeldeverfahren.Passkey))
            {
                return Results.NotFound();
            }

            if (!await GueltigAsync(antiforgery, http))
            {
                return Results.BadRequest();
            }

            http.Response.Headers.CacheControl = "no-store";
            return Results.Content(await anmeldung.MakePasskeyRequestOptionsAsync(null), "application/json");
        }).AllowAnonymous();

        // Passkey: Optionen zum Hinzufügen (nur angemeldet, nur für das eigene Konto)
        app.MapPost("/profil/sicherheit/passkey/optionen", async (HttpContext http, IAntiforgery antiforgery, Anmeldeangebot angebot,
            UserManager<Konto> identitaet, SignInManager<Konto> anmeldung) =>
        {
            if (!angebot.Hat(Anmeldeverfahren.Passkey))
            {
                return Results.NotFound();
            }

            if (!await GueltigAsync(antiforgery, http) || await identitaet.GetUserAsync(http.User) is not { } konto)
            {
                return Results.BadRequest();
            }

            http.Response.Headers.CacheControl = "no-store";
            var json = await anmeldung.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
            {
                Id = konto.Id.ToString("D"),
                Name = konto.Kennung,
                DisplayName = konto.Anzeigename,
            });
            return Results.Content(json, "application/json");
        }).RequireAuthorization();

        app.MapPost("/abmelden", async (SignInManager<Konto> anmeldung) =>
        {
            await anmeldung.SignOutAsync();
            return Results.LocalRedirect("~/");
        });
    }

    private static string? Wert(IDictionary<string, string?> items, string schluessel) => items.TryGetValue(schluessel, out var w) ? w : null;

    public static string CookiePfad(HttpContext http) => http.Request.PathBase.HasValue ? http.Request.PathBase.Value! : "/";

    private static IResult Zurueck(HttpContext http, string seite, string fehler) => Results.Redirect($"{http.Request.PathBase}/{seite}?fehler={fehler}");

    private static async Task<bool> GueltigAsync(IAntiforgery antiforgery, HttpContext http)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
