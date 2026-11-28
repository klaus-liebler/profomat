#!/usr/bin/env bash
# Deploy-Kit (KIT-DATEI): Rückweg zu postgresql.sh – Container anhalten, Umgebungsdatei aus der letzten Sicherung von postgresql.sh.
# Die Daten im Volume „<dienst>-db“ bleiben erhalten. Eine ältere Version danach mit „ausrollen.sh --zurueck“ zurückholen.
source "$(dirname "$0")/gemeinsam.sh"
als_root

letzte=$(ls -1d /root/"$DIENST"-sicherung/*-postgresql 2>/dev/null | tail -1 || true)
systemctl stop "$DIENST" 2>/dev/null || true
[[ -f "$DOCKER_ORDNER/compose.yml" ]] && docker compose --project-directory "$DOCKER_ORDNER" stop >/dev/null 2>&1 || true
docker stop "$DB_CONTAINER" >/dev/null 2>&1 && ok "Container $DB_CONTAINER angehalten" || warn "Container $DB_CONTAINER lief nicht"
if [[ -n "$letzte" && -f "$letzte$UMGEBUNG" ]]; then cp -a "$letzte$UMGEBUNG" "$UMGEBUNG" && ok "zurück: $UMGEBUNG"; else warn "keine Sicherung der Umgebungsdatei gefunden – unverändert"; fi
ok "Rückweg fertig – vorige Version: sudo bash $KIT/schritte/ausrollen.sh --zurueck (Daten im Volume $DIENST-db bleiben)"
