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
DB="$DATEN/profomat.db"
LOGS="/var/log/$DIENST"
SICHERUNGEN="/var/backups/$DIENST"
UMGEBUNG="/etc/$DIENST/$DIENST.env"
HTPASSWD="/etc/nginx/$DIENST.htpasswd"
GESUND="http://127.0.0.1:$PORT$PFAD/gesund"

info()  { printf '\033[1;34m»\033[0m %s\n' "$*"; }
ok()    { printf '\033[1;32m✓\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33m!\033[0m %s\n' "$*"; }
fehler(){ printf '\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

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

gesund() { for _ in $(seq 1 "${1:-90}"); do curl -fsS -o /dev/null "$GESUND" && return 0; sleep 1; done; return 1; }

# Ohne Hochschulanmeldung nur hinter dem Zugangspasswort: nginx-Einbindung und Passwortdatei müssen da sein.
zugangsschutz_aktiv() {
    [[ -s "$HTPASSWD" ]] && grep -qs "include /etc/nginx/snippets/$DIENST-locations.conf;" "/etc/nginx/liebler.d/$DIENST.conf" \
        && grep -qs "auth_basic_user_file $HTPASSWD;" "/etc/nginx/snippets/$DIENST-locations.conf"
}

# Kann sich jemand anmelden? Demodaten (Testanmeldung) oder ein Startkonto der Systemverwaltung.
anmeldung_pruefen() {
    if grep -qE '^\s*Demodaten__Anlegen=true' "$UMGEBUNG" 2>/dev/null; then
        grep -qE '^\s*Anmeldung__TestanmeldungErlaubt=true' "$UMGEBUNG" \
            || warn "Demodaten ohne Anmeldung__TestanmeldungErlaubt=true – die Demokonten haben kein Passwort, niemand kann sich anmelden."
    elif ! grep -qE '^\s*Startkonto__Passwort=.+' "$UMGEBUNG" 2>/dev/null; then
        warn "In $UMGEBUNG weder Demodaten__Anlegen=true noch Startkonto__Passwort – eine neue Datenbank hätte kein Konto (docs/deploy.md)."
    fi
}
