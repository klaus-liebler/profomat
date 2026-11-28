#!/usr/bin/env bash
# Deploy-Kit (KIT-DATEI): Grundgerüst für die Anwendung einrichten – einmalig vor dem ersten Ausrollen, danach idempotent.
# Legt an: Dienstbenutzer, Verzeichnisse, Umgebungsdatei (aus umgebung.beispiel), systemd-Unit (eine vorhandene bleibt, Fehlendes
# kommt ins Drop-in), nächtliche Sicherung mit Rücksicherungsprobe, Prüferkonto-Werkzeug, nginx-Snippets und die eigene
# Einbindung /etc/nginx/liebler.d/<dienst>.conf. Die Datenbank richtet danach postgresql.sh ein, die Anwendung bringt deploy.ps1.
# Voraussetzung: nginx-Grundgerüst mit TLS (NGINX_SITE in konfiguration.env), Docker.
# Start:   ssh -t <ziel> "sudo bash /tmp/<dienst>-kit/schritte/einrichten.sh"
# Rückweg: sudo bash /tmp/<dienst>-kit/schritte/einrichten-zurueck.sh   (Daten, Datenbank und Sicherungen bleiben)
source "$(dirname "$0")/gemeinsam.sh"
source "$(dirname "$0")/liebler-nginx.sh"
source "$(dirname "$0")/pruefen.sh"
source "$(dirname "$0")/konfig-abgleich.sh"
als_root

abschnitt "Einrichten: $SYSTEM ($DIENST) · https://$HOST$PFAD/"
system_pruefen
programme_pruefen curl tar ss systemctl systemd-run flock nginx docker awk
pruef_ende
oeffentlich_pruefen
[[ -f "$NGINX_SITE" ]] || fehler "$NGINX_SITE fehlt – erst das nginx-Grundgerüst mit TLS einrichten (siehe README.md des Kits). Nichts geändert."
belegt=$(ss -ltnH "sport = :$PORT" 2>/dev/null || true)
if [[ -n "$belegt" ]] && ! systemctl is-active --quiet "$DIENST" && ! docker_container_aktiv; then
    fehler "Port $PORT ist schon belegt (sudo ss -ltnp) – in konfiguration.env einen freien Port wählen. Nichts geändert."
fi
uhr_pruefen

id "$DIENST" >/dev/null 2>&1 || { useradd --system --home-dir "$DATEN" --shell /usr/sbin/nologin "$DIENST"; ok "Dienstbenutzer $DIENST angelegt"; }
install -d -o root      -g root      -m 0755 "$PROGRAMM"
install -d -o "$DIENST" -g "$DIENST" -m 0750 "$DATEN" "$LOGS" "$SICHERUNGEN"
for o in $SICHERN_ORDNER; do install -d -o "$DIENST" -g "$DIENST" -m 0700 "$DATEN/$o"; done
install -d -o root      -g "$DIENST" -m 0750 "/etc/$DIENST"
if [[ ! -f "$UMGEBUNG" ]]; then
    install -o root -g "$DIENST" -m 0640 "$KIT/umgebung.beispiel" "$UMGEBUNG"
    ok "$UMGEBUNG angelegt (Schalter und Geheimnisse dort eintragen, nie ins Repository)"
fi

# systemd: Dienst (vorhandene Unit bleibt unverändert, Fehlendes ins Drop-in; Widerspruch → Abbruch)
dienst_planen
pruef_ende
dienst_anwenden
# nächtliche Sicherung und Werkzeuge (Dateien gehören allein dem Kit)
neu=0
if vorlage_einspielen systemd/sicherung.service "/etc/systemd/system/$DIENST-sicherung.service"; then neu=1; fi
if vorlage_einspielen systemd/sicherung.timer "/etc/systemd/system/$DIENST-sicherung.timer"; then neu=1; fi
vorlage_einspielen werkzeuge/sicherung.sh "/usr/local/sbin/$DIENST-sicherung.sh" 0755 || true
vorlage_einspielen werkzeuge/pruefer-konto.sh "/usr/local/sbin/$DIENST-pruefer-konto.sh" 0755 || true
if [[ $neu -eq 1 ]]; then systemctl daemon-reload; fi
systemctl enable --now "$DIENST-sicherung.timer" >/dev/null 2>&1 && ok "nächtliche Sicherung aktiv (→ $SICHERUNGEN, mit Rücksicherungsprobe)"

# nginx: Snippets nach /etc/nginx/snippets, Einbindung als eigene Datei in liebler.d (Einträge anderer Anwendungen bleiben unberührt;
# bei Fehler in nginx -t zurück zum vorigen Stand)
snippets=0
if vorlage_einspielen nginx/proxy.conf "/etc/nginx/snippets/$DIENST-proxy.conf"; then snippets=1; fi
if vorlage_einspielen nginx/locations.conf "/etc/nginx/snippets/$DIENST-locations.conf"; then snippets=1; fi
eintrag=$(mktemp)
nginx_einbindung > "$eintrag"; echo >> "$eintrag"
liebler_app_setzen "$DIENST" "$eintrag"
rm -f "$eintrag"
if (( snippets )); then
    if nginx -t 2>/dev/null; then systemctl reload nginx && ok "nginx neu geladen"
    else nginx -t || true; fehler "nginx-Konfiguration fehlerhaft – vorige Snippets in $SICHERUNG"; fi
fi

if [[ -x "$PROGRAMM/$APP" ]] && grep -qsE "^\s*ConnectionStrings__${VERBINDUNG}=Host=" "$UMGEBUNG"; then
    systemctl restart "$DIENST"
    if gesund 60; then ok "$SYSTEM läuft: $GESUND → ok"; else journalctl -u "$DIENST" -n 30 --no-pager | sed 's/^/   /'; fehler "$SYSTEM antwortet nicht"; fi
fi
grep -qsE "^\s*ConnectionStrings__${VERBINDUNG}=Host=" "$UMGEBUNG" \
    || info "Nächster Schritt: Datenbank einrichten –  sudo bash $KIT/schritte/postgresql.sh"
ok "einrichten.sh fertig · https://$HOST$PFAD/ (Anwendung bringt deploy.ps1)"
