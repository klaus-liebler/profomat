#!/usr/bin/env bash
# Sicherung von ProfOMat: Datenbank per sqlite3 .backup (konsistent auch während der Dienst läuft, WAL-Modus) mit
# Plausibilitätsprüfung, dazu der Schlüsselordner (Systemsiegel – ohne ihn sind Siegel nicht mehr prüfbar – und Cookie-Schlüssel).
# Aufbewahrung 14 Tage (vor Updates 30 Tage). Ziel: /usr/local/sbin/profomat-sicherung.sh (Schritt 01).
# Aufruf: profomat-sicherung.sh [nacht|vor-update|…]   → /var/backups/profomat/<art>-<Zeit>.db und …-schluessel.tar.gz
# Ausgabe: Pfad der Datenbanksicherung (leer, wenn es noch keine Datenbank gibt)
set -euo pipefail
DIENST="profomat"
DB="/var/lib/$DIENST/profomat.db"
SCHLUESSEL="/var/lib/$DIENST/schluessel"
ZIEL="/var/backups/$DIENST"
art="${1:-nacht}"
zeit=$(date +%Y%m%d-%H%M%S)
install -d -o "$DIENST" -g "$DIENST" -m 0750 "$ZIEL"
umask 0077

if [[ -d "$SCHLUESSEL" ]]; then
    tar -czf "$ZIEL/$art-$zeit-schluessel.tar.gz" -C "$(dirname "$SCHLUESSEL")" "$(basename "$SCHLUESSEL")"
    chown "$DIENST:$DIENST" "$ZIEL/$art-$zeit-schluessel.tar.gz"
fi

if [[ -f "$DB" ]]; then
    datei="$ZIEL/$art-$zeit.db"
    sqlite3 "$DB" ".backup '$datei'"
    # Plausibel? Schemaversion lesbar (EnsureCreated + Tabelle Schemainfo, noch ohne EF-Migrationen)
    v=$(sqlite3 -readonly "$datei" "SELECT Version FROM Schemainfo LIMIT 1;" 2>/dev/null || true)
    [[ "$v" =~ ^[0-9]+$ ]] || { echo "Sicherung unplausibel (keine Schemaversion): $datei" >&2; exit 1; }
    chown "$DIENST:$DIENST" "$datei"; chmod 0640 "$datei"
    echo "$datei"
fi

find "$ZIEL" -maxdepth 1 \( -name 'nacht-*.db' -o -name 'nacht-*-schluessel.tar.gz' \) -mtime +14 -delete
find "$ZIEL" -maxdepth 1 \( -name 'vor-update-*.db' -o -name 'vor-update-*-schluessel.tar.gz' \) -mtime +30 -delete
