#!/usr/bin/env bash
# Neue ProfOMat-Version einspielen (von deploy.ps1 aufgerufen). Die Datenbank wird NIE übertragen: Sie liegt in /var/lib/profomat.
#
#   1/6 Prüfen       System, Programme, Port, Platz, Paket, systemd-Unit, nginx, Datenbank/Migration – ändert NICHTS;
#                    alle Probleme werden gesammelt gemeldet, dann Abbruch.
#   2/6 Sichern      Datenbank und Schlüssel (vor-update-<Zeit>), Stand für den Rückweg
#   3/6 Ergänzen     nur fehlende Einträge in systemd (Drop-in) und nginx (eigene Dateien); Bestehendes bleibt
#   4/6 Umschalten   alte Version anhalten, neue an ihre Stelle (vorige bleibt als .vorher bzw. als Abbild), starten
#   5/6 Beobachten   Countdown bis zum automatischen Rückweg; sofortiger Rückweg bei Absturz oder Fehler im Log
#   6/6 Abschluss
#
# Aufruf: sudo bash ausrollen.sh [--modus framework|self-contained|docker] [--db-neu] [--pruefen] [--wartezeit S] <archiv.tar.gz>
#         sudo bash ausrollen.sh --zurueck [--ja]      letzten Lauf zurücknehmen (Programm, Konfiguration und ggf. Datenbank)
#   --pruefen   nur Schritt 1 („Trockenlauf“), nichts wird geändert
#   --db-neu    Datenbank (samt -wal/-shm) nach /var/lib/profomat/beiseite/ verschieben; die Anwendung legt sie neu an
#   --ja        Rückfragen beim Rückweg überspringen
source "$(dirname "$0")/gemeinsam.sh"
source "$(dirname "$0")/liebler-dotnet.sh"
source "$(dirname "$0")/liebler-nginx.sh"
source "$(dirname "$0")/pruefen.sh"
source "$(dirname "$0")/konfig-abgleich.sh"
source "$(dirname "$0")/db-pruefen.sh"
source "$(dirname "$0")/beobachten.sh"
als_root

BEISEITE="$DATEN/beiseite"
ZUSTAND="/root/$DIENST-sicherung/letzter-lauf.env"
KONFIG_LISTE="/root/$DIENST-sicherung/letzter-lauf.konfig"
DOCKER_ORDNER="/opt/$DIENST-docker"

db_neu=0; nur_pruefen=0; zurueck=0; ja=0; archiv=""
while (( $# )); do
    case "$1" in
        --modus)     MODUS="${2:?--modus braucht framework, self-contained oder docker}"; shift ;;
        --db-neu)    db_neu=1 ;;
        --pruefen)   nur_pruefen=1 ;;
        --wartezeit) WARTEZEIT="${2:?--wartezeit braucht Sekunden}"; shift ;;
        --zurueck)   zurueck=1 ;;
        --ja)        ja=1 ;;
        -*)          fehler "Unbekannte Option $1 (siehe Kopf von $0)" ;;
        *)           archiv="$1" ;;
    esac
    shift
done
[[ "$WARTEZEIT" =~ ^[0-9]+$ ]] && (( WARTEZEIT >= 20 )) || fehler "--wartezeit: mindestens 20 Sekunden"

# Nie zwei Läufe gleichzeitig
exec 9>"/run/$DIENST-ausrollen.lock"
flock -n 9 || fehler "Ein anderes Ausrollen von $DIENST läuft gerade."

abschnitt() { printf '\n\033[1;36m══ %s ══\033[0m\n' "$*"; }

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
    vorlage_fuellen docker/compose.yml | sed -e "s|@DB_DATEI@|${DB_DATEI:-$DIENST.db}|g" > "$DOCKER_ORDNER/compose.yml"
    printf 'BILD=%s\nDIENST_UID=%s\nDIENST_GID=%s\n' "$1" "$(id -u "$DIENST")" "$(id -g "$DIENST")" > "$DOCKER_ORDNER/.env"
    chmod 0644 "$DOCKER_ORDNER/compose.yml" "$DOCKER_ORDNER/.env"
}
docker_bild_aktuell() { sed -n 's/^BILD=//p' "$DOCKER_ORDNER/.env" 2>/dev/null || true; }
docker_starten() { docker compose --project-directory "$DOCKER_ORDNER" up -d --force-recreate >/dev/null; }

# ---------- Rückweg ----------
# zurueckrollen <automatisch 0|1>: Programm, Konfiguration und – falls nötig – Datenbank auf den Stand vor dem letzten Lauf.
zurueckrollen() {
    local automatisch="$1"
    [[ -f "$ZUSTAND" ]] || fehler "Kein Stand eines früheren Laufs ($ZUSTAND) – Rückweg von Hand: docs/deploy.md."
    # shellcheck disable=SC1090
    source "$ZUSTAND"
    local db_jetzt kennung; kennung="verworfen-$(date +%Y%m%d-%H%M%S)"

    if (( ! automatisch )); then
        info "Rückweg zum Stand vor dem Lauf vom $LAUF_ZEIT (Modus vorher: ${VORHER_MODUS:-keiner}, danach: $LAUF_MODUS)"
        if [[ "$DB_AENDERUNG" != "keine" ]]; then
            achtung "Die Datenbank wird auf den Stand vor dem Ausrollen ($LAUF_ZEIT) zurückgesetzt." \
                    "Alles, was seitdem in der Anwendung erfasst wurde, ist danach NICHT MEHR SICHTBAR" \
                    "(die heutige Datei bleibt unter $BEISEITE/ … .$kennung erhalten)."
        fi
        if (( ! ja )); then
            [[ -t 0 ]] || fehler "Rückfrage nicht möglich (kein Terminal) – mit --ja bestätigen."
            read -r -p "Rückweg ausführen? (j/N) " antwort
            [[ "$antwort" =~ ^(j|ja|y|yes)$ ]] || { info "Abgebrochen – nichts geändert."; exit 0; }
        fi
    fi

    alles_anhalten
    # Datenbank: nach -DbNeu die vorige Datei zurück; nach einer Migration die Sicherung von vor dem Lauf
    case "$DB_AENDERUNG" in
        neu)
            if (( automatisch )); then rm -f "$DB" "$DB-wal" "$DB-shm"; else db_beiseite "$kennung"; fi
            db_zurueckholen "$DB_BEISEITE_KENNUNG" ;;
        migration)
            db_jetzt=$(db_version_jetzt)
            if [[ "$db_jetzt" != "$DB_VERSION_VORHER" ]]; then
                db_beiseite "$kennung"; db_aus_sicherung "$DB_SICHERUNG"
            else
                ok "Datenbank noch auf Version $db_jetzt – bleibt"
            fi ;;
    esac
    # Programm
    if [[ "$LAUF_MODUS" == "docker" ]]; then
        if [[ -n "${VORHER_BILD:-}" ]]; then docker_env_schreiben "$VORHER_BILD"
        else docker compose --project-directory "$DOCKER_ORDNER" down >/dev/null 2>&1 || true; fi
    else
        if [[ -d "$PROGRAMM.vorher" ]]; then
            rm -rf "$PROGRAMM.defekt"; [[ -d "$PROGRAMM" ]] && mv "$PROGRAMM" "$PROGRAMM.defekt"; mv "$PROGRAMM.vorher" "$PROGRAMM"
            ok "vorige Programmversion wieder in $PROGRAMM (die neue liegt in $PROGRAMM.defekt)"
        fi
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
abschnitt "1/6 Prüfen (ändert nichts) · Modus $MODUS"
[[ -n "$archiv" ]] || fehler "Pfad zum Archiv fehlt (Aufruf siehe Kopf von $0)"
[[ -f "$archiv" ]] || fehler "Archiv $archiv fehlt"
system_pruefen
uhr_pruefen
basis=(curl tar ss journalctl systemctl flock sqlite3 nginx awk df)
programme_pruefen "${basis[@]}"
[[ "$MODUS" == "docker" ]] && docker_pruefen
grundgeruest_pruefen

neu=$(mktemp -d "/opt/.$DIENST.neu.XXXX")
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
if tar -xzf "$archiv" -C "$neu" --strip-components=1 2>/dev/null; then ok "Archiv entpackt ($(du -sh "$neu" | cut -f1))"
else pruef_fehler "Archiv $archiv lässt sich nicht entpacken (beschädigt?)"; fi
paket_pruefen "$neu"
platz_pruefen "$archiv"
port_pruefen
vorher_modus=$(laufender_modus)
case "$vorher_modus" in
    docker) info "läuft gerade: Container $DIENST ($(docker_bild_aktuell))" ;;
    nativ)  info "läuft gerade: systemd-Dienst $DIENST" ;;
    *)      info "läuft gerade: keine Version (Erstinstallation oder angehalten)" ;;
esac
if [[ "$MODUS" == "docker" ]]; then
    runtime_bild="mcr.microsoft.com/dotnet/aspnet:$(dotnet_bedarf "$neu" "$APP")"
    [[ "$runtime_bild" == *: ]] && runtime_bild="mcr.microsoft.com/dotnet/runtime-deps:11.0"   # self-contained
    if docker pull -q "$runtime_bild" >/dev/null 2>&1; then ok "Basisabbild $runtime_bild verfügbar"
    else pruef_fehler "Basisabbild $runtime_bild lässt sich nicht laden (Netz/Proxy für mcr.microsoft.com?)"; fi
else
    dienst_planen
fi
nginx_planen
db_planen "$neu" "$db_neu"
pruef_ende

if (( nur_pruefen )); then
    ok "Trockenlauf beendet – mit --pruefen wurde nichts geändert."
    exit 0
fi

# ════════════════════════════════════════ 2/6 Sichern ════════════════════════════════════════
abschnitt "2/6 Sichern"
db_sicherung=""
if db_sicherung=$("/usr/local/sbin/$DIENST-sicherung.sh" vor-update); then
    if [[ -n "$db_sicherung" ]]; then ok "gesichert: $db_sicherung (dazu die Schlüssel)"; else info "noch keine Datenbank – nichts zu sichern"; fi
else
    fehler "Sicherung fehlgeschlagen – nichts geändert."
fi
lauf_zeit=$(date '+%d.%m.%Y %H:%M:%S'); kennung=$(date +%Y%m%d-%H%M%S)
db_aenderung="keine"
(( db_neu )) && [[ -f "$DB" ]] && db_aenderung="neu"
[[ "$db_aenderung" == "keine" && "$DB_LAGE" == "migration" ]] && db_aenderung="migration"
vorher_bild=""; [[ "$vorher_modus" == "docker" ]] && vorher_bild=$(docker_bild_aktuell)
zustand_schreiben() {
    install -d -m 0700 "$(dirname "$ZUSTAND")"
    {
        printf 'LAUF_ZEIT=%q\nLAUF_MODUS=%q\nVORHER_MODUS=%q\nVORHER_BILD=%q\n' "$lauf_zeit" "$MODUS" "$vorher_modus" "$vorher_bild"
        printf 'DB_AENDERUNG=%q\nDB_VERSION_VORHER=%q\nDB_SICHERUNG=%q\nDB_BEISEITE_KENNUNG=%q\n' "$db_aenderung" "$DB_VERSION_IST" "$db_sicherung" "$kennung"
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
chown -R root:root "$neu"
find "$neu" -type d -exec chmod 755 {} +
find "$neu" -type f -exec chmod 644 {} +
[[ -f "$neu/$APP" ]] && chmod 755 "$neu/$APP"
[[ -f "$neu/createdump" ]] && chmod 755 "$neu/createdump"

if [[ "$MODUS" == "docker" ]]; then
    bild="$DIENST:$kennung"
    info "Abbild $bild bauen (Basis $runtime_bild)"
    docker build -q -t "$bild" --build-arg "BASIS=$runtime_bild" --build-arg "APP=$APP" -f "$KIT/docker/Dockerfile" "$neu" >/dev/null \
        || fehler "Abbild bauen fehlgeschlagen (Meldung: docker build -t test -f $KIT/docker/Dockerfile <paket>)."
    ok "Abbild $bild gebaut"
fi

start=$(date +%s)
alles_anhalten
if (( db_neu )); then
    db_beiseite "$kennung"
    [[ "$db_aenderung" == "neu" ]] || info "--db-neu: keine Datenbank vorhanden, wird ohnehin neu angelegt"
fi
if [[ "$MODUS" == "docker" ]]; then
    systemctl disable "$DIENST" >/dev/null 2>&1 || true
    docker_env_schreiben "$bild"
    docker_starten
else
    rm -rf "$PROGRAMM.vorher"
    if [[ -d "$PROGRAMM" ]]; then mv "$PROGRAMM" "$PROGRAMM.vorher"; fi
    mv "$neu" "$PROGRAMM"
    systemctl enable "$DIENST" >/dev/null 2>&1 || true
    systemctl start "$DIENST"
fi
ok "neue Version gestartet ($MODUS)"

# ════════════════════════════════════════ 5/6 Beobachten ════════════════════════════════════════
abschnitt "5/6 Beobachten"
if ! beobachten "$start"; then
    rot "✗ $BEOBACHT_GRUND"
    warn "letzte Logzeilen:"
    dienst_log "$start" | tail -n 25 | sed 's/^/   /'
    schema_hinweis=0
    [[ "$(dienst_log "$start")" == *"Datenbankschema veraltet"* ]] && (( ! db_neu )) && schema_hinweis=1
    abschnitt "Rückweg (automatisch)"
    rueckweg_aktiv=0
    zurueckrollen 1 || true
    if (( schema_hinweis )); then
        achtung "Ursache: Datenbankschema passt nicht. Ausweg nur mit -DbNeu:" "" "${DBNEU_WARNUNG[@]}"
    fi
    fehler "Ausrollen fehlgeschlagen: $BEOBACHT_GRUND – voriger Stand ist wiederhergestellt (Sicherung in $SICHERUNGEN)."
fi

# ════════════════════════════════════════ 6/6 Abschluss ════════════════════════════════════════
abschnitt "6/6 Abschluss"
rueckweg_aktiv=0
ok "läuft: $(curl -fsS "$GESUND") · https://$HOST$PFAD/ · Modus $MODUS"
[[ "$db_aenderung" == "migration" ]] && ok "Datenbank aufgewertet: Version $DB_VERSION_IST → $(db_version_jetzt)"
(( db_neu )) && warn "Datenbank ist neu – alte Datei unter $BEISEITE/*.$kennung"
rm -f "$archiv"
ersteinrichtung_hinweis
info "Zurück zum vorigen Stand jederzeit mit: sudo bash $KIT/schritte/ausrollen.sh --zurueck"
