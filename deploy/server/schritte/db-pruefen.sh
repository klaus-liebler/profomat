# Datenbank vor dem Ausrollen prüfen – ÄNDERT NICHTS (wird per „source“ eingebunden; erwartet gemeinsam.sh und pruefen.sh).
# Vergleicht die Schemaversion der vorhandenen SQLite-Datei mit dem, was das neue Paket kann:
#   schemaversion  = Version, die das Programm erwartet
#   migrierbar_ab  = älteste Version, die das Programm beim Start ohne Datenverlust aufwertet (Migration im Code)
# Quelle: schemainfo.txt im Paket (deploy.ps1 schreibt sie mit „ProfOMat.Web --schemainfo“), sonst das Programm selbst.

DB_LAGE=""            # neu | gleich | migration | zu_alt | zu_neu | unlesbar | unbekannt
DB_VERSION_IST=""
DB_VERSION_SOLL=""
DB_MIGRIERBAR_AB=""

_schemainfo_lesen() {   # <programmordner> → setzt DB_VERSION_SOLL und DB_MIGRIERBAR_AB
    local ordner="$1" text=""
    if [[ -f "$ordner/schemainfo.txt" ]]; then
        text=$(cat "$ordner/schemainfo.txt")
    elif [[ "$MODUS" != "docker" && -x "$ordner/$APP" ]]; then
        text=$(timeout 30 runuser -u "$DIENST" -- "$ordner/$APP" --schemainfo 2>/dev/null || true)
    fi
    DB_VERSION_SOLL=$(sed -n 's/^schemaversion=\([0-9]\+\)\r\?$/\1/p' <<<"$text")
    DB_MIGRIERBAR_AB=$(sed -n 's/^migrierbar_ab=\([0-9]\+\)\r\?$/\1/p' <<<"$text")
}

# Text der großen roten Warnung bei -DbNeu (auch von ausrollen.sh --zurueck genutzt)
DBNEU_WARNUNG=(
    "-DbNeu: Die vorhandene Datenbank wird gesichert und beiseitegelegt,"
    "aber NICHT in die neue Version übernommen."
    "Nach dem Start sind in der Anwendung ALLE DATEN WEG"
    "(Konten, Anträge, Klausuren, Finanzen …) – Ersteinrichtung nötig."
    "Zurück kommen die Daten nur mit dem Rückweg (ausrollen.sh --zurueck),"
    "der vorige Version UND vorige Datenbank wieder einsetzt."
)

# db_planen <neuer-programmordner> <db_neu 0|1>
db_planen() {
    local ordner="$1" db_neu="$2"
    _schemainfo_lesen "$ordner"

    if [[ ! -f "$DB" ]]; then
        DB_LAGE="neu"; ok "Datenbank: noch keine vorhanden – die Anwendung legt $DB beim Start an (danach Ersteinrichtung)"
        return
    fi
    DB_VERSION_IST=$(sqlite3 -readonly "$DB" "SELECT Version FROM Schemainfo LIMIT 1;" 2>/dev/null || true)
    if [[ ! "$DB_VERSION_IST" =~ ^[0-9]+$ ]]; then
        DB_LAGE="unlesbar"
    elif [[ -z "$DB_VERSION_SOLL" ]]; then
        DB_LAGE="unbekannt"
    elif (( DB_VERSION_IST == DB_VERSION_SOLL )); then
        DB_LAGE="gleich"
    elif (( DB_VERSION_IST > DB_VERSION_SOLL )); then
        DB_LAGE="zu_neu"
    elif [[ -n "$DB_MIGRIERBAR_AB" ]] && (( DB_VERSION_IST >= DB_MIGRIERBAR_AB )); then
        DB_LAGE="migration"
    else
        DB_LAGE="zu_alt"
    fi

    case "$DB_LAGE" in
        gleich)    ok "Datenbank: Schemaversion $DB_VERSION_IST passt zum Paket – keine Migration nötig" ;;
        migration) ok "Datenbank: Version $DB_VERSION_IST → $DB_VERSION_SOLL, Migration ist im Code enthalten (ab Version $DB_MIGRIERBAR_AB)"
                   info "   Die Anwendung wertet die Datenbank beim Start auf; die Sicherung davor setzt ein Rückweg wieder ein." ;;
        unbekannt) warn "Datenbank: Version $DB_VERSION_IST; welche Version das Paket erwartet, ist unbekannt (keine schemainfo.txt)."
                   warn "   Weiter ohne Vorabprüfung – passt das Schema nicht, erkennt das die Startbeobachtung und rollt zurück." ;;
    esac

    if (( db_neu )); then
        case "$DB_LAGE" in
            gleich|migration) warn "-DbNeu ist hier NICHT nötig: Die Datenbank (Version $DB_VERSION_IST) könnte übernommen werden." ;;
        esac
        achtung "${DBNEU_WARNUNG[@]}"
        return
    fi

    case "$DB_LAGE" in
        zu_alt)
            achtung "Die Datenbank (Schemaversion $DB_VERSION_IST) ist zu alt für dieses Paket (erwartet $DB_VERSION_SOLL," \
                    "Migration im Code erst ab Version $DB_MIGRIERBAR_AB)." \
                    "Ausrollen geht nur mit -DbNeu – und dann gilt:" "" "${DBNEU_WARNUNG[@]}"
            pruef_fehler "Datenbank $DB_VERSION_IST nicht migrierbar auf $DB_VERSION_SOLL – nur mit  .\\deploy\\server\\deploy.ps1 -DbNeu  (siehe Warnung oben)" ;;
        zu_neu)
            pruef_fehler "Datenbank (Version $DB_VERSION_IST) ist NEUER als dieses Paket (erwartet $DB_VERSION_SOLL) – älteres Programm? Für den Weg zurück: ausrollen.sh --zurueck (setzt auch die Datenbank zurück). Sonst nur mit -DbNeu." ;;
        unlesbar)
            pruef_fehler "Datenbank $DB hat keine lesbare Schemaversion (Tabelle Schemainfo) – beschädigt oder fremd? Nur mit -DbNeu (legt sie beiseite)." ;;
    esac
}

# Aktuelle Version der Datei (nach dem Start; Dienst gestoppt) – für den Rückweg
db_version_jetzt() { sqlite3 -readonly "$DB" "SELECT Version FROM Schemainfo LIMIT 1;" 2>/dev/null || true; }

# db_beiseite <kennung>: Datei samt -wal/-shm nach $BEISEITE/<name>.<kennung> verschieben (nie löschen)
db_beiseite() {
    local kennung="$1" f
    install -d -o "$DIENST" -g "$DIENST" -m 0700 "$BEISEITE"
    for f in "$DB" "$DB-wal" "$DB-shm"; do
        [[ -e "$f" ]] || continue
        mv "$f" "$BEISEITE/$(basename "$f").$kennung"
        ok "beiseitegelegt: $f → $BEISEITE/$(basename "$f").$kennung"
    done
}
# db_zurueckholen <kennung>: mit db_beiseite verschobene Dateien wieder einsetzen
db_zurueckholen() {
    local kennung="$1" f
    for f in "$DB" "$DB-wal" "$DB-shm"; do
        if [[ -e "$BEISEITE/$(basename "$f").$kennung" ]]; then mv "$BEISEITE/$(basename "$f").$kennung" "$f"; fi
    done
    ok "Datenbank von $BEISEITE (…$kennung) zurückgeholt"
}
# db_aus_sicherung <sicherungsdatei>: Sicherung (sqlite3 .backup) als Datenbank einsetzen
db_aus_sicherung() {
    local datei="$1"
    [[ -f "$datei" ]] || fehler "Sicherung $datei fehlt – Datenbank bitte von Hand aus $SICHERUNGEN zurücksichern."
    install -o "$DIENST" -g "$DIENST" -m 0640 "$datei" "$DB"
    ok "Datenbank aus der Sicherung $datei eingesetzt"
}
