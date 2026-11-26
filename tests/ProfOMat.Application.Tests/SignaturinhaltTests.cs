using ProfOMat.Application.Signaturen;
using ProfOMat.Domain.Drittpruefung;

namespace ProfOMat.Application.Tests;

public sealed class SignaturinhaltTests
{
    private static Antragstext Text(string? wuensche = null) => new(Guid.Parse("01a1fe80-0000-7000-8000-000000000001"), "DP-2026-ABCDEF",
        new DateTimeOffset(2026, 11, 20, 9, 0, 0, 123, TimeSpan.Zero), "Mia", "Muster", "1234567", "mia@hs-osnabrueck.de",
        "Elektrotechnik (B.Sc., PO 2023)", "stg:ET", "12040 – Regelungstechnik", "pnr:12040", "Prof. Dr. Erika Mustermann",
        Guid.Parse("01a1fe80-0000-7000-8000-000000000002"), wuensche, null);

    [Fact]
    public void Text_ist_deterministisch_und_nur_LF()
    {
        var zeit = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.FromHours(1)).AddTicks(5);
        var a = Signaturinhalt.Erstellen(Signaturschritt.Antragstellung, Text("a\r\nb"), null, null, "Mia Muster", zeit);
        var b = Signaturinhalt.Erstellen(Signaturschritt.Antragstellung, Text("a\nb"), null, null, "Mia Muster", zeit);
        a.ShouldBe(b);
        a.ShouldNotContain("\r");
        a.ShouldContain("Zeitpunkt: 2026-11-20T09:00:00.000Z");
        a.ShouldContain("Erstellt: 2026-11-20T09:00:00.123Z");
    }

    [Fact]
    public void Mehrzeiliger_Freitext_kann_keine_Felder_einschmuggeln()
    {
        var t = Signaturinhalt.Erstellen(Signaturschritt.Antragstellung, Text("ok\nMatrikelnummer: 999"), null, null, "Mia Muster", DateTimeOffset.UnixEpoch);
        t.Split('\n').Count(z => z.StartsWith("Matrikelnummer:", StringComparison.Ordinal)).ShouldBe(1);
        t.ShouldContain("\n    Matrikelnummer: 999");
    }

    [Fact]
    public void Folgestufen_enthalten_Vorgaenger_und_Festlegung()
    {
        var f = new Festlegungstext("Freitag, 04.12.2026, 10:00 Uhr (Europe/Berlin)", "SI 0012", "Prof. Dr. Lena Probst", null);
        var t = Signaturinhalt.Erstellen(Signaturschritt.Studiendekanat, Text(), "abc123", f, "Prof. Dr.-Ing. Max Beispiel", DateTimeOffset.UnixEpoch);
        t.ShouldContain("Vorherige Unterschrift (SHA-256): abc123");
        t.ShouldContain("Ort: SI 0012");
        t.ShouldContain("[Bestellung durch das Studiendekanat]");
    }

    private static Vorgangstext V2(string? wuensche = null, string? termin = null) => new(Guid.Parse("01a1fe80-0000-7000-8000-000000000001"), "DP-2026-ABCDEF",
        new DateTimeOffset(2026, 11, 20, 9, 0, 0, 123, TimeSpan.Zero), "Mia", "Muster", "1234567", "Elektrotechnik (B.Sc., PO 2023)", "stg:ET",
        "12040 – Regelungstechnik", "pnr:12040", wuensche, "Prof. Dr. Erika Mustermann", Guid.Parse("01a1fe80-0000-7000-8000-000000000002"),
        "Prof. Dr. Lena Probst", Guid.Parse("01a1fe80-0000-7000-8000-000000000003"), termin, termin is null ? null : "SI 0012", null);

    [Fact]
    public void V2_ist_festgeschrieben_und_alte_Unterschriften_bleiben_nachrechenbar()
    {
        // Ändert sich dieser Hash, lassen sich gespeicherte Unterschriften nicht mehr nachbilden: Format nie ändern, sondern v3 einführen.
        var t = Signaturinhalt.ErstellenV2(Signaturschritt.Zweitpruefung, V2("a\r\nb", "Freitag, 04.12.2026, 10:00 Uhr (Europe/Berlin)"), "abc123",
            "Prof. Dr. Lena Probst", DateTimeOffset.UnixEpoch);
        t.ShouldNotContain("\r");
        t.ShouldContain("Format: profomat-antrag-v2");
        t.ShouldContain("Terminliche Einschränkungen und Wünsche: a\n    b");
        Signaturinhalt.Hash(t).ShouldBe("2c6273609cad12a741211a7c0cb23b4c568d9dc77821f9d81017e5c8870c33af");
    }

    [Fact]
    public void V2_Felder_lassen_sich_lesen_und_Freitext_schmuggelt_keine_Felder()
    {
        var t = Signaturinhalt.ErstellenV2(Signaturschritt.Antragstellung, V2("ok\nModul: Gefälscht"), null, "Mia Muster", DateTimeOffset.UnixEpoch);
        Signaturinhalt.Feld(t, "Modul").ShouldBe("12040 – Regelungstechnik");
        Signaturinhalt.Feld(t, "Erstprüfende/r").ShouldBe("Prof. Dr. Erika Mustermann");
        Signaturinhalt.Feld(t, "Termin").ShouldBe("-");
        t.ShouldContain("Erklärung: Ich beantrage");
    }

    [Fact]
    public void Private_Schluesselanteile_werden_abgelehnt()
        => Should.Throw<SignaturFehler>(() => SignaturPruefung.JwkLesen("""{"kty":"EC","crv":"P-256","x":"a","y":"b","d":"geheim"}"""))
            .Message.ShouldContain("private Anteile");
}
