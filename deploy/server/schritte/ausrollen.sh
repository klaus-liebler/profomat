#!/usr/bin/env bash
# Neue ProfOMat-Version einspielen (von deploy.ps1 aufgerufen). Die Datenbank wird NIE übertragen: Sie liegt in /var/lib/profomat,
# ausgetauscht wird nur /opt/profomat. Vorher Sicherung (Datenbank und Schlüssel), danach Gesundheitsprüfung; antwortet die neue
# Version nicht, kommt die vorige automatisch zurück.
# Aufruf: sudo bash /tmp/profomat-kit/schritte/ausrollen.sh [--db-neu] /tmp/profomat.tar.gz
#   --db-neu  Datenbank (samt -wal/-shm) vor dem Start nach /var/lib/profomat/beiseite/ verschieben; die Anwendung legt sie neu an
#             (danach Ersteinrichtung auf der Anmeldeseite). Nötig, solange es keine EF-Migrationen gibt und sich die Schemaversion ändert
#             („Datenbankschema veraltet“). Der Schlüsselordner (Systemsiegel) bleibt. Scheitert der Start, kommen vorige Version
#             UND vorige Datenbank zurück.
# Rückweg von Hand: sudo bash /tmp/profomat-kit/schritte/ausrollen.sh --zurueck
source "$(dirname "$0")/gemeinsam.sh"
als_root
BEISEITE="$DATEN/beiseite"

db_neu=0
if [[ "${1:-}" == "--db-neu" ]]; then db_neu=1; shift; fi

if [[ "${1:-}" == "--zurueck" ]]; then
    [[ -d "$PROGRAMM.vorher" ]] || fehler "Keine vorige Version vorhanden."
    systemctl stop "$DIENST"
    rm -rf "$PROGRAMM.defekt" && mv "$PROGRAMM" "$PROGRAMM.defekt" && mv "$PROGRAMM.vorher" "$PROGRAMM"
    if systemctl start "$DIENST" && gesund; then ok "vorige Version läuft wieder"; else fehler "auch die vorige Version startet nicht – journalctl -u $DIENST"; fi
    exit 0
fi

# Voraussetzungen aus Schritt 01 – sonst vor jeder Änderung abbrechen
id "$DIENST" >/dev/null 2>&1 && [[ -f "/etc/systemd/system/$DIENST.service" && -d "$DATEN" && -d "$SICHERUNGEN" ]] \
    || fehler "Schritt 01 fehlt (Dienstbenutzer, $DATEN, $DIENST.service) – erst einrichten, dann erneut ausrollen. Nichts geändert."
oeffentlich_pruefen

archiv="${1:?Pfad zum Archiv fehlt}"
[[ -f "$archiv" ]] || fehler "Archiv $archiv fehlt"
neu=$(mktemp -d "$PROGRAMM.neu.XXXX")
tar -xzf "$archiv" -C "$neu" --strip-components=1
[[ -f "$neu/ProfOMat.Web" ]] || { rm -rf "$neu"; fehler "Archiv enthält kein ProfOMat.Web"; }
# Sicherheitsnetz: Daten und Schlüssel gehören nie ins Programm (würden sonst mit ausgerollt)
verboten=$(find "$neu" \( -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm' -o -name '*.pem' -o -path '*/data/*' -o -path '*/schluessel/*' \) -print -quit)
if [[ -n "$verboten" ]]; then
    rm -rf "$neu"
    fehler "Archiv enthält Datenbank-, Schlüssel- oder data-Dateien (${verboten#"$neu"/}) – abgebrochen, nichts geändert."
fi
# Unter Windows gebaute Archive tragen keine Unix-Rechte: Rechte hier festlegen
chown -R root:root "$neu"
find "$neu" -type d -exec chmod 755 {} +
find "$neu" -type f -exec chmod 644 {} +
chmod 755 "$neu/ProfOMat.Web"
if [[ -f "$neu/createdump" ]]; then chmod 755 "$neu/createdump"; fi

# Datenbank und Schlüssel vor dem Update sichern
if ziel=$("/usr/local/sbin/$DIENST-sicherung.sh" vor-update); then
    if [[ -n "$ziel" ]]; then ok "gesichert: $ziel (dazu die Schlüssel)"; fi
else
    rm -rf "$neu"; fehler "Sicherung fehlgeschlagen – nichts geändert."
fi

systemctl stop "$DIENST" 2>/dev/null || true

# --db-neu: Datei samt WAL beiseitelegen (nicht löschen); Ordner nur für den Dienstbenutzer lesbar
beiseite=()
ts=$(date +%Y%m%d-%H%M%S)
if (( db_neu )); then
    install -d -o "$DIENST" -g "$DIENST" -m 0700 "$BEISEITE"
    for f in "$DB" "$DB-wal" "$DB-shm"; do
        if [[ -e "$f" ]]; then
            mv "$f" "$BEISEITE/$(basename "$f").$ts"
            beiseite+=("$f")
            ok "beiseitegelegt: $f → $BEISEITE/$(basename "$f").$ts"
        fi
    done
    (( ${#beiseite[@]} )) || info "--db-neu: keine Datenbank vorhanden, wird ohnehin neu angelegt"
fi

rm -rf "$PROGRAMM.vorher"
if [[ -d "$PROGRAMM" ]]; then mv "$PROGRAMM" "$PROGRAMM.vorher"; fi
mv "$neu" "$PROGRAMM"
start=$(date +%s)
systemctl start "$DIENST"
if gesund 120; then
    ok "neue Version läuft · $(curl -fsS "$GESUND")"
    rm -f "$archiv"
    ersteinrichtung_hinweis
else
    warn "neue Version antwortet nicht – zurück zur vorigen"
    journalctl -u "$DIENST" --since "@$start" -n 30 --no-pager | sed 's/^/   /'
    # erst einsammeln, dann suchen (grep -q in einer Pipe gälte unter pipefail per SIGPIPE als Fehler)
    startlog=$(journalctl -u "$DIENST" --since "@$start" --no-pager -o cat 2>/dev/null || true)
    schema_veraltet=0
    if [[ "$startlog" == *"Datenbankschema veraltet"* ]]; then schema_veraltet=1; fi
    systemctl stop "$DIENST"
    if (( ${#beiseite[@]} )); then
        # neu angelegte Datei verwerfen, vorige Datenbank zurück (passt zur vorigen Version)
        rm -f "$DB" "$DB-wal" "$DB-shm"
        for f in "${beiseite[@]}"; do mv "$BEISEITE/$(basename "$f").$ts" "$f"; done
        ok "vorige Datenbank zurückgelegt"
    fi
    # Hinweis vor dem Rückweg ausgeben (scheitert der, endet das Skript dort)
    if (( schema_veraltet && ! db_neu )); then
        warn "Ursache: Datenbankschema veraltet – erneut ausrollen mit:  .\\deploy\\server\\deploy.ps1 -DbNeu"
    fi
    bash "$0" --zurueck
    fehler "Ausrollen fehlgeschlagen (vorige Version läuft wieder; Sicherung in $SICHERUNGEN)."
fi
