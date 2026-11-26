# Gemeinsame Hilfen für die Schritt-Skripte (wird per „source“ eingebunden, nicht direkt starten).
# Muster wie in den Kits von VibeVHS und FraktionPerfekt: idempotent, Sicherung vorher (/root/profomat-sicherung/<Zeit>-<Schritt>),
# Prüfung danach, Rückweg.
set -euo pipefail

KIT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=../konfiguration.env
source "$KIT/konfiguration.env"
SICHERUNG="/root/profomat-sicherung/$(date +%Y%m%d-%H%M%S)-$(basename "$0" .sh)"
PROGRAMM="/opt/$DIENST"
DATEN="/var/lib/$DIENST"
DB="$DATEN/${DB_DATEI:-$DIENST.db}"
MODUS="${MODUS:-framework}"
APP="${APP:-$DIENST}"
WARTEZEIT="${WARTEZEIT:-120}"
LOGS="/var/log/$DIENST"
SICHERUNGEN="/var/backups/$DIENST"
UMGEBUNG="/etc/$DIENST/$DIENST.env"
GESUND="http://127.0.0.1:$PORT$PFAD/gesund"

info()  { printf '\033[1;34m»\033[0m %s\n' "$*"; }
ok()    { printf '\033[1;32m✓\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33m!\033[0m %s\n' "$*"; }
fehler(){ printf '\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }
rot()   { printf '\033[1;31m%s\033[0m\n' "$*"; }

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

# Vorlage aus dem Kit mit den Werten aus konfiguration.env füllen (@HOST@, @PFAD@, @PORT@, @DIENST@) und einspielen,
# nur wenn sich etwas ändert; liefert 0 bei Änderung.
vorlage_einspielen() {
    local quelle="$KIT/$1" ziel="$2" rechte="${3:-0644}" tmp
    [[ -f "$quelle" ]] || fehler "Kit-Datei fehlt: $quelle"
    tmp=$(mktemp)
    sed -e "s|@HOST@|$HOST|g" -e "s|@PFAD@|$PFAD|g" -e "s|@PORT@|$PORT|g" -e "s|@DIENST@|$DIENST|g" "$quelle" > "$tmp"
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

# Öffentlich erreichbar (ohne nginx-Passwort, wie Podium): Testanmeldung (Konto ohne Passwort wählen) und Demodaten sind verboten.
oeffentlich_pruefen() {
    if grep -qsE '^\s*(Anmeldung__TestanmeldungErlaubt|Demodaten__Anlegen)\s*=\s*[Tt]rue' "$UMGEBUNG"; then
        fehler "In $UMGEBUNG ist Testanmeldung oder Demodaten eingeschaltet – auf dem öffentlichen Server verboten. Zeile entfernen. Nichts geändert."
    fi
}

# Erinnerung an die Ersteinrichtung (wie Podium): Solange es kein Konto gibt, kann JEDE Person mit Zugang zur Seite das erste
# Konto der Systemverwaltung anlegen – deshalb direkt nach dem ersten Ausrollen erledigen.
ersteinrichtung_hinweis() {
    local n
    n=$(sqlite3 -readonly "$DB" "SELECT count(*) FROM Konten;" 2>/dev/null || echo "?")
    if [[ "$n" == "0" ]]; then
        warn "Noch KEIN Konto: jetzt sofort https://$HOST$PFAD/anmelden öffnen und das erste Konto (Systemverwaltung) anlegen – bis dahin könnte das jede Person tun."
    fi
}
