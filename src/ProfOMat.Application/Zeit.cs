using System.Globalization;
using Microsoft.Extensions.Options;

namespace ProfOMat.Application;

/// <summary>Ortszeit für Anzeige und Eingabe (gespeichert wird UTC).</summary>
public sealed class Ortszeit(IOptions<ProfOMatOptionen> optionen)
{
    private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(optionen.Value.Zeitzone);

    public DateTime Lokal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, zone).DateTime;

    /// <summary>Lokale Eingabe (ohne Zone) → UTC.</summary>
    public DateTimeOffset AlsUtc(DateTime lokal)
    {
        var ohneArt = DateTime.SpecifyKind(lokal, DateTimeKind.Unspecified);
        return new DateTimeOffset(ohneArt, zone.GetUtcOffset(ohneArt)).ToUniversalTime();
    }

    /// <summary>„Donnerstag, 03.12.2026, 10:00 Uhr“.</summary>
    public string Termin(DateTimeOffset utc) => Lokal(utc).ToString("dddd, dd.MM.yyyy, HH:mm 'Uhr'", Deutsch);

    /// <summary>„03.12.2026 10:00“.</summary>
    public string Kurz(DateTimeOffset utc) => Lokal(utc).ToString("dd.MM.yyyy HH:mm", Deutsch);

    public string Datum(DateTimeOffset utc) => Lokal(utc).ToString("dd.MM.yyyy", Deutsch);

    /// <summary>Klausurtermin: wie <see cref="Termin"/>, aber ohne Uhrzeit, wenn keine angegeben ist (00:00) – „Donnerstag, 03.12.2026“.</summary>
    public string Klausurtermin(DateTimeOffset utc) => OhneUhrzeit(utc) ? Lokal(utc).ToString("dddd, dd.MM.yyyy", Deutsch) : Termin(utc);

    /// <summary>Klausurtermin kurz: „03.12.2026 10:00“ bzw. „03.12.2026“ ohne Uhrzeit.</summary>
    public string KlausurterminKurz(DateTimeOffset utc) => OhneUhrzeit(utc) ? Datum(utc) : Kurz(utc);

    /// <summary>Uhrzeit des Klausurtermins („10:00“) oder leer, wenn keine angegeben ist.</summary>
    public string Klausuruhrzeit(DateTimeOffset utc) => OhneUhrzeit(utc) ? "" : Lokal(utc).ToString("HH:mm", Deutsch);

    private bool OhneUhrzeit(DateTimeOffset utc) => Lokal(utc).TimeOfDay == TimeSpan.Zero;
}
