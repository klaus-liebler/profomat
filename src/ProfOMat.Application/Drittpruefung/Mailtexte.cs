namespace ProfOMat.Application.Drittpruefung;

/// <summary>
/// Texte der Mails im Ablauf (förmliche Anrede, kurze Sätze, immer mit direktem Link). Studierende bekommen keine Mails:
/// Den Einladungslink schicken die Erstprüfenden selbst, den Stand sehen Studierende über diesen Link.
/// </summary>
internal static class Mailtexte
{
    private const string Fuss = "\n\n--\nDiese Nachricht wurde vom ProfOMat automatisch erzeugt.";

    public static (string Betreff, string Text) AnErstpruefende(AntragAnsicht a) => (
        $"Unterschrieben zurück: mündliche Drittprüfung {a.Pruefung.Titel} ({a.Antrag.Nummer})",
        $"""
        Guten Tag {a.Erstpruefende.Anzeigename},

        {a.Antrag.Name} (Matrikelnummer {a.Antrag.Matrikelnummer}, {a.Studiengang.Anzeige}) hat den Antrag auf mündliche
        Wiederholungsprüfung „{a.Pruefung.Anzeige}“ im dritten Versuch unterschrieben.

        Terminliche Einschränkungen und Wünsche: {a.Antrag.Terminwuensche ?? "keine angegeben"}

        Bitte ergänzen Sie Termin, Ort und Zweitprüfende (nach Rücksprache) und unterschreiben Sie dann hier:
        {a.Link}

        Der aktuelle Stand des Formulars hängt an.
        """ + Fuss);

    public static (string Betreff, string Text) AnZweitpruefende(AntragAnsicht a, string anrede) => (
        $"Bitte unterschreiben: mündliche Drittprüfung {a.Pruefung.Titel} ({a.Antrag.Nummer})",
        $"""
        Guten Tag {anrede},

        {a.Erstpruefende.Anzeigename} hat Sie als Zweitprüferin bzw. Zweitprüfer einer mündlichen Prüfung im dritten Versuch
        eingetragen und bereits unterschrieben:

        Prüfling: {a.Antrag.Name} ({a.Antrag.Matrikelnummer}), {a.Studiengang.Anzeige}
        Prüfung: {a.Pruefung.Anzeige}
        Termin: {a.Termin}
        Ort: {a.Antrag.Ort}

        Bitte unterschreiben Sie hier (oder geben Sie mit Rückfrage zurück):
        {a.Link}

        Der aktuelle Stand des Formulars hängt an.
        """ + Fuss);

    public static (string Betreff, string Text) AnStudiendekanat(AntragAnsicht a, string anrede) => (
        $"Bitte bestellen: mündliche Drittprüfung {a.Pruefung.Titel} ({a.Antrag.Nummer})",
        $"""
        Guten Tag {anrede},

        die Prüfenden haben die mündliche Drittprüfung festgelegt und unterschrieben:

        Prüfling: {a.Antrag.Name} ({a.Antrag.Matrikelnummer}), {a.Studiengang.Anzeige}
        Prüfung: {a.Pruefung.Anzeige}
        Termin: {a.Termin}
        Ort: {a.Antrag.Ort}
        Prüfende: {a.Erstpruefende.Anzeigename} und {a.Antrag.ZweitpruefendeName}

        Bitte prüfen und unterschreiben Sie die Bestellung der Prüfenden hier:
        {a.Link}

        Danach laden Sie dort das vollständige Formular herunter und schicken es an das Studierendensekretariat ({a.SekretariatEmail}).
        """ + Fuss);

    public static (string Betreff, string Text) AbgeschlossenAnPruefende(AntragAnsicht a, string anrede) => (
        $"Bestellt: mündliche Drittprüfung am {a.Termin} ({a.Antrag.Nummer})",
        $"""
        Guten Tag {anrede},

        das Studiendekanat hat die Prüfenden der mündlichen Prüfung im dritten Versuch bestellt:

        Prüfling: {a.Antrag.Name}
        Prüfung: {a.Pruefung.Anzeige}
        Termin: {a.Termin}
        Ort: {a.Antrag.Ort}
        Prüfende: {a.Erstpruefende.Anzeigename} und {a.Antrag.ZweitpruefendeName}

        Das vollständig unterschriebene Formular hängt an. Stand und Unterschriften: {a.Link}
        """ + Fuss);

    /// <summary>Nur für Anträge aus dem alten Verfahren (mit Mailadresse der Studierenden).</summary>
    public static (string Betreff, string Text) Abgelehnt(AntragAnsicht a) => (
        $"Ihr Antrag auf mündliche Drittprüfung {a.Antrag.Nummer} wurde abgelehnt",
        $"""
        Guten Tag {a.Antrag.Name},

        Ihr Antrag für „{a.Pruefung.Anzeige}“ wurde von {a.Antrag.AbgelehntVon} abgelehnt.
        Begründung: {a.Antrag.Ablehnungsgrund}

        Bitte wenden Sie sich an Ihre Prüferin bzw. Ihren Prüfer. Der Antrag ist bis zum Ende des regulären
        Prüfungsanmeldezeitraums zu stellen (ATPO § 18 Abs. 1 Satz 4).
        """ + Fuss);

    public static (string Betreff, string Text) Zurueckgegeben(AntragAnsicht a, string von, string grund) => (
        $"Rückfrage zur mündlichen Drittprüfung {a.Antrag.Nummer}",
        $"""
        Guten Tag {a.Erstpruefende.Anzeigename},

        {von} hat den Antrag mit folgender Rückfrage an Sie zurückgegeben:
        {grund}

        Ihre Unterschrift und die folgenden gelten damit nicht mehr. Bitte passen Sie die Angaben an und unterschreiben Sie erneut:
        {a.Link}
        """ + Fuss);

    public static (string Betreff, string Text) Zurueckgezogen(AntragAnsicht a, string anrede) => (
        $"Zurückgezogen: Antrag auf mündliche Drittprüfung {a.Antrag.Nummer}",
        $"""
        Guten Tag {anrede},

        {a.Antrag.Name} hat den Antrag auf mündliche Drittprüfung für „{a.Pruefung.Anzeige}“ zurückgezogen.
        {a.Link}
        """ + Fuss);
}
