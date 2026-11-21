namespace ProfOMat.Application.Drittpruefung;

/// <summary>Texte der Mails im Ablauf (förmliche Anrede, kurze Sätze, immer mit direktem Link).</summary>
internal static class Mailtexte
{
    private const string Fuss = "\n\n--\nDiese Nachricht wurde vom ProfOMat automatisch erzeugt.";

    public static (string Betreff, string Text) Bestaetigen(AntragAnsicht a, string link, int fristTage) => (
        $"Bitte bestätigen: Antrag auf mündliche Drittprüfung {a.Antrag.Nummer}",
        $"""
        Guten Tag {a.Antrag.Name},

        Sie haben beantragt, die Prüfung „{a.Pruefung.Anzeige}“ ({a.Studiengang.Anzeige}) im dritten Versuch mündlich abzulegen.
        Gewünschte/r Erstprüfende/r: {a.Erstpruefende.Anzeigename}

        Bitte bestätigen Sie den Antrag innerhalb von {fristTage} Tagen über diesen Link. Erst dann geht er an die/den Erstprüfende/n:
        {link}

        Über denselben Link sehen Sie später den Stand und können den Antrag zurückziehen. Bitte geben Sie ihn nicht weiter.
        Wenn Sie den Antrag nicht gestellt haben, ignorieren Sie diese Mail; er verfällt dann.
        """ + Fuss);

    public static (string Betreff, string Text) AnErstpruefende(AntragAnsicht a) => (
        $"Neuer Antrag auf mündliche Drittprüfung: {a.Pruefung.Titel} ({a.Antrag.Nummer})",
        $"""
        Guten Tag {a.Erstpruefende.Anzeigename},

        {a.Antrag.Name} (Matrikelnummer {a.Antrag.Matrikelnummer}, {a.Studiengang.Anzeige}) bittet Sie, die Prüfung
        „{a.Pruefung.Anzeige}“ im dritten Versuch als mündliche Prüfung abzunehmen (ATPO § 18 Abs. 1 Satz 3).

        Terminliche Einschränkungen und Wünsche: {a.Antrag.Terminwuensche ?? "keine angegeben"}
        Wunsch Zweitprüfende/r: {a.Antrag.Zweitpruefungswunsch ?? "keiner angegeben"}

        Bitte stimmen Sie Termin und Ort mit der/dem Zweitprüfenden ab und unterschreiben Sie dann hier:
        {a.Link}
        """ + Fuss);

    public static (string Betreff, string Text) AnStudiendekanat(AntragAnsicht a, string anrede) => (
        $"Bitte bestellen: mündliche Drittprüfung {a.Pruefung.Titel} ({a.Antrag.Nummer})",
        $"""
        Guten Tag {anrede},

        {a.Erstpruefende.Anzeigename} hat die mündliche Drittprüfung festgelegt und unterschrieben:

        Prüfling: {a.Antrag.Name} ({a.Antrag.Matrikelnummer}), {a.Studiengang.Anzeige}
        Prüfung: {a.Pruefung.Anzeige}
        Termin: {a.Termin}
        Ort: {a.Antrag.Ort}
        Prüfende: {a.Erstpruefende.Anzeigename} und {a.Antrag.ZweitpruefendeName}

        Bitte prüfen und unterschreiben Sie die Bestellung der Prüfenden hier:
        {a.Link}
        """ + Fuss);

    public static (string Betreff, string Text) TerminAnStudierende(AntragAnsicht a) => (
        $"Termin für Ihre mündliche Drittprüfung {a.Antrag.Nummer} (vorläufig)",
        $"""
        Guten Tag {a.Antrag.Name},

        {a.Erstpruefende.Anzeigename} hat für „{a.Pruefung.Anzeige}“ folgenden Termin vorgesehen:

        Termin: {a.Termin}
        Ort: {a.Antrag.Ort}
        Zweitprüfende/r: {a.Antrag.ZweitpruefendeName}

        Der Termin wird verbindlich, sobald das Studiendekanat die Prüfenden bestellt hat; Sie erhalten dann eine weitere Mail.
        Den Stand sehen Sie jederzeit über den Link aus Ihrer Bestätigungsmail.
        """ + Fuss);

    public static (string Betreff, string Text) AnSekretariat(AntragAnsicht a) => (
        $"Mündliche Drittprüfung {a.Antrag.Matrikelnummer} – {a.Pruefung.Titel} ({a.Antrag.Nummer})",
        $"""
        Guten Tag,

        anbei der vollständig unterschriebene Antrag auf mündliche Prüfung im dritten Versuch (ATPO § 18 Abs. 1 Satz 3):

        Prüfling: {a.Antrag.Name}, Matrikelnummer {a.Antrag.Matrikelnummer}
        Studiengang: {a.Studiengang.Anzeige}
        Prüfung: {a.Pruefung.Anzeige}
        Termin: {a.Termin}
        Ort: {a.Antrag.Ort}
        Prüfende: {a.Erstpruefende.Anzeigename} (Erstprüfung), {a.Antrag.ZweitpruefendeName} (Zweitprüfung)
        Bestellt durch: {a.Studiendekanat?.Anzeigename}

        Prüfwert (SHA-256): {a.Pruefwert}
        Die Unterschriften sind im ProfOMat gespeichert und dort prüfbar: {a.Link}
        """ + Fuss);

    public static (string Betreff, string Text) AbgeschlossenAnBeteiligte(AntragAnsicht a, string anrede, string? link) => (
        $"Bestätigt: mündliche Drittprüfung am {a.Termin} ({a.Antrag.Nummer})",
        $"""
        Guten Tag {anrede},

        die mündliche Prüfung im dritten Versuch ist bestellt und an das Studierendensekretariat gemeldet:

        Prüfling: {a.Antrag.Name}
        Prüfung: {a.Pruefung.Anzeige}
        Termin: {a.Termin}
        Ort: {a.Antrag.Ort}
        Prüfende: {a.Erstpruefende.Anzeigename} und {a.Antrag.ZweitpruefendeName}

        Das unterschriebene Dokument hängt an.{(link is null ? "" : $"\nStand und Dokument: {link}")}
        """ + Fuss);

    public static (string Betreff, string Text) Abgelehnt(AntragAnsicht a) => (
        $"Ihr Antrag auf mündliche Drittprüfung {a.Antrag.Nummer} wurde abgelehnt",
        $"""
        Guten Tag {a.Antrag.Name},

        Ihr Antrag für „{a.Pruefung.Anzeige}“ wurde von {a.Antrag.AbgelehntVon} abgelehnt.
        Begründung: {a.Antrag.Ablehnungsgrund}

        Sie können einen neuen Antrag stellen und dabei eine andere Erstprüferin oder einen anderen Erstprüfer wählen.
        Bitte beachten Sie die Frist: Der Antrag ist bis zum Ende des regulären Prüfungsanmeldezeitraums zu stellen (ATPO § 18 Abs. 1 Satz 4).
        Den Stand sehen Sie weiterhin über den Link aus Ihrer Bestätigungsmail.
        """ + Fuss);

    public static (string Betreff, string Text) Zurueckgegeben(AntragAnsicht a, string grund) => (
        $"Rückfrage des Studiendekanats zur Drittprüfung {a.Antrag.Nummer}",
        $"""
        Guten Tag {a.Erstpruefende.Anzeigename},

        das Studiendekanat hat die Festlegung zurückgegeben:
        {grund}

        Bitte passen Sie die Angaben an und unterschreiben Sie erneut:
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
