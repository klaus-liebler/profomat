#!/usr/bin/env bash
# Deploy-Kit (KIT-DATEI): PostgreSQL 17 für die Anwendung als eigener Container „<dienst>-db“ – einmalig nach einrichten.sh, danach
# idempotent (wiederholbar). Gilt für JEDE Betriebsart: nativ spricht die Anwendung 127.0.0.1:$DB_PORT an, als Container das Netz
# „<dienst>-netz“. Ein Wechsel der Betriebsart lässt die Daten im Volume „<dienst>-db“.
# Start:   ssh -t <ziel> "sudo bash /tmp/<dienst>-kit/schritte/postgresql.sh"
# Rückweg: sudo bash /tmp/<dienst>-kit/schritte/postgresql-zurueck.sh (Container anhalten, Umgebung von vorher; Daten im Volume bleiben)
#
# - postgres:17, nur auf 127.0.0.1:$DB_PORT, Daten im Docker-Volume, Neustart automatisch (restart unless-stopped).
# - Eigener Container je System statt eines gemeinsamen Servers: Version, Sicherung und Rückweg bleiben unabhängig.
# - Zufälliges Passwort nur in /etc/<dienst>/postgres.env (root, 0600) und als Verbindung in der Umgebungsdatei (root:<dienst>, 0640).
# - pg_dump/pg_restore 17 auf dem Host (postgresql-client-17), damit die Anwendung selbst sichern kann (läuft ohne Docker-Rechte).
# - Ein vorhandener Container gleichen Namens (frühere Schritte 02 bzw. 10) wird übernommen, nicht neu angelegt.
source "$(dirname "$0")/gemeinsam.sh"
als_root

command -v docker >/dev/null || fehler "docker fehlt – sudo apt install docker.io (oder Docker CE). Nichts geändert."
docker info >/dev/null 2>&1 || fehler "Docker-Dienst läuft nicht – sudo systemctl enable --now docker. Nichts geändert."
[[ -d "/etc/$DIENST" && -f "$UMGEBUNG" ]] || fehler "einrichten.sh fehlt (/etc/$DIENST, $UMGEBUNG). Nichts geändert."
if ss -ltn | grep -qE "127\.0\.0\.1:$DB_PORT\b" && ! docker ps --format '{{.Names}}' | grep -qx "$DB_CONTAINER"; then
    fehler "Port $DB_PORT ist schon belegt (nicht von $DB_CONTAINER) – DB_PORT in konfiguration.env ändern. Nichts geändert."
fi

# 1. Passwort (einmalig)
if [[ ! -f "$PG_UMGEBUNG" ]]; then
    umask 077
    printf 'POSTGRES_PASSWORD=%s\n' "$(openssl rand -hex 24)" > "$PG_UMGEBUNG"
    chmod 0600 "$PG_UMGEBUNG"
    ok "Passwort erzeugt: $PG_UMGEBUNG"
fi
# shellcheck disable=SC1090
source "$PG_UMGEBUNG"

# 2. Netz und Container
docker network inspect "$DB_NETZ" >/dev/null 2>&1 || { docker network create "$DB_NETZ" >/dev/null; ok "Docker-Netz $DB_NETZ angelegt"; }
if docker ps -a --format '{{.Names}}' | grep -qx "$DB_CONTAINER"; then
    docker start "$DB_CONTAINER" >/dev/null
    ok "Container $DB_CONTAINER läuft (vorhanden, übernommen)"
else
    docker run -d --name "$DB_CONTAINER" --restart unless-stopped --network "$DB_NETZ" \
        -p "127.0.0.1:$DB_PORT:5432" \
        -v "$DIENST-db:/var/lib/postgresql/data" \
        -e POSTGRES_USER="$DIENST" -e POSTGRES_PASSWORD="$POSTGRES_PASSWORD" -e POSTGRES_DB="$DB_NAME" \
        postgres:17 >/dev/null
    ok "Container $DB_CONTAINER angelegt (postgres:17, 127.0.0.1:$DB_PORT, Volume $DIENST-db)"
fi
if ! docker inspect -f '{{range $n, $_ := .NetworkSettings.Networks}}{{$n}} {{end}}' "$DB_CONTAINER" | grep -qw "$DB_NETZ"; then
    docker network connect "$DB_NETZ" "$DB_CONTAINER" && ok "Container $DB_CONTAINER ins Netz $DB_NETZ (für den Modus docker)"
fi
for _ in $(seq 1 60); do db_container_bereit && break; sleep 1; done
db_container_bereit || fehler "PostgreSQL antwortet nicht – docker logs $DB_CONTAINER"
ok "PostgreSQL bereit: $(psql_admin 'SHOW server_version')"

# 3. Verbindung für die Anwendung (ersetzt eine frühere Zeile; ohne Anführungszeichen, gilt so für systemd und docker --env-file)
neu_zeile="ConnectionStrings__${VERBINDUNG}=Host=127.0.0.1;Port=$DB_PORT;Database=$DB_NAME;Username=$DIENST;Password=$POSTGRES_PASSWORD"
if grep -qxF "$neu_zeile" "$UMGEBUNG"; then
    ok "Verbindung in $UMGEBUNG unverändert"
else
    sichern "$UMGEBUNG"
    sed -i "/^ConnectionStrings__${VERBINDUNG}=/d" "$UMGEBUNG"
    printf '%s\n' "$neu_zeile" >> "$UMGEBUNG"
    chown "root:$DIENST" "$UMGEBUNG"; chmod 0640 "$UMGEBUNG"
    ok "Verbindung in $UMGEBUNG eingetragen"
fi
# Eine alte Unit setzt die Verbindung womöglich noch auf SQLite – die Umgebungsdatei hat Vorrang (EnvironmentFile), trotzdem melden
if grep -qsE "ConnectionStrings__${VERBINDUNG}=Data Source" "/etc/systemd/system/$DIENST.service"; then
    warn "/etc/systemd/system/$DIENST.service setzt ConnectionStrings__$VERBINDUNG noch auf SQLite – wirkt nicht mehr (Umgebungsdatei hat Vorrang), Zeile kann weg."
fi

# 4. pg_dump/pg_restore 17 auf dem Host (für Sicherungen der Anwendung selbst; das Kit sichert über den Container)
if ! [[ -x /usr/lib/postgresql/17/bin/pg_dump ]]; then
    if apt-get update -qq && { apt-cache show postgresql-client-17 >/dev/null 2>&1 \
         || { apt-get install -y -qq postgresql-common >/dev/null && /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y >/dev/null && apt-get update -qq; }; } \
       && apt-get install -y -qq postgresql-client-17 >/dev/null; then
        ok "Werkzeuge: $(/usr/lib/postgresql/17/bin/pg_dump --version)"
    else
        warn "postgresql-client-17 ließ sich nicht installieren – die Sicherung des Kits (über den Container) läuft trotzdem."
    fi
else
    ok "Werkzeuge: $(/usr/lib/postgresql/17/bin/pg_dump --version)"
fi

# 5. Alte SQLite-Datei? Hinweis auf die Übernahme
if [[ -n "$SQLITE_ALT" && -f "$DATEN/$SQLITE_ALT" ]]; then
    info "Alte SQLite-Datenbank $DATEN/$SQLITE_ALT gefunden: beim nächsten Ausrollen übernehmen mit  .\deploy\server\deploy.ps1 -SqliteUebernehmen"
    info "   (vorher prüft eine Probe-Übernahme in eine Wegwerf-Datenbank Zeilen und Prüfsummen jeder Tabelle)."
fi
ok "postgresql.sh fertig – jetzt ausrollen: .\deploy\server\deploy.ps1 (der Prüflauf zeigt vorher den Datenbankstand)"
