using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProfOMat.Domain.Gemeinsam;

namespace ProfOMat.Application.Betrieb;

/// <summary>Rechenaufgabe für den Browser: finde <c>n ≤ Maximum</c> mit <c>SHA-256(Salz + n) = Herausforderung</c> (Hex).</summary>
public sealed record Rechenaufgabe(string Salz, string Herausforderung, string Signatur, int Maximum);

/// <summary>Antwort des Browsers; <paramref name="Koeder"/> ist das unsichtbare Feld, das Menschen leer lassen.</summary>
public sealed record Rechenloesung(string Salz, int Zahl, string Signatur, string? Koeder);

/// <summary>
/// Captcha für öffentliche Formulare ohne Anmeldung (Antrag der Studierenden) – selbst gehostet nach dem ALTCHA-Prinzip
/// (Proof-of-Work): kein Bilderrätsel, keine Cookies, keine Daten an Dritte, barrierefrei. Der Browser rechnet im Mittel
/// rund eine Sekunde; massenhaftes automatisches Absenden wird dadurch teuer. Aufgaben sind mit einem geheimen Schlüssel
/// signiert (zustandslos), laufen nach <see cref="Gueltigkeit"/> ab und sind nur einmal verwendbar. Dazu ein Köderfeld.
/// </summary>
public sealed class Menschenpruefung(TimeProvider uhr)
{
    public static readonly TimeSpan Gueltigkeit = TimeSpan.FromMinutes(10);
    public const int StandardMaximum = 100_000;

    private readonly byte[] schluessel = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, DateTimeOffset> verbraucht = new(StringComparer.Ordinal);

    public int Maximum { get; init; } = StandardMaximum;

    public Rechenaufgabe Neu()
    {
        var ablauf = uhr.GetUtcNow().Add(Gueltigkeit).ToUnixTimeSeconds();
        var salz = $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}.{ablauf.ToString(CultureInfo.InvariantCulture)}";
        var zahl = RandomNumberGenerator.GetInt32(Maximum + 1);
        var herausforderung = Sha256(salz + zahl.ToString(CultureInfo.InvariantCulture));
        return new Rechenaufgabe(salz, herausforderung, Signieren(herausforderung), Maximum);
    }

    /// <summary>Wirft mit verständlicher Meldung, wenn die Lösung nicht passt (falsch, manipuliert, abgelaufen, schon benutzt).</summary>
    public void Pruefen(Rechenloesung? loesung)
    {
        const string Hinweis = "Die automatische Sicherheitsprüfung ist fehlgeschlagen. Bitte die Seite neu laden und erneut absenden.";
        if (loesung is null || !string.IsNullOrEmpty(loesung.Koeder) || loesung.Zahl < 0 || loesung.Zahl > Maximum
            || string.IsNullOrEmpty(loesung.Salz) || loesung.Salz.Length > 64 || string.IsNullOrEmpty(loesung.Signatur))
        {
            throw new FachlicherFehler(Hinweis);
        }

        var jetzt = uhr.GetUtcNow();
        var teile = loesung.Salz.Split('.');
        if (teile.Length != 2 || !long.TryParse(teile[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ablauf)
            || DateTimeOffset.FromUnixTimeSeconds(ablauf) < jetzt)
        {
            throw new FachlicherFehler("Die Sicherheitsprüfung ist abgelaufen. Bitte erneut absenden.");
        }

        var erwartet = Signieren(Sha256(loesung.Salz + loesung.Zahl.ToString(CultureInfo.InvariantCulture)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(erwartet), Encoding.ASCII.GetBytes(loesung.Signatur.ToLowerInvariant())))
        {
            throw new FachlicherFehler(Hinweis);
        }

        foreach (var alt in verbraucht.Where(v => v.Value < jetzt).Select(v => v.Key).ToList())
        {
            verbraucht.TryRemove(alt, out _);
        }

        if (!verbraucht.TryAdd(loesung.Salz, DateTimeOffset.FromUnixTimeSeconds(ablauf)))
        {
            throw new FachlicherFehler(Hinweis);
        }
    }

    /// <summary>Löst eine Aufgabe wie der Browser (für Tests).</summary>
    public static Rechenloesung Loesen(Rechenaufgabe a)
    {
        ArgumentNullException.ThrowIfNull(a);
        for (var n = 0; n <= a.Maximum; n++)
        {
            if (Sha256(a.Salz + n.ToString(CultureInfo.InvariantCulture)) == a.Herausforderung)
            {
                return new Rechenloesung(a.Salz, n, a.Signatur, null);
            }
        }

        throw new InvalidOperationException("Keine Lösung gefunden.");
    }

    private string Signieren(string herausforderung) => Convert.ToHexString(HMACSHA256.HashData(schluessel, Encoding.ASCII.GetBytes(herausforderung))).ToLowerInvariant();

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
