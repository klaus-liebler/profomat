#!/usr/bin/env bash
# Rückweg zu Schritt 01: Dienst und Sicherungs-Timer aus, nginx-Einbindung entfernen. Daten, Schlüssel, Sicherungen, Umgebung und
# Passwortdatei bleiben (Löschen nur bewusst von Hand, siehe docs/deploy.md).
source "$(dirname "$0")/gemeinsam.sh"
source "$(dirname "$0")/liebler-nginx.sh"
als_root
systemctl disable --now "$DIENST" "$DIENST-sicherung.timer" 2>/dev/null || true
liebler_app_entfernen "$DIENST"
ok "ProfOMat ist aus. Daten unverändert in $DATEN, Sicherungen in $SICHERUNGEN."
