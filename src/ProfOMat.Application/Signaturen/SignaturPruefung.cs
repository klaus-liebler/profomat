using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProfOMat.Domain.Drittpruefung;

namespace ProfOMat.Application.Signaturen;

/// <summary>Was die Oberfläche nach dem Unterschreiben schickt.</summary>
/// <param name="Art">Schlüssel oder gezeichnete Unterschrift.</param>
/// <param name="Algorithmus">Bei Schlüssel: „ECDSA-SHA256“ (P-256), „ECDSA-SHA384“ (P-384), „ECDSA-SHA512“ (P-521), „RSASSA-PKCS1-v1_5-SHA256“.</param>
/// <param name="SignaturWertBase64">Bei Schlüssel: Signatur (WebCrypto-Format).</param>
/// <param name="OeffentlicherSchluesselJwk">Bei Schlüssel: öffentlicher Schlüssel als JWK (ohne private Anteile).</param>
/// <param name="BildPng">Bei Handschrift: das gezeichnete Bild.</param>
public sealed record Unterschrift(Signaturart Art, string? Algorithmus, string? SignaturWertBase64, string? OeffentlicherSchluesselJwk, byte[]? BildPng);

/// <summary>Prüft Signaturen aus dem Browser (WebCrypto) und gezeichnete Unterschriften.</summary>
public static class SignaturPruefung
{
    public const int MaxBildBytes = 400_000;

    public static readonly string[] Algorithmen = ["ECDSA-SHA256", "ECDSA-SHA384", "ECDSA-SHA512", "RSASSA-PKCS1-v1_5-SHA256"];

    /// <summary>Öffentlicher Schlüssel (nur die Teile, die RFC 7638 vorsieht) und sein Fingerabdruck.</summary>
    public sealed record OeffentlicherSchluessel(string Kty, string? Crv, string? X, string? Y, string? N, string? E)
    {
        /// <summary>Kanonisches JWK nach RFC 7638 (Mitglieder alphabetisch, ohne Leerzeichen).</summary>
        public string Kanonisch => Kty == "EC"
            ? $"{{\"crv\":\"{Crv}\",\"kty\":\"EC\",\"x\":\"{X}\",\"y\":\"{Y}\"}}"
            : $"{{\"e\":\"{E}\",\"kty\":\"RSA\",\"n\":\"{N}\"}}";

        public string Fingerabdruck => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Kanonisch)));

        /// <summary>Kurzform für Anzeige und PDF: 4er-Gruppen der ersten 64 Bit.</summary>
        public static string Kurz(string fingerabdruck) => string.Join(' ', Enumerable.Range(0, 4).Select(i => fingerabdruck.Substring(i * 4, 4))).ToUpperInvariant();
    }

    /// <summary>Liest ein JWK (öffentlich). Enthält es private Anteile („d“), wird es abgelehnt – die gehören nie auf den Server.</summary>
    public static OeffentlicherSchluessel JwkLesen(string jwk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jwk);
        using var dok = JsonDocument.Parse(jwk);
        var w = dok.RootElement;
        string? Feld(string name) => w.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (Feld("d") is not null || Feld("p") is not null)
        {
            throw new SignaturFehler("Der übermittelte Schlüssel enthält private Anteile. Es darf nur der öffentliche Schlüssel übertragen werden.");
        }

        var kty = Feld("kty");
        return kty switch
        {
            "EC" when Feld("crv") is "P-256" or "P-384" or "P-521" && Feld("x") is { } x && Feld("y") is { } y
                => new OeffentlicherSchluessel("EC", Feld("crv"), x, y, null, null),
            "RSA" when Feld("n") is { } n && Feld("e") is { } e => new OeffentlicherSchluessel("RSA", null, null, null, n, e),
            _ => throw new SignaturFehler("Schlüsseltyp nicht unterstützt. Möglich sind RSA (mind. 2048 Bit) und EC (P-256, P-384, P-521)."),
        };
    }

    /// <summary>Prüft eine Signatur über <paramref name="inhalt"/> (UTF-8). Wirft <see cref="SignaturFehler"/> mit verständlicher Meldung.</summary>
    public static OeffentlicherSchluessel SchluesselSignaturPruefen(string inhalt, string algorithmus, string signaturBase64, string jwk)
    {
        var schluessel = JwkLesen(jwk);
        byte[] signatur;
        try
        {
            signatur = Convert.FromBase64String(signaturBase64);
        }
        catch (FormatException)
        {
            throw new SignaturFehler("Der Signaturwert ist beschädigt.");
        }

        var daten = Encoding.UTF8.GetBytes(inhalt);
        bool gueltig;
        if (schluessel.Kty == "EC")
        {
            var (kurve, hash, erwartet) = schluessel.Crv switch
            {
                "P-256" => (ECCurve.NamedCurves.nistP256, HashAlgorithmName.SHA256, "ECDSA-SHA256"),
                "P-384" => (ECCurve.NamedCurves.nistP384, HashAlgorithmName.SHA384, "ECDSA-SHA384"),
                _ => (ECCurve.NamedCurves.nistP521, HashAlgorithmName.SHA512, "ECDSA-SHA512"),
            };
            if (algorithmus != erwartet)
            {
                throw new SignaturFehler($"Für einen {schluessel.Crv}-Schlüssel wird {erwartet} erwartet.");
            }

            using var ec = ECDsa.Create(new ECParameters { Curve = kurve, Q = new ECPoint { X = Base64Url(schluessel.X!), Y = Base64Url(schluessel.Y!) } });
            gueltig = ec.VerifyData(daten, signatur, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        else
        {
            if (algorithmus != "RSASSA-PKCS1-v1_5-SHA256")
            {
                throw new SignaturFehler("Für RSA-Schlüssel wird RSASSA-PKCS1-v1_5 mit SHA-256 erwartet.");
            }

            var modulus = Base64Url(schluessel.N!);
            if (modulus.Length * 8 < 2048)
            {
                throw new SignaturFehler("RSA-Schlüssel müssen mindestens 2048 Bit lang sein.");
            }

            using var rsa = RSA.Create(new RSAParameters { Modulus = modulus, Exponent = Base64Url(schluessel.E!) });
            gueltig = rsa.VerifyData(daten, signatur, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        return gueltig ? schluessel : throw new SignaturFehler("Die Signatur passt nicht zum angezeigten Text. Bitte erneut unterschreiben.");
    }

    /// <summary>Prüft das Bild einer gezeichneten Unterschrift (PNG, sinnvolle Größe).</summary>
    public static void BildPruefen(byte[]? png)
    {
        if (png is null || png.Length < 100)
        {
            throw new SignaturFehler("Bitte im Feld unterschreiben.");
        }

        if (png.Length > MaxBildBytes)
        {
            throw new SignaturFehler("Das Unterschriftsbild ist zu groß.");
        }

        ReadOnlySpan<byte> kennung = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!png.AsSpan(0, 8).SequenceEqual(kennung) || png.Length < 24)
        {
            throw new SignaturFehler("Das Unterschriftsbild ist kein PNG.");
        }

        var breite = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var hoehe = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (breite is < 100 or > 3000 || hoehe is < 40 or > 1500)
        {
            throw new SignaturFehler("Das Unterschriftsbild hat ein unerwartetes Format.");
        }
    }

    internal static byte[] Base64Url(string wert)
    {
        var s = wert.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            throw new SignaturFehler("Der öffentliche Schlüssel ist beschädigt.");
        }
    }
}

/// <summary>Unterschrift ungültig; Meldung ist für Menschen geschrieben.</summary>
public sealed class SignaturFehler : Exception
{
    public SignaturFehler()
    {
    }

    public SignaturFehler(string message) : base(message)
    {
    }

    public SignaturFehler(string message, Exception innerException) : base(message, innerException)
    {
    }
}
