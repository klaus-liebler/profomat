#!/usr/bin/env bash
# Ende-zu-Ende-Test des Kits OHNE Server: spielt Erstinstallation, Updates, Migration, -DbNeu, Absturz mit automatischem Rückweg,
# Konfigurationskonflikte und den Rückweg von Hand durch. systemd, journalctl und nginx sind nachgebildet (stubs/), die App ist
# ein kleines Python-Programm (fake-app.py), Datenbank echtes SQLite.
# NUR in einem Wegwerf-Container als root starten – schreibt nach /etc, /opt, /var:
#   docker run --rm -it -v "${PWD}/deploy:/deploy" ubuntu:24.04 bash -c "apt-get update -qq && apt-get install -yqq sqlite3 curl iproute2 python3 >/dev/null && bash /deploy/test/simulation.sh"
set -uo pipefail
HIER="$(cd "$(dirname "$0")" && pwd)"
[[ $EUID -eq 0 && ( -f /.dockerenv || -n "${SIMULATION_ERLAUBT:-}" ) ]] || { echo "Nur als root in einem Wegwerf-Container (SIMULATION_ERLAUBT=1 erzwingt)."; exit 2; }
export SIM=/tmp/sim PATH="$HIER/stubs:$PATH" STABIL=3
rm -rf "$SIM" /tmp/profomat-kit /opt/profomat /opt/profomat.* /opt/.profomat.* /var/lib/profomat /var/log/profomat /var/backups/profomat \
       /etc/profomat /etc/systemd/system/profomat* /etc/nginx /root/profomat-sicherung /usr/local/sbin/profomat-sicherung.sh
mkdir -p "$SIM" /run/systemd/system
cp -a "$HIER/../server" /tmp/profomat-kit; chmod +x /tmp/profomat-kit/schritte/*.sh /tmp/profomat-kit/werkzeuge/*.sh
KIT=/tmp/profomat-kit/schritte
bestanden=0; gescheitert=0
pruefe() { if eval "$2"; then printf '\033[32m  ✓ %s\033[0m\n' "$1"; ((bestanden++)); else printf '\033[31m  ✗ %s\033[0m\n' "$1"; ((gescheitert++)); fi; }
szenario() { printf '\n\033[1;35m━━━ %s ━━━\033[0m\n' "$*"; }
paket() {   # <name> <schemaversion> <migrierbar_ab> <verhalten> → /tmp/<name>.tar.gz
    local d; d=$(mktemp -d); mkdir "$d/profomat"
    cp "$HIER/fake-app.py" "$d/profomat/ProfOMat.Web"; chmod +x "$d/profomat/ProfOMat.Web"
    printf 'schemaversion=%s\nmigrierbar_ab=%s\n' "$2" "$3" > "$d/profomat/schemainfo.txt"
    echo "$4" > "$d/profomat/VERHALTEN"
    printf '{"runtimeOptions":{"framework":{"name":"Microsoft.AspNetCore.App","version":"11.0.0"}}}\n' | sed 's/{"name"/{\n"name"/; s/,"version"/,\n"version"/' > "$d/profomat/ProfOMat.Web.runtimeconfig.json"
    tar -czf "/tmp/$1.tar.gz" -C "$d" profomat; rm -rf "$d"
}
ausrollen() {   # wie deploy.ps1: Archiv nach /tmp kopieren (ausrollen.sh löscht es nach Erfolg)
    local args=("$@") n=$#; cp "${args[n-1]}" /tmp/profomat.tar.gz; args[n-1]=/tmp/profomat.tar.gz
    bash "$KIT/ausrollen.sh" --wartezeit 20 "${args[@]}"
}
db_version() { sqlite3 /var/lib/profomat/profomat.db "SELECT Version FROM Schemainfo" 2>/dev/null; }

# Grundgerüst wie auf liebler (VibeVHS-Kit): nginx-Site mit Zertifikatsanker, map für WebSockets, .NET-Runtime
mkdir -p /etc/nginx/sites-available /etc/nginx/sites-enabled /etc/nginx/snippets /etc/nginx/conf.d /usr/share/dotnet/shared/Microsoft.AspNetCore.App/11.0.0
printf 'server_tokens off;\nmap $http_upgrade $connection_upgrade { default upgrade; "" close; }\n' > /etc/nginx/nginx.conf
printf 'server {\n    listen 443 ssl;\n    ssl_protocols TLSv1.2 TLSv1.3;\n    include /etc/nginx/snippets/liebler-zertifikat.conf;\n}\n' > /etc/nginx/sites-available/liebler.conf
ln -sf /etc/nginx/sites-available/liebler.conf /etc/nginx/sites-enabled/liebler.conf

szenario "Schritt 01: einrichten"
bash "$KIT/01-einrichten.sh"; pruefe "01 läuft durch" '[[ $? -eq 0 ]]'
pruefe "Unit angelegt" '[[ -f /etc/systemd/system/profomat.service ]]'
pruefe "nginx-Einbindung in liebler.d" 'grep -q "profomat-locations" /etc/nginx/liebler.d/profomat.conf'
pruefe "include-Zeile in liebler.conf" 'grep -qF "include /etc/nginx/liebler.d/*.conf;" /etc/nginx/sites-available/liebler.conf'

szenario "Trockenlauf (--pruefen) ändert nichts"
paket v18 18 7 gut
ausrollen --pruefen /tmp/v18.tar.gz; pruefe "Prüfung bestanden" '[[ $? -eq 0 ]]'
pruefe "nichts ausgerollt" '[[ ! -d /opt/profomat/schemainfo.txt && ! -f /opt/profomat/ProfOMat.Web ]]'

szenario "Erstes Ausrollen"
ausrollen /tmp/v18.tar.gz; pruefe "Ausrollen erfolgreich" '[[ $? -eq 0 ]]'
pruefe "Dienst läuft, /gesund ok" 'curl -fsS http://127.0.0.1:5082/profomat/gesund | grep -q ok'
pruefe "Datenbank Version 18" '[[ "$(db_version)" == 18 ]]'
sqlite3 /var/lib/profomat/profomat.db "INSERT INTO Konten VALUES ('klaus')"

szenario "Neue Version stürzt beim Start ab → sofortiger Rückweg"
paket v18kaputt 18 7 absturz
t0=$(date +%s); ausrollen /tmp/v18kaputt.tar.gz; rc=$?; dauer=$(( $(date +%s) - t0 ))
pruefe "Ausrollen meldet Fehler" '[[ $rc -ne 0 ]]'
pruefe "Rückweg kam sofort (${dauer}s < 15s), nicht erst nach dem Countdown" '(( dauer < 15 ))'
pruefe "vorige Version läuft wieder" 'curl -fsS http://127.0.0.1:5082/profomat/gesund | grep -q ok && grep -q gut /opt/profomat/VERHALTEN'
pruefe "Daten noch da" '[[ "$(sqlite3 /var/lib/profomat/profomat.db "SELECT count(*) FROM Konten")" == 1 ]]'

szenario "Neue Version antwortet nie → Rückweg nach Ablauf des Countdowns"
paket v18haengt 18 7 haengt
ausrollen /tmp/v18haengt.tar.gz; rc=$?
pruefe "Fehler gemeldet" '[[ $rc -ne 0 ]]'
pruefe "vorige Version läuft wieder" 'grep -q gut /opt/profomat/VERHALTEN && curl -fsS http://127.0.0.1:5082/profomat/gesund >/dev/null'

szenario "Absturz kurz NACH /gesund (in der Stabilitätsprüfung) → Rückweg"
paket v18spaet 18 7 spaet-absturz
STABIL=8 ausrollen /tmp/v18spaet.tar.gz; rc=$?
pruefe "Fehler gemeldet" '[[ $rc -ne 0 ]]'
pruefe "vorige Version läuft wieder" 'grep -q gut /opt/profomat/VERHALTEN'

szenario "Update mit Migration 18 → 19 (im Code enthalten)"
paket v19 19 7 gut
ausrollen /tmp/v19.tar.gz; pruefe "Ausrollen erfolgreich" '[[ $? -eq 0 ]]'
pruefe "Datenbank auf 19, Daten erhalten" '[[ "$(db_version)" == 19 && "$(sqlite3 /var/lib/profomat/profomat.db "SELECT count(*) FROM Konten")" == 1 ]]'

szenario "Rückweg von Hand nach Migration: Programm UND Datenbank zurück"
bash "$KIT/ausrollen.sh" --zurueck --ja; pruefe "Rückweg erfolgreich" '[[ $? -eq 0 ]]'
pruefe "Datenbank wieder Version 18 und läuft" '[[ "$(db_version)" == 18 ]] && curl -fsS http://127.0.0.1:5082/profomat/gesund >/dev/null'
pruefe "aufgewertete Datei beiseitegelegt" 'ls /var/lib/profomat/beiseite/profomat.db.verworfen-* >/dev/null 2>&1'
ausrollen /tmp/v19.tar.gz >/dev/null; pruefe "wieder auf 19" '[[ "$(db_version)" == 19 ]]'

szenario "Keine Migration im Code (Version 21 kann erst ab 20) → früher Abbruch mit -DbNeu-Hinweis"
paket v21 21 20 gut
ausgabe=$(ausrollen /tmp/v21.tar.gz 2>&1); rc=$?; echo "$ausgabe"
pruefe "abgebrochen" '[[ $rc -ne 0 ]]'
pruefe "Hinweis auf -DbNeu und Warnung" 'grep -q "ACHTUNG\|████" <<<"$ausgabe" && grep -q "DbNeu" <<<"$ausgabe"'
pruefe "nichts geändert (läuft weiter auf 19)" '[[ "$(db_version)" == 19 ]] && grep -q gut /opt/profomat/VERHALTEN && [[ ! -f /opt/profomat.vorher/VERHALTEN || -f /opt/profomat/schemainfo.txt ]] && grep -q "schemaversion=19" /opt/profomat/schemainfo.txt'

szenario "-DbNeu: neue leere Datenbank, alte beiseite; Rückweg holt sie zurück"
ausrollen --db-neu /tmp/v21.tar.gz; pruefe "Ausrollen erfolgreich" '[[ $? -eq 0 ]]'
pruefe "neue Datenbank Version 21 ohne Konten" '[[ "$(db_version)" == 21 && "$(sqlite3 /var/lib/profomat/profomat.db "SELECT count(*) FROM Konten")" == 0 ]]'
bash "$KIT/ausrollen.sh" --zurueck --ja; pruefe "Rückweg erfolgreich" '[[ $? -eq 0 ]]'
pruefe "alte Datenbank (19, mit Konto) wieder da" '[[ "$(db_version)" == 19 && "$(sqlite3 /var/lib/profomat/profomat.db "SELECT count(*) FROM Konten")" == 1 ]]'

szenario "Unit wurde von Hand geändert: fehlender Eintrag → Drop-in, Bestehendes bleibt"
sed -i '/Speicher__Logs/d; s/^RestartSec=5/RestartSec=10/' /etc/systemd/system/profomat.service
cp /etc/systemd/system/profomat.service "$SIM/unit-vorher"
ausrollen /tmp/v19.tar.gz; pruefe "Ausrollen erfolgreich" '[[ $? -eq 0 ]]'
pruefe "Unit selbst unverändert" 'cmp -s "$SIM/unit-vorher" /etc/systemd/system/profomat.service'
pruefe "Drop-in ergänzt Speicher__Logs" 'grep -q "Speicher__Logs=/var/log/profomat" /etc/systemd/system/profomat.service.d/50-kit.conf'
pruefe "RestartSec=10 nicht ins Drop-in übernommen" '! grep -q RestartSec /etc/systemd/system/profomat.service.d/50-kit.conf'

szenario "Unit widerspricht der Vorlage (App lauscht auf 0.0.0.0) → früher Abbruch"
sed -i 's|ASPNETCORE_URLS=http://127.0.0.1:5082|ASPNETCORE_URLS=http://0.0.0.0:5082|' /etc/systemd/system/profomat.service
ausgabe=$(ausrollen /tmp/v19.tar.gz 2>&1); rc=$?; echo "$ausgabe"
pruefe "abgebrochen mit Hinweis auf ASPNETCORE_URLS" '[[ $rc -ne 0 ]] && grep -q "ASPNETCORE_URLS" <<<"$ausgabe"'
pruefe "Unit nicht angefasst" 'grep -q "0.0.0.0:5082" /etc/systemd/system/profomat.service'
sed -i 's|0.0.0.0:5082|127.0.0.1:5082|' /etc/systemd/system/profomat.service

szenario "Fremde nginx-Datei beansprucht /profomat → früher Abbruch"
printf 'location /profomat/ { proxy_pass http://127.0.0.1:9999; }\n' > /etc/nginx/conf.d/fremd.conf
ausgabe=$(ausrollen /tmp/v19.tar.gz 2>&1); rc=$?; echo "$ausgabe"
pruefe "abgebrochen mit Fundstelle" '[[ $rc -ne 0 ]] && grep -q "fremd.conf" <<<"$ausgabe"'
rm /etc/nginx/conf.d/fremd.conf

szenario "Port von fremdem Prozess belegt → früher Abbruch"
systemctl stop profomat
python3 -m http.server 5082 --bind 127.0.0.1 >/dev/null 2>&1 & fremd=$!; sleep 1
ausgabe=$(ausrollen /tmp/v19.tar.gz 2>&1); rc=$?; echo "$ausgabe"
pruefe "abgebrochen mit Hinweis auf Port" '[[ $rc -ne 0 ]] && grep -q "Port 5082 ist belegt" <<<"$ausgabe"'
kill $fremd; wait $fremd 2>/dev/null; systemctl start profomat

szenario "Testanmeldung in der Umgebungsdatei → Abbruch"
echo "Anmeldung__TestanmeldungErlaubt=true" >> /etc/profomat/profomat.env
ausrollen --pruefen /tmp/v19.tar.gz >/dev/null 2>&1; pruefe "abgebrochen" '[[ $? -ne 0 ]]'
sed -i '/TestanmeldungErlaubt/d' /etc/profomat/profomat.env

systemctl stop profomat
printf '\n\033[1m%s bestanden, %s gescheitert\033[0m\n' "$bestanden" "$gescheitert"
(( gescheitert == 0 ))
