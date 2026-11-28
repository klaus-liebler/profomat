#!/usr/bin/env bash
# Ende-zu-Ende-Test des Deploy-Kits OHNE Server (KIT-DATEI): Einrichtung, Datenbank-Container, Übernahme alter SQLite-Daten (Probe und
# echt), Updates mit Migration, Absturz und Hänger mit automatischem Rückweg, fremder Datenbankstand, -DbNeu, Rückweg von Hand,
# Konfigurationskonflikte, Sicherung mit Rücksicherungsprobe. systemd, journalctl, nginx und docker sind nachgebildet (stubs/);
# die Datenbank ist ein echtes PostgreSQL im Test-Container, die App ein kleines Python-Programm (fake-app.py).
# Werte (Dienst, Port, Pfad …) kommen aus server/konfiguration.env – die Simulation läuft so für jedes der vier Systeme.
# NUR in einem Wegwerf-Container als root starten – schreibt nach /etc, /opt, /var:
#   docker run --rm -v "${PWD}/deploy:/deploy" ubuntu:24.04 bash /deploy/test/simulation.sh
set -uo pipefail
HIER="$(cd "$(dirname "$0")" && pwd)"
[[ $EUID -eq 0 && ( -f /.dockerenv || -n "${SIMULATION_ERLAUBT:-}" ) ]] || { echo "Nur als root in einem Wegwerf-Container (SIMULATION_ERLAUBT=1 erzwingt)."; exit 2; }
if ! command -v pg_ctlcluster >/dev/null || ! command -v sqlite3 >/dev/null; then
    echo "» Pakete für die Simulation (postgresql, sqlite3, curl, iproute2, python3) …"
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq && apt-get install -yqq postgresql sqlite3 curl iproute2 python3 >/dev/null || exit 2
fi
# shellcheck source=../server/konfiguration.env
source "$HIER/../server/konfiguration.env"
export SIM=/tmp/sim PATH="$HIER/stubs:$PATH" STABIL=3
rm -rf "$SIM" "/tmp/$DIENST-kit" "/opt/$DIENST" /opt/"$DIENST".* /opt/."$DIENST".* "/var/lib/$DIENST" "/var/log/$DIENST" "/var/backups/$DIENST" \
       "/etc/$DIENST" /etc/systemd/system/"$DIENST"* /etc/nginx "/root/$DIENST-sicherung" /usr/local/sbin/"$DIENST"-*
mkdir -p "$SIM" /run/systemd/system
# PostgreSQL der Simulation (übernimmt die Rolle des Containers <dienst>-db)
PGV=$(ls /etc/postgresql | sort -V | tail -1)
sed -i 's/^\(local\s\+all\s\+all\s\+\)peer/\1trust/' /etc/postgresql/"$PGV"/main/pg_hba.conf
pg_ctlcluster "$PGV" main restart >/dev/null 2>&1 || pg_ctlcluster "$PGV" main start
su postgres -c "psql -q -c 'DROP ROLE IF EXISTS $DIENST'" >/dev/null 2>&1
for d in $(su postgres -c "psql -tAc \"SELECT datname FROM pg_database WHERE datname LIKE '${DB_NAME}%'\""); do su postgres -c "dropdb --force $d"; done
su postgres -c "psql -q -c 'DROP ROLE IF EXISTS $DIENST'" >/dev/null 2>&1
cp -a "$HIER/../server" "/tmp/$DIENST-kit"; chmod +x "/tmp/$DIENST-kit"/schritte/*.sh "/tmp/$DIENST-kit"/werkzeuge/*.sh
KIT="/tmp/$DIENST-kit/schritte"
URL="http://127.0.0.1:$PORT$PFAD${GESUND_PFAD:-/gesund}"
bestanden=0; gescheitert=0
pruefe() { if eval "$2"; then printf '\033[32m  ✓ %s\033[0m\n' "$1"; ((bestanden++)); else printf '\033[31m  ✗ %s\033[0m\n' "$1"; ((gescheitert++)); fi; }
szenario() { printf '\n\033[1;35m━━━ %s ━━━\033[0m\n' "$*"; }
paket() {   # <name> <verhalten> <migration> … → /tmp/<name>.tar.gz
    local name="$1" verhalten="$2" d; shift 2
    d=$(mktemp -d); mkdir "$d/$DIENST"
    cp "$HIER/fake-app.py" "$d/$DIENST/$APP"; chmod +x "$d/$DIENST/$APP"
    printf '%s\n' "$@" > "$d/$DIENST/MIGRATIONEN"
    echo "$verhalten" > "$d/$DIENST/VERHALTEN"
    printf '{"runtimeOptions":{"framework":{\n"name":"Microsoft.AspNetCore.App",\n"version":"11.0.0"}}}\n' > "$d/$DIENST/$APP.runtimeconfig.json"
    tar -czf "/tmp/$name.tar.gz" -C "$d" "$DIENST"; rm -rf "$d"
}
ausrollen() {   # wie deploy.ps1: Archiv nach /tmp kopieren (ausrollen.sh löscht es nach Erfolg)
    local args=("$@") n=$#; cp "${args[n-1]}" "/tmp/$DIENST.tar.gz"; args[n-1]="/tmp/$DIENST.tar.gz"
    bash "$KIT/ausrollen.sh" --wartezeit 20 "${args[@]}"
}
psql_db() { psql -h /var/run/postgresql -U "$DIENST" -d "${2:-$DB_NAME}" -tAc "$1" 2>/dev/null; }
konten() { psql_db 'SELECT count(*) FROM "Konten"'; }
migrationen() { psql_db 'SELECT count(*) FROM "__EFMigrationsHistory"'; }
laeuft() { curl -fsS "$URL" 2>/dev/null | grep -q ok; }

# Grundgerüst wie auf einem Server mit nginx und TLS: Site mit Zertifikatsanker, map für WebSockets, .NET-Runtime
mkdir -p /etc/nginx/sites-available /etc/nginx/sites-enabled /etc/nginx/snippets /etc/nginx/conf.d /usr/share/dotnet/shared/Microsoft.AspNetCore.App/11.0.0
printf 'server_tokens off;\nmap $http_upgrade $connection_upgrade { default upgrade; "" close; }\n' > /etc/nginx/nginx.conf
printf 'server {\n    listen 443 ssl;\n    ssl_protocols TLSv1.2 TLSv1.3;\n    include /etc/nginx/snippets/liebler-zertifikat.conf;\n}\n' > "$NGINX_SITE"
ln -sf "$NGINX_SITE" /etc/nginx/sites-enabled/

szenario "einrichten.sh"
bash "$KIT/einrichten.sh"; pruefe "läuft durch" '[[ $? -eq 0 ]]'
pruefe "Unit angelegt, mit Umgebung aus umgebung.kit" 'grep -q "Environment=\"PathBase=$PFAD\"" /etc/systemd/system/$DIENST.service'
pruefe "nginx-Einbindung in liebler.d" 'grep -q "$DIENST-locations" /etc/nginx/liebler.d/$DIENST.conf'
pruefe "Sicherungsskript mit eingesetztem Dienst" 'grep -q "^DIENST=\"$DIENST\"" /usr/local/sbin/$DIENST-sicherung.sh'

szenario "Ausrollen ohne Datenbank → früher Abbruch mit Hinweis auf postgresql.sh"
paket v1 gut 20260101_Initial
ausgabe=$(ausrollen --pruefen /tmp/v1.tar.gz 2>&1); rc=$?; echo "$ausgabe" | tail -5
pruefe "abgebrochen mit Hinweis" '[[ $rc -ne 0 ]] && grep -q "postgresql.sh" <<<"$ausgabe"'

szenario "postgresql.sh: Container und Verbindung"
bash "$KIT/postgresql.sh"; pruefe "läuft durch" '[[ $? -eq 0 ]]'
pruefe "Verbindung in der Umgebungsdatei" 'grep -q "^ConnectionStrings__$VERBINDUNG=Host=127.0.0.1;Port=$DB_PORT;Database=$DB_NAME;" /etc/$DIENST/$DIENST.env'
pruefe "Passwortdatei nur für root" '[[ "$(stat -c %a /etc/$DIENST/postgres.env)" == 600 ]]'
bash "$KIT/postgresql.sh" >/dev/null; pruefe "zweiter Lauf ändert nichts" '[[ $? -eq 0 && $(grep -c "^ConnectionStrings__" /etc/$DIENST/$DIENST.env) -eq 1 ]]'

if [[ -n "${SQLITE_ALT:-}" ]]; then
    szenario "Alte SQLite-Daten vorhanden → ohne Entscheidung Abbruch"
    sqlite3 "/var/lib/$DIENST/$SQLITE_ALT" "CREATE TABLE Konten (Kennung TEXT); INSERT INTO Konten VALUES ('klaus'), ('vera');"
    chown "$DIENST:$DIENST" "/var/lib/$DIENST/$SQLITE_ALT"
    ausgabe=$(ausrollen /tmp/v1.tar.gz 2>&1); rc=$?; echo "$ausgabe" | tail -4
    pruefe "abgebrochen mit Hinweis auf -SqliteUebernehmen" '[[ $rc -ne 0 ]] && grep -q "SqliteUebernehmen" <<<"$ausgabe"'
    pruefe "nichts ausgerollt" '[[ ! -e /opt/$DIENST/$APP ]]'

    szenario "Trockenlauf mit Übernahme: Probe in eine Wegwerf-Datenbank, echte bleibt leer"
    ausgabe=$(ausrollen --pruefen --sqlite-uebernehmen /tmp/v1.tar.gz 2>&1); rc=$?; echo "$ausgabe" | grep -E "Probe|Konten" | head -5
    pruefe "Probe bestanden" '[[ $rc -eq 0 ]] && grep -q "Probe-Übernahme: 1 Tabellen" <<<"$ausgabe"'
    pruefe "keine Probe-Datenbank übrig, Ziel unberührt" '[[ -z "$(psql_db "SELECT 1 FROM pg_database WHERE datname LIKE '"'"'%uebernahmeprobe'"'"'" postgres)" ]] && [[ -z "$(konten)" ]]'

    szenario "Erstes Ausrollen mit Übernahme der SQLite-Daten"
    ausrollen --sqlite-uebernehmen /tmp/v1.tar.gz; pruefe "Ausrollen erfolgreich" '[[ $? -eq 0 ]]'
    pruefe "läuft, /gesund ok" 'laeuft'
    pruefe "beide Konten in PostgreSQL" '[[ "$(konten)" == 2 ]]'
    pruefe "SQLite-Datei beiseitegelegt" '[[ ! -f /var/lib/$DIENST/$SQLITE_ALT ]] && ls /var/lib/$DIENST/beiseite/$SQLITE_ALT.uebernommen-* >/dev/null 2>&1'
    pruefe "Übernahmebericht abgelegt" 'ls /var/backups/$DIENST/uebernahme-*.txt >/dev/null 2>&1'
else
    szenario "Erstes Ausrollen"
    ausrollen /tmp/v1.tar.gz; pruefe "Ausrollen erfolgreich" '[[ $? -eq 0 ]]'
    psql_db "INSERT INTO \"Konten\" VALUES ('klaus'), ('vera')" >/dev/null
fi

szenario "Neue Version stürzt beim Start ab → sofortiger Rückweg"
paket v1kaputt absturz 20260101_Initial
t0=$(date +%s); ausrollen /tmp/v1kaputt.tar.gz; rc=$?; dauer=$(( $(date +%s) - t0 ))
pruefe "Ausrollen meldet Fehler" '[[ $rc -ne 0 ]]'
pruefe "Rückweg kam sofort (${dauer}s < 20s), nicht erst nach dem Countdown" '(( dauer < 20 ))'
pruefe "vorige Version läuft wieder" 'laeuft && grep -q gut /opt/$DIENST/VERHALTEN'
pruefe "Daten noch da" '[[ "$(konten)" == 2 ]]'

szenario "Neue Version antwortet nie → Rückweg nach Ablauf des Countdowns"
paket v1haengt haengt 20260101_Initial
ausrollen /tmp/v1haengt.tar.gz; rc=$?
pruefe "Fehler gemeldet" '[[ $rc -ne 0 ]]'
pruefe "vorige Version läuft wieder" 'grep -q gut /opt/$DIENST/VERHALTEN && laeuft'

szenario "Absturz kurz NACH /gesund (in der Stabilitätsprüfung) → Rückweg"
paket v1spaet spaet-absturz 20260101_Initial
STABIL=8 ausrollen /tmp/v1spaet.tar.gz; rc=$?
pruefe "Fehler gemeldet" '[[ $rc -ne 0 ]]'
pruefe "vorige Version läuft wieder" 'grep -q gut /opt/$DIENST/VERHALTEN && laeuft'

szenario "Update mit neuer Migration"
paket v2 gut 20260101_Initial 20260201_Zweite
ausgabe=$(ausrollen /tmp/v2.tar.gz 2>&1); rc=$?; echo "$ausgabe" | grep -E "Migrierbar|Migration" | head -3
pruefe "Ausrollen erfolgreich" '[[ $rc -eq 0 ]]'
pruefe "Prüflauf meldete Migrierbar" 'grep -q "Migrierbar" <<<"$ausgabe"'
pruefe "zwei Migrationen eingespielt, Daten erhalten" '[[ "$(migrationen)" == 2 && "$(konten)" == 2 ]]'
pruefe "Sicherung vor dem Update (pg_dump)" 'ls /var/backups/$DIENST/vor-update-*-$DB_NAME.dump >/dev/null 2>&1'

szenario "Rückweg von Hand nach Migration: Programm UND Datenbank zurück"
psql_db "INSERT INTO \"Konten\" VALUES ('nach-update')" >/dev/null
bash "$KIT/ausrollen.sh" --zurueck --ja; pruefe "Rückweg erfolgreich" '[[ $? -eq 0 ]]'
pruefe "Datenbank wieder mit einer Migration, Stand vor dem Update" '[[ "$(migrationen)" == 1 && "$(konten)" == 2 ]] && laeuft'
pruefe "heutiger Stand als …_verworfen_… aufgehoben" '[[ -n "$(psql_db "SELECT datname FROM pg_database WHERE datname LIKE '"'"'${DB_NAME}_verworfen_%'"'"'" postgres)" ]]'
ausrollen /tmp/v2.tar.gz >/dev/null 2>&1; pruefe "wieder auf v2" '[[ "$(migrationen)" == 2 ]]'

szenario "Ältere Version gegen neuere Datenbank (fremde Migration) → früher Abbruch, nichts geändert"
ausgabe=$(ausrollen /tmp/v1.tar.gz 2>&1); rc=$?; echo "$ausgabe" | grep -E "Fremd|Rückweg" | head -3
pruefe "abgebrochen" '[[ $rc -ne 0 ]] && grep -q "Fremd" <<<"$ausgabe"'
pruefe "läuft unverändert weiter" 'laeuft && [[ "$(migrationen)" == 2 ]] && grep -q 20260201_Zweite /opt/$DIENST/MIGRATIONEN'

szenario "Schema geändert ohne Migration (Exitcode 3) → Abbruch mit -DbNeu-Hinweis; -DbNeu baut neu, Rückweg holt die Daten"
paket v3 neuaufbau 20260101_Initial 20260201_Zweite
ausgabe=$(ausrollen /tmp/v3.tar.gz 2>&1); rc=$?
pruefe "abgebrochen mit Warnung und -DbNeu" '[[ $rc -ne 0 ]] && grep -q "████" <<<"$ausgabe" && grep -q "DbNeu" <<<"$ausgabe"'
ausrollen --db-neu /tmp/v2.tar.gz; pruefe "-DbNeu erfolgreich" '[[ $? -eq 0 ]]'
pruefe "neue Datenbank ohne Konten, alte als …_alt_… beiseite" '[[ "$(konten)" == 0 && -n "$(psql_db "SELECT datname FROM pg_database WHERE datname LIKE '"'"'${DB_NAME}_alt_%'"'"'" postgres)" ]]'
bash "$KIT/ausrollen.sh" --zurueck --ja; pruefe "Rückweg erfolgreich" '[[ $? -eq 0 ]]'
pruefe "alte Datenbank mit Konten wieder da" '[[ "$(konten)" -ge 2 ]] && laeuft'

szenario "Unit wurde von Hand geändert: fehlender Eintrag → Drop-in, Bestehendes bleibt"
sed -i '/Speicher__Logs\|PathBase/d; s/^RestartSec=5/RestartSec=10/' /etc/systemd/system/$DIENST.service
cp /etc/systemd/system/$DIENST.service "$SIM/unit-vorher"
ausrollen /tmp/v2.tar.gz; pruefe "Ausrollen erfolgreich" '[[ $? -eq 0 ]]'
pruefe "Unit selbst unverändert" 'cmp -s "$SIM/unit-vorher" /etc/systemd/system/$DIENST.service'
pruefe "Drop-in ergänzt PathBase" 'grep -q "PathBase=$PFAD" /etc/systemd/system/$DIENST.service.d/50-kit.conf'
pruefe "RestartSec=10 nicht ins Drop-in übernommen" '! grep -q RestartSec /etc/systemd/system/$DIENST.service.d/50-kit.conf'

szenario "Unit widerspricht der Vorlage (App lauscht auf 0.0.0.0) → früher Abbruch"
sed -i "s|ASPNETCORE_URLS=http://127.0.0.1:$PORT|ASPNETCORE_URLS=http://0.0.0.0:$PORT|" /etc/systemd/system/$DIENST.service
ausgabe=$(ausrollen /tmp/v2.tar.gz 2>&1); rc=$?
pruefe "abgebrochen mit Hinweis auf ASPNETCORE_URLS" '[[ $rc -ne 0 ]] && grep -q "ASPNETCORE_URLS" <<<"$ausgabe"'
pruefe "Unit nicht angefasst" 'grep -q "0.0.0.0:$PORT" /etc/systemd/system/$DIENST.service'
sed -i "s|0.0.0.0:$PORT|127.0.0.1:$PORT|" /etc/systemd/system/$DIENST.service

szenario "Fremde nginx-Datei beansprucht den Pfad → früher Abbruch"
printf 'location %s/ { proxy_pass http://127.0.0.1:9999; }\n' "$PFAD" > /etc/nginx/conf.d/fremd.conf
ausgabe=$(ausrollen /tmp/v2.tar.gz 2>&1); rc=$?
pruefe "abgebrochen mit Fundstelle" '[[ $rc -ne 0 ]] && grep -q "fremd.conf" <<<"$ausgabe"'
rm /etc/nginx/conf.d/fremd.conf

szenario "Port von fremdem Prozess belegt → früher Abbruch"
systemctl stop "$DIENST"
python3 -m http.server "$PORT" --bind 127.0.0.1 >/dev/null 2>&1 & fremd=$!; sleep 1
ausgabe=$(ausrollen /tmp/v2.tar.gz 2>&1); rc=$?
pruefe "abgebrochen mit Hinweis auf den Port" '[[ $rc -ne 0 ]] && grep -q "Port $PORT ist belegt" <<<"$ausgabe"'
kill $fremd; wait $fremd 2>/dev/null; systemctl start "$DIENST"

szenario "Verbotener Schalter in der Umgebungsdatei → Abbruch"
echo "Anmeldung__TestanmeldungErlaubt=true" >> /etc/$DIENST/$DIENST.env
ausrollen --pruefen /tmp/v2.tar.gz >/dev/null 2>&1; pruefe "abgebrochen" '[[ $? -ne 0 ]]'
sed -i "/TestanmeldungErlaubt/d" /etc/$DIENST/$DIENST.env

szenario "Prüfsumme des Pakets falsch → Abbruch, Prüfsumme richtig → läuft"
cp /tmp/v2.tar.gz /tmp/$DIENST.tar.gz
echo "0000000000000000000000000000000000000000000000000000000000000000  $DIENST.tar.gz" > /tmp/$DIENST.tar.gz.sha256
ausgabe=$(bash "$KIT/ausrollen.sh" --wartezeit 20 --pruefen /tmp/$DIENST.tar.gz 2>&1); rc=$?
pruefe "abgebrochen mit Hinweis auf die Prüfsumme" '[[ $rc -ne 0 ]] && grep -q "Prüfsumme" <<<"$ausgabe"'
(cd /tmp && sha256sum $DIENST.tar.gz > $DIENST.tar.gz.sha256)
ausgabe=$(bash "$KIT/ausrollen.sh" --wartezeit 20 --pruefen /tmp/$DIENST.tar.gz 2>&1); rc=$?
pruefe "Prüfung mit richtiger Prüfsumme bestanden" '[[ $rc -eq 0 ]] && grep -q "Prüfsumme stimmt" <<<"$ausgabe"'
rm -f /tmp/$DIENST.tar.gz.sha256

szenario "Sicherung: pg_dump je Datenbank mit Rücksicherungsprobe, Schlüssel, Statusdatei"
ausgabe=$(/usr/local/sbin/$DIENST-sicherung.sh nacht 2>&1); rc=$?; echo "$ausgabe"
pruefe "Sicherung mit Probe läuft" '[[ $rc -eq 0 ]] && grep -q "Probe: $DB_NAME: [0-9]* Tabellen" /var/backups/$DIENST/letzte-sicherung.txt'
pruefe "Dump und Prüfsumme" 'ls /var/backups/$DIENST/nacht-*-$DB_NAME.dump.sha256 >/dev/null 2>&1'
pruefe "beiseitegelegte und verworfene Datenbanken nicht mitgesichert" '! ls /var/backups/$DIENST/nacht-*_verworfen_* >/dev/null 2>&1'
pruefe "keine Probedatenbank übrig" '[[ -z "$(psql_db "SELECT 1 FROM pg_database WHERE datname LIKE '"'"'%_probe'"'"'" postgres)" ]]'

systemctl stop "$DIENST"
printf '\n\033[1m%s bestanden, %s gescheitert\033[0m\n' "$bestanden" "$gescheitert"
(( gescheitert == 0 ))
