using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProfOMat.Application.Konten;
using ProfOMat.Domain.Konten;
using ProfOMat.Infrastructure.Datenbank;

namespace ProfOMat.Infrastructure.Sicherheit;

/// <summary>
/// ASP.NET Core Identity über das vorhandene Konto-Modell: ein eigener Speicher (UserStore) statt der Identity-Standardtabellen, damit
/// Konten, Rollen und Signaturschlüssel fachlich bleiben, wie sie sind. Identity liefert Passwort-Hashes (PBKDF2), Sperre nach Fehlversuchen,
/// Sicherheitsstempel, TOTP, Wiederherstellungscodes, externe Anmeldungen (SAML/OIDC) und Passkeys.
/// </summary>
public static class Identitaet
{
    /// <summary>Claim mit der ProfOMat-Kennung (der Name-Claim trägt den Anzeigenamen).</summary>
    public const string KennungClaim = "profomat:kennung";

    public static IdentityBuilder AddProfOMatIdentitaet(this IServiceCollection dienste, IConfiguration konfiguration)
    {
        ArgumentNullException.ThrowIfNull(konfiguration);
        var anmeldung = konfiguration.GetSection(AnmeldeOptionen.Abschnitt).Get<AnmeldeOptionen>() ?? new AnmeldeOptionen();
        dienste.AddDataProtection();   // TOTP-Geheimnisse verschlüsselt; die Web-Anwendung legt die Schlüssel dauerhaft ab
        dienste.Configure<PasswordHasherOptions>(o => o.IterationCount = KontoDienst.PasswortIterationen);
        dienste.AddScoped<IUserConfirmation<Konto>, KontoBestaetigung>();
        return dienste.AddIdentityCore<Konto>(o =>
            {
                o.User.RequireUniqueEmail = false;
                o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyz0123456789._-ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                o.Password.RequiredLength = KontoDienst.MinPasswortLaenge;
                o.Password.RequireDigit = o.Password.RequireLowercase = o.Password.RequireUppercase = o.Password.RequireNonAlphanumeric = false;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = Math.Max(1, anmeldung.SperreNachFehlversuchen);
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(Math.Max(1, anmeldung.SperreMinuten));
                o.SignIn.RequireConfirmedAccount = true;   // „bestätigt“ = aktiv (KontoBestaetigung)
                o.ClaimsIdentity.UserIdClaimType = ClaimTypes.NameIdentifier;
                o.ClaimsIdentity.UserNameClaimType = ClaimTypes.Name;
                o.ClaimsIdentity.RoleClaimType = ClaimTypes.Role;
            })
            .AddUserStore<KontoSpeicher>()
            .AddClaimsPrincipalFactory<KontoClaimsFabrik>()
            .AddTokenProvider<AuthenticatorTokenProvider<Konto>>(TokenOptions.DefaultAuthenticatorProvider);
    }
}

/// <summary>Angemeldet werden dürfen nur aktive Konten (gesperrte Konten: keine Anmeldung, egal mit welchem Verfahren).</summary>
internal sealed class KontoBestaetigung : IUserConfirmation<Konto>
{
    public Task<bool> IsConfirmedAsync(UserManager<Konto> manager, Konto user) => Task.FromResult(user?.Aktiv == true);
}

/// <summary>Claims der Sitzung: Konto-Id, Anzeigename, Kennung, Rollen und Sicherheitsstempel.</summary>
internal sealed class KontoClaimsFabrik(UserManager<Konto> verwaltung, IOptions<IdentityOptions> optionen) : UserClaimsPrincipalFactory<Konto>(verwaltung, optionen)
{
    protected override Task<ClaimsIdentity> GenerateClaimsAsync(Konto user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var o = Options.ClaimsIdentity;
        var identitaet = new ClaimsIdentity(IdentityConstants.ApplicationScheme, o.UserNameClaimType, o.RoleClaimType);
        identitaet.AddClaim(new Claim(o.UserIdClaimType, user.Id.ToString("D")));
        identitaet.AddClaim(new Claim(o.UserNameClaimType, user.Anzeigename));
        identitaet.AddClaim(new Claim(Identitaet.KennungClaim, user.Kennung));
        identitaet.AddClaim(new Claim(o.SecurityStampClaimType, user.SicherheitsStempel));
        foreach (var r in user.Rollen.Select(r => r.Rolle).Order())
        {
            identitaet.AddClaim(new Claim(o.RoleClaimType, r.ToString()));
        }

        return Task.FromResult(identitaet);
    }
}

/// <summary>Identity-Speicher über <see cref="Konto"/> (Tabellen Konten, ExterneAnmeldungen, Passkeys). Speichert bei jedem Update sofort.</summary>
internal sealed class KontoSpeicher(ProfOMatDbContext db, IDataProtectionProvider schutz, TimeProvider uhr) :
    IUserPasswordStore<Konto>, IUserSecurityStampStore<Konto>, IUserLockoutStore<Konto>, IUserTwoFactorStore<Konto>,
    IUserAuthenticatorKeyStore<Konto>, IUserTwoFactorRecoveryCodeStore<Konto>, IUserLoginStore<Konto>, IUserPasskeyStore<Konto>
{
    private readonly IDataProtector totpSchutz = schutz.CreateProtector("ProfOMat.Identitaet.Authenticator.v1");

    public void Dispose()
    {
    }

    // ------------------------------------------------------------------------------------------------ Konto

    public Task<string> GetUserIdAsync(Konto user, CancellationToken cancellationToken) => Task.FromResult(user.Id.ToString("D"));

    public Task<string?> GetUserNameAsync(Konto user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Kennung);

    public Task SetUserNameAsync(Konto user, string? userName, CancellationToken cancellationToken)
        => string.Equals(userName, user.Kennung, StringComparison.OrdinalIgnoreCase)
            ? Task.CompletedTask
            : throw new NotSupportedException("Die Kennung eines Kontos ist fest.");

    public Task<string?> GetNormalizedUserNameAsync(Konto user, CancellationToken cancellationToken)
        => Task.FromResult<string?>(user.Kennung.ToUpperInvariant());

    public Task SetNormalizedUserNameAsync(Konto user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<IdentityResult> CreateAsync(Konto user, CancellationToken cancellationToken)
    {
        db.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(Konto user, CancellationToken cancellationToken)
    {
        if (db.Entry(user).State == EntityState.Detached)
        {
            db.Attach(user);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return IdentityResult.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            return IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure());
        }
    }

    public async Task<IdentityResult> DeleteAsync(Konto user, CancellationToken cancellationToken)
    {
        db.Remove(user);
        await db.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<Konto?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(userId, out var id))
        {
            return null;
        }

        return db.ChangeTracker.Entries<Konto>().FirstOrDefault(e => e.Entity.Id == id)?.Entity
               ?? await db.Set<Konto>().FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
    }

    public Task<Konto?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        var kennung = (normalizedUserName ?? "").Trim().ToLowerInvariant();
        return db.Set<Konto>().FirstOrDefaultAsync(k => k.Kennung == kennung, cancellationToken);
    }

    // ------------------------------------------------------------------------------------------------ Passwort, Stempel

    public Task SetPasswordHashAsync(Konto user, string? passwordHash, CancellationToken cancellationToken)
    {
        if (passwordHash is not null)
        {
            user.PasswortSetzen(passwordHash);
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(Konto user, CancellationToken cancellationToken) => Task.FromResult(user.PasswortHash);

    public Task<bool> HasPasswordAsync(Konto user, CancellationToken cancellationToken) => Task.FromResult(user.PasswortHash is not null);

    public Task SetSecurityStampAsync(Konto user, string stamp, CancellationToken cancellationToken)
    {
        user.StempelSetzen(stamp);
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(Konto user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.SicherheitsStempel);

    // ------------------------------------------------------------------------------------------------ Sperre nach Fehlversuchen

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(Konto user, CancellationToken cancellationToken) => Task.FromResult(user.GesperrtBis);

    public Task SetLockoutEndDateAsync(Konto user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.SperreSetzen(lockoutEnd);
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(Konto user, CancellationToken cancellationToken)
    {
        user.FehlversucheSetzen(user.Fehlversuche + 1);
        return Task.FromResult(user.Fehlversuche);
    }

    public Task ResetAccessFailedCountAsync(Konto user, CancellationToken cancellationToken)
    {
        user.FehlversucheSetzen(0);
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(Konto user, CancellationToken cancellationToken) => Task.FromResult(user.Fehlversuche);

    public Task<bool> GetLockoutEnabledAsync(Konto user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SetLockoutEnabledAsync(Konto user, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

    // ------------------------------------------------------------------------------------------------ Zweiter Faktor

    public Task SetTwoFactorEnabledAsync(Konto user, bool enabled, CancellationToken cancellationToken)
    {
        user.ZweitenFaktorSetzen(enabled);
        return Task.CompletedTask;
    }

    public Task<bool> GetTwoFactorEnabledAsync(Konto user, CancellationToken cancellationToken) => Task.FromResult(user.ZweiterFaktorAktiv);

    public Task SetAuthenticatorKeyAsync(Konto user, string key, CancellationToken cancellationToken)
    {
        user.AuthenticatorSchluesselSetzen(string.IsNullOrEmpty(key) ? null : totpSchutz.Protect(key));
        return Task.CompletedTask;
    }

    public Task<string?> GetAuthenticatorKeyAsync(Konto user, CancellationToken cancellationToken)
    {
        if (user.AuthenticatorSchluessel is not { } geschuetzt)
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            return Task.FromResult<string?>(totpSchutz.Unprotect(geschuetzt));
        }
        catch (CryptographicException)
        {
            return Task.FromResult<string?>(null);   // Schlüsselring verloren: zweiten Faktor neu einrichten (Systemverwaltung setzt zurück)
        }
    }

    public Task ReplaceCodesAsync(Konto user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        user.WiederherstellungscodesSetzen(recoveryCodes.Select(CodeHash));
        return Task.CompletedTask;
    }

    public Task<bool> RedeemCodeAsync(Konto user, string code, CancellationToken cancellationToken)
    {
        var hash = CodeHash(code);
        var vorhanden = user.WiederherstellungscodeHashes.ToList();
        if (!vorhanden.Remove(hash))
        {
            return Task.FromResult(false);
        }

        user.WiederherstellungscodesSetzen(vorhanden);
        return Task.FromResult(true);
    }

    public Task<int> CountCodesAsync(Konto user, CancellationToken cancellationToken) => Task.FromResult(user.WiederherstellungscodeHashes.Count);

    /// <summary>Wiederherstellungscodes nur als Hash (ohne Bindestriche, Leerzeichen, Groß-/Kleinschreibung).</summary>
    internal static string CodeHash(string code)
    {
        var sauber = new string((code ?? "").Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sauber)));
    }

    // ------------------------------------------------------------------------------------------------ Externe Anmeldungen (SAML, OIDC)

    public Task AddLoginAsync(Konto user, UserLoginInfo login, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(login);
        user.AnmeldungVerknuepfen(login.LoginProvider, login.ProviderKey, login.ProviderDisplayName, uhr.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task RemoveLoginAsync(Konto user, string loginProvider, string providerKey, CancellationToken cancellationToken)
    {
        user.AnmeldungTrennen(loginProvider, providerKey);
        return Task.CompletedTask;
    }

    public Task<IList<UserLoginInfo>> GetLoginsAsync(Konto user, CancellationToken cancellationToken)
        => Task.FromResult<IList<UserLoginInfo>>(user.Anmeldungen.Select(a => new UserLoginInfo(a.Anbieter, a.Schluessel, a.Anzeigename)).ToList());

    public async Task<Konto?> FindByLoginAsync(string loginProvider, string providerKey, CancellationToken cancellationToken)
    {
        var kontoId = await db.Set<ExterneAnmeldung>().Where(a => a.Anbieter == loginProvider && a.Schluessel == providerKey)
            .Select(a => (Guid?)a.KontoId).FirstOrDefaultAsync(cancellationToken);
        return kontoId is { } id ? await FindByIdAsync(id.ToString("D"), cancellationToken) : null;
    }

    // ------------------------------------------------------------------------------------------------ Passkeys

    public Task AddOrUpdatePasskeyAsync(Konto user, UserPasskeyInfo passkey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passkey);
        user.PasskeySpeichern(new KontoPasskey.Angaben(passkey.CredentialId, passkey.PublicKey, passkey.Name, passkey.CreatedAt, passkey.SignCount,
            passkey.Transports is { Length: > 0 } t ? string.Join(',', t) : null, passkey.IsUserVerified, passkey.IsBackupEligible, passkey.IsBackedUp,
            passkey.AttestationObject, passkey.ClientDataJson), uhr.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task<IList<UserPasskeyInfo>> GetPasskeysAsync(Konto user, CancellationToken cancellationToken)
        => Task.FromResult<IList<UserPasskeyInfo>>(user.Passkeys.Select(Info).ToList());

    public async Task<Konto?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        var kontoId = await db.Set<KontoPasskey>().Where(p => p.CredentialId == credentialId).Select(p => (Guid?)p.KontoId).FirstOrDefaultAsync(cancellationToken);
        return kontoId is { } id ? await FindByIdAsync(id.ToString("D"), cancellationToken) : null;
    }

    public Task<UserPasskeyInfo?> FindPasskeyAsync(Konto user, byte[] credentialId, CancellationToken cancellationToken)
        => Task.FromResult(user.Passkeys.FirstOrDefault(p => p.CredentialId.AsSpan().SequenceEqual(credentialId)) is { } p ? Info(p) : null);

    public Task RemovePasskeyAsync(Konto user, byte[] credentialId, CancellationToken cancellationToken)
    {
        user.PasskeyEntfernen(credentialId);
        return Task.CompletedTask;
    }

    private static UserPasskeyInfo Info(KontoPasskey p)
    {
        var a = p.AlsAngaben();
        return new UserPasskeyInfo(a.CredentialId, a.OeffentlicherSchluessel, a.Erstellt, a.Zaehler,
            a.Transporte?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [], a.NutzerGeprueft, a.SicherungMoeglich, a.Gesichert, a.Attestierung, a.ClientDaten)
        {
            Name = a.Bezeichnung,
        };
    }
}
