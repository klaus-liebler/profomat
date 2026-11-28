#!/usr/bin/env bash
# Deploy-Kit (KIT-DATEI): Rückweg zu einrichten.sh – Dienst bzw. Container und Sicherungs-Timer aus, nginx-Einbindung entfernen.
# Daten, Datenbank-Container, Schlüssel, Sicherungen und Umgebung bleiben (Löschen nur bewusst von Hand, siehe README.md).
source "$(dirname "$0")/gemeinsam.sh"
source "$(dirname "$0")/liebler-nginx.sh"
als_root
systemctl disable --now "$DIENST" "$DIENST-sicherung.timer" 2>/dev/null || true
[[ -f "$DOCKER_ORDNER/compose.yml" ]] && docker compose --project-directory "$DOCKER_ORDNER" down >/dev/null 2>&1 || true
liebler_app_entfernen "$DIENST"
ok "$SYSTEM ist aus. Daten unverändert in $DATEN, Datenbank im Container $DB_CONTAINER, Sicherungen in $SICHERUNGEN."
