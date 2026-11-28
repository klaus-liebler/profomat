# Gemeinsame Hilfen für die Schritt-Skripte des Deploy-Kits (wird per „source“ eingebunden, nicht direkt starten).
# KIT-DATEI: in allen vier Systemen (ProfOMat, FraktionPerfekt, VibeVHS, SchulPerfekt) gleich – Unterschiede nur in
# konfiguration.env, umgebung.kit, umgebung.beispiel und nginx/locations.conf (siehe ../README.md).
# Grundsätze: idempotent, Sicherung vorher (/root/<dienst>-sicherung/<Zeit>-<Schritt>), Prüfung danach, Rückweg.
set -euo pipefail

KIT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=../konfiguration.env
source "$KIT/konfiguration.env"
: "${DIENST:?DIENST fehlt in konfiguration.env}" "${APP:?APP fehlt}" "${PORT:?PORT fehlt}" "${PFAD:?PFAD fehlt}" "${HOST:?HOST fehlt}"
SYSTEM="${SYSTEM:-$DIENST}"
SICHERUNG="/root/$DIENST-sicherung/$(date +%Y%m%d-%H%M%S)-$(basename "$0" .sh)"
PROGRAMM="/opt/$DIENST"
DATEN="/var/lib/$DIENST"
LOGS="/var/log/$DIENST"
SICHERUNGEN="/var/backups/$DIENST"
UMGEBUNG="/etc/$DIENST/$DIENST.env"
PG_UMGEBUNG="/etc/$DIENST/postgres.env"
MODUS="${MODUS:-framework}"
WARTEZEIT="${WARTEZEIT:-120}"
GESUND="http://127.0.0.1:$PORT$PFAD${GESUND_PFAD:-/gesund}"
DB_CONTAINER="${DB_CONTAINER:-$DIENST-db}"
DB_NETZ="${DB_NETZ:-$DIENST-netz}"
DB_NAME="${DB_NAME:-$DIENST}"
DB_PORT="${DB_PORT:?DB_PORT fehlt in konfiguration.env}"
VERBINDUNG="${VERBINDUNG:?VERBINDUNG fehlt in konfiguration.env}"
DOCKER_ORDNER="/opt/$DIENST-docker"
SQLITE_ALT="${SQLITE_ALT:-}"
SICHERN_ORDNER="${SICHERN_ORDNER:-schluessel}"
VERBOTEN_MUSTER="${VERBOTEN_MUSTER:-^\s*(Anmeldung__TestanmeldungErlaubt|Demodaten__Anlegen)\s*=\s*[Tt]rue}"

info()  { printf '\033[1;34m»\033[0m %s\n' "$*"; }
ok()    { printf '\033[1;32m✓\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33m!\033[0m %s\n' "$*"; }
fehler(){ printf '\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }
rot()   { printf '\033[1;31m%s\033[0m\n' "$*"; }
abschnitt() { printf '\n\033[1;36m══ %s ══\033[0m\n' "$*"; }

# Große rote Warnung (Datenverlust in der Anwendung, Rückweg mit Datenbank): Banner „ACHTUNG“ in Blockbuchstaben, darunter der Text.
achtung() {
    local zeile
    printf '\033[1;31m'
    cat <<'BANNER'

   █████   ██████ ██   ██ ████████ ██    ██ ███    ██  ██████
  ██   ██ ██      ██   ██    ██    ██    ██ ████   ██ ██
  ███████ ██      ███████    ██    ██    ██ ██ ██  ██ ██   ███
  ██   ██ ██      ██   ██    ██    ██    ██ ██  ██ ██ ██    ██
  ██   ██  ██████ ██   ██    ██     ██████  ██   ████  ██████

BANNER
    for zeile in "$@"; do printf '  %s\n' "$zeile"; done
    printf '\033[0m\n'
}

als_root() { [[ $EUID -eq 0 ]] || fehler "Bitte mit sudo starten: sudo bash $0"; }

sichern() {
    mkdir -p "$SICHERUNG"
    for p in "$@"; do
        if [[ -e "$p" ]]; then
            cp -a --parents "$p" "$SICHERUNG/"
            info "gesichert: $p → $SICHERUNG$p"
        fi
    done
}

# ---------- Vorlagen ----------
# Feste Umgebung der Anwendung aus umgebung.kit für eine Betriebsart (nativ | docker): Zeilen „SCHLÜSSEL=WERT“, Platzhalter gefüllt.
umgebung_zeilen() {   # <nativ|docker>
    local art="$1" zeile
    [[ -f "$KIT/umgebung.kit" ]] || return 0
    while IFS= read -r zeile || [[ -n "$zeile" ]]; do
        zeile="${zeile%$'\r'}"
        [[ "$zeile" =~ ^[[:space:]]*(#|$) ]] && continue
        case "$zeile" in
            nativ:*)  [[ "$art" == nativ ]] || continue; zeile="${zeile#nativ:}" ;;
            docker:*) [[ "$art" == docker ]] || continue; zeile="${zeile#docker:}" ;;
        esac
        _platzhalter <<<"$zeile"
    done < "$KIT/umgebung.kit"
}
_platzhalter() {
    sed -e "s|@HOST@|$HOST|g" -e "s|@PFAD@|$PFAD|g" -e "s|@PORT@|$PORT|g" -e "s|@DIENST@|$DIENST|g" -e "s|@APP@|$APP|g" \
        -e "s|@SYSTEM@|$SYSTEM|g" -e "s|@DB_PORT@|$DB_PORT|g" -e "s|@DB_NAME@|$DB_NAME|g" -e "s|@DB_CONTAINER@|$DB_CONTAINER|g" \
        -e "s|@DB_NETZ@|$DB_NETZ|g" -e "s|@VERBINDUNG@|$VERBINDUNG|g" -e "s|@SICHERN_ORDNER@|$SICHERN_ORDNER|g"         -e "s|@SICHERN_ORDNER_TEXT@|${SICHERN_ORDNER// /, }|g" -e "s|@SQLITE_ALT@|$SQLITE_ALT|g"
}
# Vorlage aus dem Kit füllen und ausgeben. Zeilen „@UMGEBUNG_SYSTEMD@“ bzw. „@UMGEBUNG_COMPOSE@“ werden durch umgebung.kit ersetzt.
vorlage_fuellen() {   # <pfad-im-kit>
    local zeile k v
    [[ -f "$KIT/$1" ]] || fehler "Kit-Datei fehlt: $KIT/$1"
    while IFS= read -r zeile || [[ -n "$zeile" ]]; do
        case "$zeile" in
            *@UMGEBUNG_SYSTEMD@*)
                while IFS= read -r kv; do printf 'Environment="%s"\n' "$kv"; done < <(umgebung_zeilen nativ) ;;
            *@UMGEBUNG_COMPOSE@*)
                local einzug="${zeile%%@*}"
                while IFS= read -r kv; do k="${kv%%=*}"; v="${kv#*=}"; printf '%s%s: "%s"\n' "$einzug" "$k" "${v//\"/\\\"}"; done < <(umgebung_zeilen docker) ;;
            *) _platzhalter <<<"$zeile" ;;
        esac
    done < "$KIT/$1"
}

# Vorlage füllen und einspielen, nur wenn sich etwas ändert; liefert 0 bei Änderung.
vorlage_einspielen() {   # <pfad-im-kit> <ziel> [rechte]
    local ziel="$2" rechte="${3:-0644}" tmp
    tmp=$(mktemp)
    vorlage_fuellen "$1" > "$tmp"
    if [[ -f "$ziel" ]] && cmp -s "$tmp" "$ziel"; then
        rm -f "$tmp"; ok "unverändert: $ziel"; return 1
    fi
    sichern "$ziel"
    install -D -m "$rechte" "$tmp" "$ziel"
    rm -f "$tmp"
    ok "eingespielt: $ziel"
    return 0
}

gesund() { for _ in $(seq 1 "${1:-90}"); do curl -fsS -o /dev/null "$GESUND" 2>/dev/null && return 0; sleep 1; done; return 1; }

# Öffentlich erreichbarer Server: Testanmeldung, Demodaten u. ä. (VERBOTEN_MUSTER) sind verboten.
oeffentlich_pruefen() {
    if [[ -f "$UMGEBUNG" ]] && grep -qsE "$VERBOTEN_MUSTER" "$UMGEBUNG"; then
        fehler "In $UMGEBUNG ist ein auf dem Server verbotener Schalter eingeschaltet ($(grep -E "$VERBOTEN_MUSTER" "$UMGEBUNG" | head -1)). Zeile entfernen. Nichts geändert."
    fi
}

# ---------- PostgreSQL-Container ----------
pg() { docker exec -i "$DB_CONTAINER" "$@"; }
# psql_admin <sql>: als Datenbank-Superuser (= $DIENST im eigenen Container) gegen „postgres“, eine Zeile Ergebnis ohne Rahmen
psql_admin() { docker exec "$DB_CONTAINER" psql -U "$DIENST" -d postgres -v ON_ERROR_STOP=1 -tAc "$1"; }
psql_in() { docker exec "$DB_CONTAINER" psql -U "$DIENST" -d "$1" -v ON_ERROR_STOP=1 -tAc "$2"; }
db_container_bereit() { command -v docker >/dev/null 2>&1 && docker exec "$DB_CONTAINER" pg_isready -U "$DIENST" >/dev/null 2>&1; }
db_vorhanden() { [[ "$(psql_admin "SELECT 1 FROM pg_database WHERE datname = '$1'")" == "1" ]]; }
# Datenbanken der Anwendung im Container (ohne Systemdatenbanken, beiseitegelegte und Proben)
db_liste() {
    psql_admin "SELECT datname FROM pg_database WHERE NOT datistemplate AND datname <> 'postgres'
                AND datname !~ '_(alt|verworfen)_[0-9_]+\$' AND datname !~ '_(uebernahmeprobe|probe)\$' ORDER BY datname"
}
db_trennen() { psql_admin "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$1' AND pid <> pg_backend_pid()" >/dev/null; }
db_umbenennen() { db_trennen "$1"; psql_admin "ALTER DATABASE \"$1\" RENAME TO \"$2\"" >/dev/null; }
db_loeschen() { psql_admin "DROP DATABASE IF EXISTS \"$1\" WITH (FORCE)" >/dev/null; }
# db_aus_dump <datenbank> <dump>: Datenbank aus einer pg_dump-Sicherung (custom format) neu anlegen
db_aus_dump() {
    db_loeschen "$1"
    psql_admin "CREATE DATABASE \"$1\" OWNER \"$DIENST\"" >/dev/null
    pg pg_restore -U "$DIENST" -d "$1" --no-owner --exit-on-error < "$2"
}

# Erinnerung an die Ersteinrichtung: Solange es kein Konto gibt, könnte jede Person mit Zugang das erste Verwaltungskonto anlegen.
ersteinrichtung_hinweis() {
    [[ -n "${ERSTEINRICHTUNG_SQL:-}" ]] && db_container_bereit || return 0
    local n; n=$(psql_in "$DB_NAME" "$ERSTEINRICHTUNG_SQL" 2>/dev/null || echo "?")
    if [[ "$n" == "0" ]]; then
        warn "Noch KEIN Konto: jetzt sofort https://$HOST$PFAD/ öffnen und das erste Konto (Verwaltung) anlegen – bis dahin könnte das jede Person tun."
    fi
}
