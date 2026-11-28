#!/usr/bin/env bash
# Sicherung (Deploy-Kit, KIT-DATEI; Vorlage → /usr/local/sbin/@DIENST@-sicherung.sh): alle Datenbanken der Anwendung im Container
# @DB_CONTAINER@ per pg_dump (konsistent im laufenden Betrieb), jede mit automatischer Rücksicherungsprobe in eine Wegwerf-Datenbank,
# dazu die Schlüsselordner (@SICHERN_ORDNER_TEXT@) – ohne sie sind verschlüsselte Felder, Siegel und Anmeldungen nicht mehr nutzbar.
# Aufbewahrung: nacht 14 Tage, vor-update 30 Tage. Aufruf: @DIENST@-sicherung.sh [nacht|vor-update|…]
#
# Verschlüsselung und Kopie außer Haus (optional, /etc/@DIENST@/sicherung.env):
#   AGE_EMPFAENGER="age1… [age1…]"   öffentliche age-Schlüssel; der private liegt OFFLINE beim Betreiber
#   KOPIE_ZIEL="rsync:benutzer@host:/pfad"  oder  "rclone:remote:pfad"   nur verschlüsselte Dateien gehen dorthin
# Mit AGE_EMPFAENGER werden nächtliche Sicherungen nur verschlüsselt (.age) aufbewahrt. Sicherungen vor einem Update bleiben zusätzlich
# unverschlüsselt (0640), weil der Rückweg des Kits ohne den privaten Schlüssel daraus zurücksetzt.
#
# Ausgabe (für ausrollen.sh): je Datenbank „DUMP|<datenbank>|<datei>“, je Ordner „DATEI|<ordner>|<datei>“ (unverschlüsselte Pfade).
# Scheitert eine Rücksicherungsprobe, endet das Skript mit Fehler (Journal; systemd meldet die fehlgeschlagene Unit).
set -euo pipefail
DIENST="@DIENST@"
CONTAINER="@DB_CONTAINER@"
DATEN="/var/lib/$DIENST"
ZIEL="/var/backups/$DIENST"
STATUS="$ZIEL/letzte-sicherung.txt"
KONFIG="/etc/$DIENST/sicherung.env"
ORDNER="@SICHERN_ORDNER@"
SQLITE_ALT="@SQLITE_ALT@"
AGE_EMPFAENGER=""
KOPIE_ZIEL=""
# shellcheck disable=SC1090
[[ -f "$KONFIG" ]] && source "$KONFIG"
art="${1:-nacht}"
zeit=$(date +%Y%m%d-%H%M%S)
install -d -o "$DIENST" -g "$DIENST" -m 0750 "$ZIEL"
umask 0077
ausgaben=()
proben=()

verschluesseln() {   # $1 = Datei; legt <Datei>.age an
    local datei="$1" args=() e
    command -v age >/dev/null || { echo "AGE_EMPFAENGER gesetzt, aber age fehlt (apt install age)" >&2; exit 1; }
    for e in $AGE_EMPFAENGER; do args+=(-r "$e"); done
    age "${args[@]}" -o "$datei.age" "$datei"
    chown "$DIENST:$DIENST" "$datei.age"; chmod 0640 "$datei.age"
}
ablegen() {          # $1 = Datei: Rechte, Prüfsumme, ggf. verschlüsseln (nachts: nur noch verschlüsselt)
    local datei="$1"
    chown "$DIENST:$DIENST" "$datei"; chmod 0640 "$datei"
    sha256sum "$datei" > "$datei.sha256"; chown "$DIENST:$DIENST" "$datei.sha256"
    if [[ -n "$AGE_EMPFAENGER" ]]; then
        verschluesseln "$datei"; ausgaben+=("$datei.age")
        [[ "$art" == "vor-update" ]] || rm -f "$datei" "$datei.sha256"
    else
        ausgaben+=("$datei")
    fi
}
psql_admin() { docker exec "$CONTAINER" psql -U "$DIENST" -d postgres -v ON_ERROR_STOP=1 -tAc "$1"; }

# 1. Datenbanken (ohne Systemdatenbanken, beiseitegelegte und Proben)
if docker exec "$CONTAINER" pg_isready -U "$DIENST" >/dev/null 2>&1; then
    for db in $(psql_admin "SELECT datname FROM pg_database WHERE NOT datistemplate AND datname <> 'postgres'
                            AND datname !~ '_(alt|verworfen)_[0-9_]+\$' AND datname !~ '_(uebernahmeprobe|probe)\$' ORDER BY datname"); do
        datei="$ZIEL/$art-$zeit-$db.dump"
        docker exec "$CONTAINER" pg_dump -U "$DIENST" -d "$db" --format=custom > "$datei"
        # Rücksicherungsprobe: in eine Wegwerf-Datenbank einspielen, Tabellen zählen, wieder löschen
        probe="${db}_probe"
        psql_admin "DROP DATABASE IF EXISTS \"$probe\" WITH (FORCE)" >/dev/null
        psql_admin "CREATE DATABASE \"$probe\"" >/dev/null
        if meldung=$(docker exec -i "$CONTAINER" pg_restore -U "$DIENST" -d "$probe" --no-owner --exit-on-error < "$datei" 2>&1); then
            n=$(docker exec "$CONTAINER" psql -U "$DIENST" -d "$probe" -tAc "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'")
            psql_admin "DROP DATABASE IF EXISTS \"$probe\" WITH (FORCE)" >/dev/null
            proben+=("$db: $n Tabellen")
        else
            psql_admin "DROP DATABASE IF EXISTS \"$probe\" WITH (FORCE)" >/dev/null
            echo "Rücksicherungsprobe fehlgeschlagen: $datei lässt sich nicht einspielen – ${meldung:0:400}" >&2
            exit 1
        fi
        ablegen "$datei"
        echo "DUMP|$db|$datei"
    done
else
    echo "Datenbank-Container $CONTAINER antwortet nicht – nur Dateien gesichert" >&2
    [[ "$art" == "vor-update" ]] && exit 1
fi

# 2. Schlüsselordner und eine noch nicht übernommene SQLite-Datei (Umstellung)
for o in $ORDNER; do
    [[ -d "$DATEN/$o" ]] || continue
    tarball="$ZIEL/$art-$zeit-$o.tar.gz"
    tar -czf "$tarball" -C "$DATEN" "$o"
    ablegen "$tarball"
    echo "DATEI|$o|$tarball"
done
if [[ -n "$SQLITE_ALT" && -f "$DATEN/$SQLITE_ALT" ]] && command -v sqlite3 >/dev/null; then
    datei="$ZIEL/$art-$zeit-$SQLITE_ALT"
    sqlite3 "$DATEN/$SQLITE_ALT" ".backup '$datei'"
    ablegen "$datei"
    echo "DATEI|$SQLITE_ALT|$datei"
fi

printf '%s %s %s (Probe: %s)\n' "$(date -Iseconds)" "$art" "${#ausgaben[@]} Dateien" "${proben[*]:-keine Datenbank}" > "$STATUS"
chown "$DIENST:$DIENST" "$STATUS"; chmod 0640 "$STATUS"

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

find "$ZIEL" -maxdepth 1 -name 'nacht-*' -mtime +14 -delete
find "$ZIEL" -maxdepth 1 -name 'vor-update-*' -mtime +30 -delete
