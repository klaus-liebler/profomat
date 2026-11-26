using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using ProfOMat.Application.Konten;
using Sustainsys.Saml2;
using Sustainsys.Saml2.AspNetCore2;
using Sustainsys.Saml2.Metadata;

namespace ProfOMat.Web.Sicherheit;

/// <summary>
/// Hochschul-, Firmen- oder Behördenkonto per SAML 2.0 (Shibboleth/DFN-AAI, Entra ID, ADFS …), nach dem Vorbild von Podium.
/// ProfOMat ist Service Provider:
///   Metadaten  {OeffentlicheAdresse}Saml2       (entityID = diese URL)
///   ACS        {OeffentlicheAdresse}Saml2/Acs   (HTTP-POST)
/// Konfiguration (Abschnitt „Saml“, z. B. per Umgebung Saml__IdpEntityId=…):
///   IdpEntityId        schaltet SAML frei
///   IdpMetadataUrl     Vorgabe: IdpEntityId
///   Zugehoerigkeiten   erlaubte eduPersonScopedAffiliation, kommagetrennt (Vorgabe: employee@, staff@, faculty@hs-osnabrueck.de)
///   Zertifikat         Schlüssel + Zertifikat des SP (PFX); fehlt die Datei, wird sie beim Start erzeugt (im Schlüsselordner)
///   Kontakt            optional: technischer Kontakt in den Metadaten
///   Anzeigename        Text der Kachel (Vorgabe „Hochschulkonto“)
///   MfaKontexte        weitere AuthnContextClassRef-Werte, die als zweiter Faktor gelten (REFEDS-MFA gilt immer)
/// </summary>
internal static class Saml
{
    public const string Schema = Saml2Defaults.Scheme;   // „Saml2“ – zugleich der Pfad der Metadaten

    public static bool Konfiguriert(IConfiguration k) => !string.IsNullOrWhiteSpace(k["Saml:IdpEntityId"]);

    public static string[] Zugehoerigkeiten(IConfiguration k)
        => (k["Saml:Zugehoerigkeiten"] ?? "employee@hs-osnabrueck.de,staff@hs-osnabrueck.de,faculty@hs-osnabrueck.de")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string[] MfaKontexte(IConfiguration k)
        => (k["Saml:MfaKontexte"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static void AddProfOMatSaml(this AuthenticationBuilder anmeldung, IConfiguration k, string oeffentlicheAdresse, string schluesselOrdner)
    {
        var basis = oeffentlicheAdresse.TrimEnd('/');
        var zertifikat = ZertifikatLadenOderErzeugen(k["Saml:Zertifikat"] is { Length: > 0 } p ? p : Path.Combine(schluesselOrdner, "saml-sp.pfx"), new Uri(basis).Host);
        anmeldung.AddSaml2(Schema, o =>
        {
            o.SignInScheme = IdentityConstants.ExternalScheme;   // kurzlebiges Zwischen-Cookie bis zur Kontozuordnung
            var sp = o.SPOptions;
            sp.EntityId = new EntityId(basis + "/" + Schema);
            sp.PublicOrigin = new Uri(basis + "/");
            sp.ModulePath = "/" + Schema;
            sp.ReturnUrl = new Uri(basis + "/anmelden/extern/fertig");
            sp.WantAssertionsSigned = true;
            sp.ServiceCertificates.Add(new ServiceCertificate { Certificate = zertifikat, Use = CertificateUse.Both, MetadataPublishOverride = MetadataPublishOverrideType.None });
            sp.MetadataCacheDuration = new XsdDuration(hours: 1);
            sp.Organization = new Organization();
            sp.Organization.Names.Add(new LocalizedName("ProfOMat", "de"));
            sp.Organization.DisplayNames.Add(new LocalizedName("ProfOMat – Prüfungsabläufe mit digitaler Unterschrift", "de"));
            sp.Organization.Urls.Add(new LocalizedUri(new Uri(basis + "/"), "de"));
            if (k["Saml:Kontakt"] is { Length: > 0 } mail)
            {
                var c = new ContactPerson(ContactType.Technical);
                c.EmailAddresses.Add("mailto:" + mail);
                sp.Contacts.Add(c);
            }

            var acs = new AttributeConsumingService { IsDefault = true };
            acs.ServiceNames.Add(new LocalizedName("ProfOMat", "de"));
            foreach (var (name, freundlich, pflicht) in new[]
                     {
                         (ExterneAntwort.AttrSubjectId, "samlSubjectID", false), (ExterneAntwort.AttrPairwiseId, "samlPairwiseID", false),
                         (ExterneAntwort.AttrEppn, "eduPersonPrincipalName", false), (ExterneAntwort.AttrAffiliation, "eduPersonScopedAffiliation", true),
                         (ExterneAntwort.AttrDisplayName, "displayName", false), (ExterneAntwort.AttrGivenName, "givenName", false),
                         (ExterneAntwort.AttrSurname, "sn", false), (ExterneAntwort.AttrMail, "mail", true),
                     })
            {
                acs.RequestedAttributes.Add(new RequestedAttribute(name) { FriendlyName = freundlich, IsRequired = pflicht, NameFormat = RequestedAttribute.AttributeNameFormatUri });
            }

            sp.AttributeConsumingServices.Add(acs);

            // .NET (EncryptedXml) kann kein AES-GCM – Shibboleth wählt laut Metadaten dann AES-CBC (aus Podium)
            o.Notifications.MetadataCreated = (md, _) =>
            {
                // nur HTTP-POST an den ACS, kein Artifact-Binding und kein Single Logout
                foreach (var spd in md.RoleDescriptors.OfType<SpSsoDescriptor>())
                {
                    foreach (var schluessel in spd.AssertionConsumerServices.Where(a => a.Value.Binding != Sustainsys.Saml2.WebSso.Saml2Binding.HttpPostUri).Select(a => a.Key).ToList())
                    {
                        spd.AssertionConsumerServices.Remove(schluessel);
                    }

                    spd.SingleLogoutServices.Clear();
                }

                foreach (var rolle in md.RoleDescriptors)
                {
                    foreach (var kd in rolle.Keys.Where(x => x.Use is KeyType.Encryption or KeyType.Unspecified))
                    {
                        // Schlüsseltransport nur rsa-oaep-mgf1p: bei xmlenc11#rsa-oaep scheitert Sustainsys am Digest-URI xmldsig#sha1
                        foreach (var alg in new[] { "http://www.w3.org/2001/04/xmlenc#aes256-cbc", "http://www.w3.org/2001/04/xmlenc#aes128-cbc",
                                     "http://www.w3.org/2001/04/xmlenc#rsa-oaep-mgf1p" })
                        {
                            kd.EncryptionMethods.Add(new EncryptionMethod { Algorithm = new Uri(alg) });
                        }
                    }
                }
            };

            // Zwischen-Cookie klein halten (sonst „upstream sent too big header“ am Proxy): nur die Attribute, die ProfOMat auswertet,
            // plus den Authentifizierungskontext für den MFA-Nachweis.
            o.Notifications.AcsCommandResultCreated = (ergebnis, _) =>
            {
                if (ergebnis.Principal?.Identity is not ClaimsIdentity quelle)
                {
                    return;
                }

                var schmal = new ClaimsIdentity(quelle.AuthenticationType);
                schmal.AddClaims(quelle.Claims.Where(c => ExterneAntwort.SamlBenoetigt.Contains(c.Type)).Select(c => new Claim(c.Type, c.Value)));
                ergebnis.Principal = new ClaimsPrincipal(schmal);
            };
        });
    }

    /// <summary>IdP beim ersten Bedarf registrieren (Metadaten laden) – so bleiben die eigenen Metadaten abrufbar, auch wenn der IdP gerade fehlt.</summary>
    private static readonly SemaphoreSlim IdpSperre = new(1, 1);

    public static async Task<bool> IdpSicherstellenAsync(Saml2Options o, IConfiguration k, ILogger log)
    {
        var entityId = new EntityId(k["Saml:IdpEntityId"]);
        if (o.IdentityProviders.TryGetValue(entityId, out _))
        {
            return true;
        }

        await IdpSperre.WaitAsync();
        try
        {
            if (o.IdentityProviders.TryGetValue(entityId, out _))
            {
                return true;
            }

            var idp = new IdentityProvider(entityId, o.SPOptions)
            {
                MetadataLocation = k["Saml:IdpMetadataUrl"] ?? entityId.Id,
                AllowUnsolicitedAuthnResponse = false,
            };
            await Task.Run(() => idp.LoadMetadata = true);
            o.IdentityProviders.Add(idp);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Metadaten des SAML-IdP konnten nicht geladen werden");
            return false;
        }
        finally
        {
            IdpSperre.Release();
        }
    }

    /// <summary>
    /// Fehler beim Verarbeiten der IdP-Antwort (…/Saml2/Acs) wirft Sustainsys als Ausnahme – statt einer leeren 500-Seite: ausführlich loggen
    /// und mit fester Meldung zurück zur Anmeldeseite. Muss vor UseAuthentication stehen.
    /// </summary>
    public static void UseSamlFehler(this WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProfOMat.Saml");
        app.Use(async (kontext, weiter) =>
        {
            if (!kontext.Request.Path.StartsWithSegments("/" + Schema))
            {
                await weiter();
                return;
            }

            try
            {
                await weiter();
            }
            catch (Exception ex) when (!kontext.Response.HasStarted && ex is not OperationCanceledException)
            {
                log.LogError(ex, "SAML: Antwort des IdP an {Pfad} konnte nicht verarbeitet werden", kontext.Request.Path);
                kontext.Response.Clear();
                kontext.Response.Redirect($"{kontext.Request.PathBase}/anmelden?fehler={Anmeldefehler.Antwort}");
            }
        });
    }

    private static X509Certificate2 ZertifikatLadenOderErzeugen(string pfad, string host)
    {
        if (!File.Exists(pfad))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pfad))!);
            using var rsa = RSA.Create(3072);
            var anfrage = new CertificateRequest($"CN={host}, O=ProfOMat", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            anfrage.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            using var c = anfrage.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllBytes(pfad, c.Export(X509ContentType.Pkcs12));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(pfad, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        return X509CertificateLoader.LoadPkcs12FromFile(pfad, null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
    }
}
