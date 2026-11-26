#!/usr/bin/env bash
# Sicherung von ProfOMat: Datenbank per sqlite3 .backup (konsistent auch während der Dienst läuft, WAL-Modus) mit
# automatischer Rücksicherungsprobe, dazu der Schlüsselordner (Systemsiegel – ohne ihn sind Siegel nicht mehr prüfbar –,
# Cookie- und TOTP-Schlüssel, SAML-Zertifikat). Aufbewahrung 14 Tage (vor Updates 30 Tage). Ziel: /usr/local/sbin/profomat-sicherung.sh.
#
# Verschlüsselung und Kopie außer Haus (optional, /etc/profomat/sicherung.env):
#   AGE_EMPFAENGER="age1… [age1…]"   öffentliche age-Schlüssel; der private liegt OFFLINE beim Betreiber
#   KOPIE_ZIEL="rsync:benutzer@host:/pfad"  oder  "rclone:remote:pfad"   nur die verschlüsselten Dateien gehen dorthin
# Mit AGE_EMPFAENGER werden die nächtlichen Sicherungen nur verschlüsselt (.age) aufbewahrt. Sicherungen vor einem Update bleiben
# unverschlüsselt (0640, nur Dienst/root), weil ausrollen.sh daraus ohne den privaten Schlüssel zurückrollt; außer Haus gehen auch
# sie nur verschlüsselt.
#
# Rücksicherungsprobe (jedes Mal, vor dem Verschlüsseln): Kopie in ein leeres Verzeichnis, PRAGMA integrity_check, Schemaversion,
# Tabellen lesbar. Scheitert sie, endet das Skript mit Fehler (Journal, systemd meldet die fehlgeschlagene Unit).
#
# Aufruf: profomat-sicherung.sh [nacht|vor-update|…]   → /var/backups/profomat/<art>-<Zeit>.db[.age] und …-schluessel.tar.gz[.age]
# Ausgabe: Pfad der (unverschlüsselten) Datenbanksicherung bei vor-update, sonst der gesicherten Datei; leer ohne Datenbank.
set -euo pipefail
DIENST="profomat"
DB="/var/lib/$DIENST/profomat.db"
SCHLUESSEL="/var/lib/$DIENST/schluessel"
ZIEL="/var/backups/$DIENST"
STATUS="$ZIEL/letzte-sicherung.txt"
KONFIG="/etc/$DIENST/sicherung.env"
AGE_EMPFAENGER=""
KOPIE_ZIEL=""
# shellcheck disable=SC1090
[[ -f "$KONFIG" ]] && source "$KONFIG"
art="${1:-nacht}"
zeit=$(date +%Y%m%d-%H%M%S)
install -d -o "$DIENST" -g "$DIENST" -m 0750 "$ZIEL"
umask 0077
ausgaben=()

verschluesseln() {   # $1 = Datei; ersetzt sie durch <Datei>.age
    local datei="$1" args=()
    command -v age >/dev/null || { echo "AGE_EMPFAENGER gesetzt, aber age fehlt (apt install age)" >&2; exit 1; }
    for e in $AGE_EMPFAENGER; do args+=(-r "$e"); done
    age "${args[@]}" -o "$datei.age" "$datei"
    rm -f "$datei"
    chown "$DIENST:$DIENST" "$datei.age"; chmod 0640 "$datei.age"
}

rueckprobe() {   # $1 = Datenbanksicherung
    local probe v n
    probe=$(mktemp -d)
    cp "$1" "$probe/probe.db"
    [[ "$(sqlite3 "$probe/probe.db" 'PRAGMA integrity_check;' 2>&1)" == "ok" ]] || { rm -rf "$probe"; echo "Rücksicherungsprobe: integrity_check fehlgeschlagen ($1)" >&2; exit 1; }
    v=$(sqlite3 -readonly "$probe/probe.db" "SELECT Version FROM Schemainfo LIMIT 1;" 2>/dev/null || true)
    [[ "$v" =~ ^[0-9]+$ ]] || { rm -rf "$probe"; echo "Rücksicherungsprobe: keine Schemaversion ($1)" >&2; exit 1; }
    n=$(sqlite3 -readonly "$probe/probe.db" "SELECT (SELECT COUNT(*) FROM Konten) || ' Konten, ' || (SELECT COUNT(*) FROM Sicherheitsprotokoll) || ' Protokolleinträge';" 2>/dev/null \
        || sqlite3 -readonly "$probe/probe.db" "SELECT COUNT(*) || ' Tabellen' FROM sqlite_master WHERE type = 'table';" 2>/dev/null || echo "Tabellen nicht zählbar")
    rm -rf "$probe"
    echo "Schema $v, $n"
}

if [[ -d "$SCHLUESSEL" ]]; then
    tarball="$ZIEL/$art-$zeit-schluessel.tar.gz"
    tar -czf "$tarball" -C "$(dirname "$SCHLUESSEL")" "$(basename "$SCHLUESSEL")"
    chown "$DIENST:$DIENST" "$tarball"
    if [[ -n "$AGE_EMPFAENGER" ]]; then verschluesseln "$tarball"; tarball="$tarball.age"; fi
    ausgaben+=("$tarball")
fi

if [[ -f "$DB" ]]; then
    datei="$ZIEL/$art-$zeit.db"
    sqlite3 "$DB" ".backup '$datei'"
    chown "$DIENST:$DIENST" "$datei"; chmod 0640 "$datei"
    probe=$(rueckprobe "$datei")
    ergebnis="$datei"
    if [[ -n "$AGE_EMPFAENGER" && "$art" != "vor-update" ]]; then
        verschluesseln "$datei"; ergebnis="$datei.age"
    elif [[ -n "$AGE_EMPFAENGER" ]]; then
        # vor-update: unverschlüsselt für den Rückweg, zusätzlich verschlüsselt für außer Haus
        cp "$datei" "$datei.kopie"; verschluesseln "$datei.kopie"; mv "$datei.kopie.age" "$datei.age"; ausgaben+=("$datei.age")
    fi
    ausgaben+=("$ergebnis")
    sha256sum "$ergebnis" > "$ergebnis.sha256"
    printf '%s %s %s (Probe: %s)\n' "$(date -Iseconds)" "$art" "$(basename "$ergebnis")" "$probe" > "$STATUS"
    chown "$DIENST:$DIENST" "$STATUS" "$ergebnis.sha256"; chmod 0640 "$STATUS" "$ergebnis.sha256"
    echo "$datei"
fi

if [[ -n "$KOPIE_ZIEL" && -n "$AGE_EMPFAENGER" ]]; then
    for f in "${ausgaben[@]}"; do
        [[ "$f" == *.age ]] || continue
        case "$KOPIE_ZIEL" in
            rsync:*)  rsync -a --chmod=F600 "$f" "${KOPIE_ZIEL#rsync:}/" >&2 ;;
            rclone:*) rclone copy "$f" "${KOPIE_ZIEL#rclone:}" >&2 ;;
            *)        echo "KOPIE_ZIEL unbekannt: $KOPIE_ZIEL (rsync:… oder rclone:…)" >&2; exit 1 ;;
        esac
    done
elif [[ -n "$KOPIE_ZIEL" ]]; then
    echo "KOPIE_ZIEL ohne AGE_EMPFAENGER: außer Haus gehen nur verschlüsselte Sicherungen – nichts kopiert." >&2
fi

find "$ZIEL" -maxdepth 1 \( -name 'nacht-*.db*' -o -name 'nacht-*-schluessel.tar.gz*' \) -mtime +14 -delete
find "$ZIEL" -maxdepth 1 \( -name 'vor-update-*.db*' -o -name 'vor-update-*-schluessel.tar.gz*' \) -mtime +30 -delete
