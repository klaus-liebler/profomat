#!/usr/bin/env bash
# Deploy-Kit (KIT-DATEI): neue Version einspielen (von deploy.ps1 aufgerufen). Übertragen wird NUR das Programm – Datenbank
# (PostgreSQL-Container <dienst>-db), Schlüssel und Logs bleiben auf dem Server.
#
#   1/6 Prüfen       System, Programme, Docker, Port, Platz, Paket, Runtime, systemd-Unit, nginx und – mit der NEUEN Version –
#                    der Datenbankstand (--datenbankstand) samt ggf. Probe-Übernahme alter SQLite-Daten. Ändert NICHTS; alle
#                    Probleme werden gesammelt gemeldet, dann Abbruch. (Im Modus docker wird hier schon das Abbild gebaut.)
#   2/6 Sichern      alle Datenbanken (pg_dump mit Rücksicherungsprobe) und Schlüsselordner, Stand für den Rückweg
#   3/6 Ergänzen     nur fehlende Einträge in systemd (Drop-in) und nginx (eigene Dateien); Bestehendes bleibt
#   4/6 Umschalten   alte Version anhalten, ggf. -DbNeu bzw. SQLite-Übernahme, neue Version an ihre Stelle, starten
#   5/6 Beobachten   Countdown bis zum automatischen Rückweg; sofortiger Rückweg bei Absturz oder Fehler im Log
#   6/6 Abschluss
#
# Aufruf: sudo bash ausrollen.sh [--modus framework|self-contained|docker] [--db-neu] [--pruefen] [--wartezeit S]
#                                [--sqlite-uebernehmen | --sqlite-ignorieren] <archiv.tar.gz>
#         sudo bash ausrollen.sh --zurueck [--ja]   letzten Lauf zurücknehmen (Programm, Konfiguration und ggf. Datenbanken)
#         sudo bash ausrollen.sh --neustart         laufende Version neu starten (z. B. nach Änderungen an /etc/<dienst>/<dienst>.env)
#   --pruefen             nur Schritt 1 („Trockenlauf“), nichts wird geändert
#   --db-neu              Hauptdatenbank umbenennen (<db>_alt_<Zeit>, nicht löschen); die Anwendung legt sie neu an
#   --sqlite-uebernehmen  alte SQLite-Datei (SQLITE_ALT) einmalig nach PostgreSQL übernehmen (vorher Probe, danach geprüft)
#   --sqlite-ignorieren   alte SQLite-Datei bewusst liegen lassen und leer starten
#   --ja                  Rückfragen beim Rückweg überspringen
source "$(dirname "$0")/gemeinsam.sh"
source "$(dirname "$0")/liebler-dotnet.sh"
source "$(dirname "$0")/liebler-nginx.sh"
source "$(dirname "$0")/pruefen.sh"
source "$(dirname "$0")/konfig-abgleich.sh"
source "$(dirname "$0")/datenbank.sh"
source "$(dirname "$0")/beobachten.sh"
als_root

BEISEITE="$DATEN/beiseite"
ZUSTAND="/root/$DIENST-sicherung/letzter-lauf.env"
KONFIG_LISTE="/root/$DIENST-sicherung/letzter-lauf.konfig"

db_neu=0; nur_pruefen=0; zurueck=0; neustart=0; ja=0; sqlite=""; archiv=""
while (( $# )); do
    case "$1" in
        --modus)              MODUS="${2:?--modus braucht framework, self-contained oder docker}"; shift ;;
        --db-neu)             db_neu=1 ;;
        --pruefen)            nur_pruefen=1 ;;
        --wartezeit)          WARTEZEIT="${2:?--wartezeit braucht Sekunden}"; shift ;;
        --zurueck)            zurueck=1 ;;
        --neustart)           neustart=1 ;;
        --ja)                 ja=1 ;;
        --sqlite-uebernehmen) sqlite="uebernehmen" ;;
        --sqlite-ignorieren)  sqlite="ignorieren" ;;
        -*)                   fehler "Unbekannte Option $1 (siehe Kopf von $0)" ;;
        *)                    archiv="$1" ;;
    esac
    shift
done
[[ "$WARTEZEIT" =~ ^[0-9]+$ ]] && (( WARTEZEIT >= 20 )) || fehler "--wartezeit: mindestens 20 Sekunden"
(( db_neu )) && [[ "$sqlite" == "uebernehmen" ]] && fehler "--db-neu und --sqlite-uebernehmen schließen sich aus."

# Nie zwei Läufe gleichzeitig
exec 9>"/run/$DIENST-ausrollen.lock"
flock -n 9 || fehler "Ein anderes Ausrollen von $DIENST läuft gerade."

# ---------- Welche Variante läuft gerade? ----------
laufender_modus() {
    if docker_container_aktiv; then echo docker
    elif systemctl is-active --quiet "$DIENST" 2>/dev/null; then echo nativ
    else echo ""; fi
}
alles_anhalten() {
    systemctl stop "$DIENST" 2>/dev/null || true
    if [[ -f "$DOCKER_ORDNER/compose.yml" ]] && command -v docker >/dev/null 2>&1; then
        docker compose --project-directory "$DOCKER_ORDNER" stop >/dev/null 2>&1 || true
    fi
}

# ---------- Docker ----------
docker_env_schreiben() {   # <bild>
    install -d -m 0755 "$DOCKER_ORDNER"
    vorlage_fuellen docker/compose.yml > "$DOCKER_ORDNER/compose.yml"
    # shellcheck disable=SC1090
    source "$PG_UMGEBUNG"
    ( umask 077; printf 'BILD=%s\nDIENST_UID=%s\nDIENST_GID=%s\nPOSTGRES_PASSWORD=%s\n' "$1" "$(id -u "$DIENST")" "$(id -g "$DIENST")" "$POSTGRES_PASSWORD" > "$DOCKER_ORDNER/.env" )
    chmod 0644 "$DOCKER_ORDNER/compose.yml"; chmod 0600 "$DOCKER_ORDNER/.env"
}
docker_bild_aktuell() { sed -n 's/^BILD=//p' "$DOCKER_ORDNER/.env" 2>/dev/null || true; }
docker_starten() { docker compose --project-directory "$DOCKER_ORDNER" up -d --force-recreate >/dev/null; }

if (( neustart )); then
    case "$(laufender_modus)" in
        docker) docker_env_schreiben "$(docker_bild_aktuell)"; docker_starten ;;
        nativ)  systemctl restart "$DIENST" ;;
        *)      fehler "Es läuft keine Version von $SYSTEM." ;;
    esac
    if gesund 90; then ok "$SYSTEM läuft wieder · $(curl -fsS "$GESUND")"; else fehler "$SYSTEM antwortet nicht – journalctl -u $DIENST bzw. docker logs $DIENST"; fi
    exit 0
fi

# ---------- Rückweg ----------
# zurueckrollen <automatisch 0|1>: Programm, Konfiguration und – falls nötig – Datenbanken auf den Stand vor dem letzten Lauf.
zurueckrollen() {
    local automatisch="$1"
    [[ -f "$ZUSTAND" ]] || fehler "Kein Stand eines früheren Laufs ($ZUSTAND) – Rückweg von Hand: README.md des Kits."
    # shellcheck disable=SC1090
    source "$ZUSTAND"

    if (( ! automatisch )); then
        info "Rückweg zum Stand vor dem Lauf vom $LAUF_ZEIT (Modus vorher: ${VORHER_MODUS:-keiner}, danach: $LAUF_MODUS)"
        if [[ "$DB_AENDERUNG" != "keine" ]]; then
            achtung "Die Datenbanken werden auf den Stand vor dem Ausrollen ($LAUF_ZEIT) zurückgesetzt." \
                    "Alles, was seitdem in der Anwendung erfasst wurde, ist danach NICHT MEHR SICHTBAR" \
                    "(der heutige Stand bleibt als Datenbank …_verworfen_<Zeit> im Container erhalten)."
        fi
        if (( ! ja )); then
            [[ -t 0 ]] || fehler "Rückfrage nicht möglich (kein Terminal) – mit --ja bestätigen."
            read -r -p "Rückweg ausführen? (j/N) " antwort
            [[ "$antwort" =~ ^(j|ja|y|yes)$ ]] || { info "Abgebrochen – nichts geändert."; exit 0; }
        fi
    fi

    alles_anhalten
    db_zurueck "$DB_AENDERUNG" "${DB_BEISEITE:-}" "${DB_VORHER:-}" "${DB_SICHERUNGEN:-}"
    # Programm
    if [[ "$LAUF_MODUS" == "docker" ]]; then
        if [[ -n "${VORHER_BILD:-}" ]]; then docker_env_schreiben "$VORHER_BILD"
        else docker compose --project-directory "$DOCKER_ORDNER" down >/dev/null 2>&1 || true; fi
    elif [[ -d "$PROGRAMM.vorher" ]]; then
        rm -rf "$PROGRAMM.defekt"; [[ -d "$PROGRAMM" ]] && mv "$PROGRAMM" "$PROGRAMM.defekt"; mv "$PROGRAMM.vorher" "$PROGRAMM"
        ok "vorige Programmversion wieder in $PROGRAMM (die neue liegt in $PROGRAMM.defekt)"
    fi
    # automatisch: Protokoll aus diesem Lauf (aktueller als die Datei); von Hand: Datei des letzten Laufs
    if (( automatisch )) && (( ${#KONFIG_PROTOKOLL[@]} )); then konfig_zurueck; else konfig_zurueck "$KONFIG_LISTE"; fi
    # Vorige Variante wieder starten
    case "${VORHER_MODUS:-}" in
        docker) systemctl disable "$DIENST" >/dev/null 2>&1 || true; docker_env_schreiben "$VORHER_BILD"; docker_starten; MODUS=docker ;;
        nativ)  systemctl enable "$DIENST" >/dev/null 2>&1 || true; systemctl start "$DIENST"; MODUS=framework ;;
        *)      warn "Vor dem Lauf lief keine Version – es bleibt alles angehalten."; rm -f "$ZUSTAND"; return 0 ;;
    esac
    if gesund 90; then ok "vorige Version läuft wieder · $(curl -fsS "$GESUND")"
    else fehler "Auch die vorige Version antwortet nicht – journalctl -u $DIENST bzw. docker logs $DIENST; Sicherungen in $SICHERUNGEN."; fi
    rm -f "$ZUSTAND"
}

if (( zurueck )); then
    zurueckrollen 0
    exit 0
fi

# ════════════════════════════════════════ 1/6 Prüfen ════════════════════════════════════════
abschnitt "1/6 Prüfen (ändert nichts) · $SYSTEM · Modus $MODUS"
[[ -n "$archiv" ]] || fehler "Pfad zum Archiv fehlt (Aufruf siehe Kopf von $0)"
[[ -f "$archiv" ]] || fehler "Archiv $archiv fehlt"
system_pruefen
uhr_pruefen
programme_pruefen curl tar ss journalctl systemctl systemd-run flock nginx awk df sha256sum
docker_pruefen
grundgeruest_pruefen

kennung=$(date +%Y%m%d-%H%M%S)
neu=$(mktemp -d "/opt/.$DIENST.neu.XXXX")
bild=""
# Ab Schritt 3 führt JEDER Abbruch (Fehler, fehler(), Strg+C) automatisch zurück zum vorigen Stand.
rueckweg_aktiv=0
beim_ende() {
    local rc=$?
    rm -rf "$neu"
    if (( rc != 0 && rueckweg_aktiv )); then
        rueckweg_aktiv=0
        abschnitt "Rückweg (automatisch nach Abbruch)"
        zurueckrollen 1 || true
    fi
}
trap beim_ende EXIT
trap 'exit 130' INT TERM
# Prüfsumme (deploy.ps1 legt <archiv>.sha256 daneben): stimmt sie nicht, wird nichts angefasst
if [[ -f "$archiv.sha256" ]]; then
    if (cd "$(dirname "$archiv")" && sha256sum --status -c <(sed "s#  .*#  $(basename "$archiv")#" "$archiv.sha256")); then ok "Prüfsumme stimmt (SHA-256 $(cut -c1-16 "$archiv.sha256")…)"
    else pruef_fehler "Prüfsumme von $archiv stimmt nicht – Paket beschädigt oder verändert"; fi
else
    warn "keine Prüfsumme ($archiv.sha256) – Paket ungeprüft"
fi
if tar -xzf "$archiv" -C "$neu" --strip-components=1 2>/dev/null; then ok "Archiv entpackt ($(du -sh "$neu" | cut -f1))"
else pruef_fehler "Archiv $archiv lässt sich nicht entpacken (beschädigt?)"; fi
# Unter Windows gebaute Archive tragen keine Unix-Rechte: hier festlegen (der Prüflauf läuft schon als Dienstbenutzer)
chown -R root:root "$neu"; chmod 755 "$neu"
find "$neu" -mindepth 1 -type d -exec chmod 755 {} +
find "$neu" -type f -exec chmod 644 {} +
[[ -f "$neu/$APP" ]] && chmod 755 "$neu/$APP"
[[ -f "$neu/createdump" ]] && chmod 755 "$neu/createdump"
paket_pruefen "$neu"
platz_pruefen "$archiv"
port_pruefen
vorher_modus=$(laufender_modus)
case "$vorher_modus" in
    docker) info "läuft gerade: Container $DIENST ($(docker_bild_aktuell))" ;;
    nativ)  info "läuft gerade: systemd-Dienst $DIENST" ;;
    *)      info "läuft gerade: keine Version (Erstinstallation oder angehalten)" ;;
esac
quelle="$neu"
if [[ "$MODUS" == "docker" ]]; then
    runtime_bild="mcr.microsoft.com/dotnet/aspnet:$(dotnet_bedarf "$neu" "$APP")"
    [[ "$runtime_bild" == *: ]] && runtime_bild="mcr.microsoft.com/dotnet/runtime-deps:11.0"   # self-contained
    if docker pull -q "$runtime_bild" >/dev/null 2>&1; then
        ok "Basisabbild $runtime_bild verfügbar"
        bild="$DIENST:$kennung"
        if docker build -q -t "$bild" --build-arg "BASIS=$runtime_bild" --build-arg "APP=$APP" -f "$KIT/docker/Dockerfile" "$neu" >/dev/null; then
            ok "Abbild $bild gebaut (läuft noch nicht)"; quelle="$bild"
        else
            pruef_fehler "Abbild bauen fehlgeschlagen (Meldung: docker build -t test -f $KIT/docker/Dockerfile <paket>)."; bild=""
        fi
    else
        pruef_fehler "Basisabbild $runtime_bild lässt sich nicht laden (Netz/Proxy für mcr.microsoft.com?)"
    fi
else
    dienst_planen
fi
nginx_planen
if (( ${#PRUEF_FEHLER[@]} == 0 )); then
    db_planen "$quelle" "$db_neu" "$sqlite"
else
    warn "Datenbankstand nicht geprüft – erst die Punkte oben beheben."
fi
pruef_ende

if (( nur_pruefen )); then
    [[ -n "$bild" ]] && docker rmi "$bild" >/dev/null 2>&1 || true
    ok "Trockenlauf beendet – mit --pruefen wurde nichts geändert."
    exit 0
fi

# ════════════════════════════════════════ 2/6 Sichern ════════════════════════════════════════
abschnitt "2/6 Sichern"
db_sichern
lauf_zeit=$(date '+%d.%m.%Y %H:%M:%S')
db_aenderung="keine"; db_beiseite=""
[[ "$DB_LAGE" == "migration" || "$DB_LAGE" == "neu" ]] && db_aenderung="migration"
(( DB_UEBERNAHME )) && db_aenderung="uebernahme"
if (( db_neu )) && db_vorhanden "$DB_NAME"; then db_aenderung="neu"; db_beiseite="${DB_NAME}_alt_${kennung//-/_}"; fi
vorher_bild=""; [[ "$vorher_modus" == "docker" ]] && vorher_bild=$(docker_bild_aktuell)
zustand_schreiben() {
    install -d -m 0700 "$(dirname "$ZUSTAND")"
    {
        printf 'LAUF_ZEIT=%q\nLAUF_MODUS=%q\nVORHER_MODUS=%q\nVORHER_BILD=%q\n' "$lauf_zeit" "$MODUS" "$vorher_modus" "$vorher_bild"
        printf 'DB_AENDERUNG=%q\nDB_BEISEITE=%q\nDB_VORHER=%q\nDB_SICHERUNGEN=%q\n' "$db_aenderung" "$db_beiseite" "$DB_VORHER" "$DB_SICHERUNGEN"
    } > "$ZUSTAND"
    konfig_protokoll_schreiben "$KONFIG_LISTE"
}
zustand_schreiben
ok "Stand für den Rückweg: $ZUSTAND"

# ════════════════════════════════════════ 3/6 Konfiguration ergänzen ════════════════════════════════════════
abschnitt "3/6 Konfiguration ergänzen (Bestehendes bleibt)"
rueckweg_aktiv=1
if [[ "$MODUS" != "docker" ]]; then dienst_anwenden; fi
nginx_anwenden
zustand_schreiben

# ════════════════════════════════════════ 4/6 Umschalten ════════════════════════════════════════
abschnitt "4/6 Umschalten"
start=$(date +%s)
alles_anhalten
if [[ "$db_aenderung" == "neu" ]]; then
    db_umbenennen "$DB_NAME" "$db_beiseite"
    ok "beiseitegelegt: Datenbank $DB_NAME → $db_beiseite (die Anwendung legt sie neu an)"
fi
if [[ "$db_aenderung" == "uebernahme" ]]; then
    sqlite_uebernehmen "$quelle" || fehler "Übernahme der SQLite-Daten fehlgeschlagen – zurück zum vorigen Stand."
    install -d -o "$DIENST" -g "$DIENST" -m 0700 "$BEISEITE"
    mv "$SQLITE_DATEI" "$BEISEITE/$(basename "$SQLITE_DATEI").uebernommen-$kennung"
    rm -f "$SQLITE_DATEI-wal" "$SQLITE_DATEI-shm"
    ok "alte SQLite-Datei beiseitegelegt: $BEISEITE/$(basename "$SQLITE_DATEI").uebernommen-$kennung"
fi
if [[ "$MODUS" == "docker" ]]; then
    systemctl disable "$DIENST" >/dev/null 2>&1 || true
    docker_env_schreiben "$bild"
    docker_starten
else
    rm -rf "$PROGRAMM.vorher"
    if [[ -d "$PROGRAMM" ]]; then mv "$PROGRAMM" "$PROGRAMM.vorher"; fi
    mv "$neu" "$PROGRAMM"
    [[ -f "$DOCKER_ORDNER/compose.yml" ]] && docker compose --project-directory "$DOCKER_ORDNER" down >/dev/null 2>&1 || true
    systemctl enable "$DIENST" >/dev/null 2>&1 || true
    systemctl start "$DIENST"
fi
ok "neue Version gestartet ($MODUS)"

# ════════════════════════════════════════ 5/6 Beobachten ════════════════════════════════════════
abschnitt "5/6 Beobachten"
if ! beobachten "$start"; then
    rot "✗ $BEOBACHT_GRUND"
    warn "letzte Logzeilen:"
    dienst_log "$start" | tail -n 25 | cut -c1-220 | sed 's/^/   /'
    dienst_log "$start" | deuten
    abschnitt "Rückweg (automatisch)"
    rueckweg_aktiv=0
    zurueckrollen 1 || true
    fehler "Ausrollen fehlgeschlagen: $BEOBACHT_GRUND – voriger Stand ist wiederhergestellt (Sicherung in $SICHERUNGEN)."
fi

# ════════════════════════════════════════ 6/6 Abschluss ════════════════════════════════════════
abschnitt "6/6 Abschluss"
rueckweg_aktiv=0
ok "läuft: $(curl -fsS "$GESUND") · https://$HOST$PFAD/ · Modus $MODUS"
case "$db_aenderung" in
    migration)  ok "Datenbankstand jetzt: $(app_werkzeug "${bild:-$PROGRAMM}" --datenbankstand 2>/dev/null | grep '^DATENBANKSTAND|' | cut -d'|' -f3,4 | tr '\n' ' ')" ;;
    uebernahme) ok "alte SQLite-Daten übernommen (Bericht in $SICHERUNGEN/uebernahme-*.txt)" ;;
    neu)        warn "Datenbank ist neu – die vorige liegt als $db_beiseite im Container" ;;
esac
fehlerzeilen=$(dienst_log "$start" | grep -E '(FTL|ERR)\]' || true)
if [[ -n "$fehlerzeilen" ]]; then
    warn "$(wc -l <<<"$fehlerzeilen") Fehlermeldung(en) seit dem Start (die Anwendung läuft trotzdem):"
    head -n 5 <<<"$fehlerzeilen" | cut -c1-200 | sed 's/^/   /'
fi
# ältere Abbilder aufräumen (das laufende und das vorige bleiben)
if [[ "$MODUS" == "docker" ]]; then
    docker images --format '{{.Repository}}:{{.Tag}}' "$DIENST" | grep -vxF -e "$bild" -e "${vorher_bild:-–}" | tail -n +2 | xargs -r docker rmi >/dev/null 2>&1 || true
fi
rm -f "$archiv" "$archiv.sha256"
ersteinrichtung_hinweis
info "Zurück zum vorigen Stand jederzeit mit: sudo bash $KIT/schritte/ausrollen.sh --zurueck (bzw. deploy.ps1 -Zurueck)"
