#!/usr/bin/env bash
# Schritt 01: ProfOMat auf dem Server einrichten, auf dem schon das VibeVHS-Kit läuft (nginx, Zertifikat, Firewall, sqlite3).
# Legt an: Dienstbenutzer, Verzeichnisse, Umgebungsdatei, systemd-Unit, nächtliche Sicherung, nginx-Snippets mit Zugangspasswort
# und die eigene nginx-Datei /etc/nginx/liebler.d/profomat.conf. Die Anwendung selbst bringt deploy.ps1.
# Start:   ssh -t srvadmin@liebler.iui.hs-osnabrueck.de "sudo bash /tmp/profomat-kit/schritte/01-einrichten.sh"
# Rückweg: sudo bash /tmp/profomat-kit/schritte/01-zurueck.sh   (Daten in /var/lib/profomat bleiben)
source "$(dirname "$0")/gemeinsam.sh"
source "$(dirname "$0")/liebler-nginx.sh"
als_root

command -v nginx >/dev/null && command -v sqlite3 >/dev/null && command -v htpasswd >/dev/null \
    || fehler "nginx, sqlite3 oder htpasswd fehlen – erst das VibeVHS-Kit (Schritt 02/03) einspielen. Nichts geändert."
[[ -f "$NGINX_SITE" ]] || fehler "$NGINX_SITE fehlt (VibeVHS-Schritt 03). Nichts geändert."
belegt=$(ss -ltnH "sport = :$PORT" 2>/dev/null || true)
if [[ -n "$belegt" ]] && ! systemctl is-active --quiet "$DIENST"; then
    fehler "Port $PORT ist schon belegt (sudo ss -ltnp) – in konfiguration.env einen freien Port wählen. Nichts geändert."
fi

# Zeitzone: Die Anwendung rechnet mit ProfOMat:Zeitzone (Europe/Berlin) und speichert UTC – die Uhr muss stimmen.
if timedatectl show -p NTPSynchronized --value 2>/dev/null | grep -q yes; then ok "Serveruhr synchronisiert"; else warn "Serveruhr nicht per NTP synchronisiert (timedatectl)"; fi

id "$DIENST" >/dev/null 2>&1 || { useradd --system --home-dir "$DATEN" --shell /usr/sbin/nologin "$DIENST"; ok "Dienstbenutzer $DIENST angelegt"; }
install -d -o root      -g root      -m 0755 "$PROGRAMM"
install -d -o "$DIENST" -g "$DIENST" -m 0750 "$DATEN" "$LOGS" "$SICHERUNGEN"
install -d -o "$DIENST" -g "$DIENST" -m 0700 "$DATEN/schluessel"   # Systemsiegel- und Cookie-Schlüssel
install -d -o root      -g "$DIENST" -m 0750 "/etc/$DIENST"
if [[ ! -f "$UMGEBUNG" ]]; then
    install -o root -g "$DIENST" -m 0640 "$KIT/profomat.env.beispiel" "$UMGEBUNG"
    ok "$UMGEBUNG angelegt (Schalter und Geheimnisse dort eintragen, nie ins Repository)"
fi

# systemd: Dienst und nächtliche Sicherung
neu=0
if vorlage_einspielen systemd/profomat.service "/etc/systemd/system/$DIENST.service"; then neu=1; fi
if vorlage_einspielen systemd/profomat-sicherung.service "/etc/systemd/system/$DIENST-sicherung.service"; then neu=1; fi
if vorlage_einspielen systemd/profomat-sicherung.timer "/etc/systemd/system/$DIENST-sicherung.timer"; then neu=1; fi
vorlage_einspielen werkzeuge/sicherung.sh "/usr/local/sbin/$DIENST-sicherung.sh" 0755 || true
if [[ $neu -eq 1 ]]; then systemctl daemon-reload; fi
systemctl enable "$DIENST" >/dev/null 2>&1
systemctl enable --now "$DIENST-sicherung.timer" >/dev/null 2>&1 && ok "nächtliche Sicherung aktiv (02:00 Uhr → $SICHERUNGEN)"

# Zugangspasswort (nginx Basic Auth) – solange es keine Hochschulanmeldung gibt, Pflicht
if [[ ! -s "$HTPASSWD" ]]; then
    info "Zugangspasswort für $PFAD anlegen (Benutzer $ZUGANG_BENUTZER) – htpasswd fragt nach dem Passwort:"
    htpasswd -c -B "$HTPASSWD" "$ZUGANG_BENUTZER"
    chown root:www-data "$HTPASSWD"
    chmod 0640 "$HTPASSWD"
    ok "$HTPASSWD angelegt"
fi

# nginx: Snippets nach /etc/nginx/snippets, Einbindung als eigene Datei /etc/nginx/liebler.d/$DIENST.conf
# (gemeinsames Schema mit VibeVHS, FraktionPerfekt und Podium, schritte/liebler-nginx.sh): die Einträge anderer Apps bleiben
# unberührt; bei Fehler (nginx -t) zurück zum vorigen Stand.
snippets=0
if vorlage_einspielen nginx/profomat-proxy.conf "/etc/nginx/snippets/$DIENST-proxy.conf"; then snippets=1; fi
if vorlage_einspielen nginx/profomat-locations.conf "/etc/nginx/snippets/$DIENST-locations.conf"; then snippets=1; fi
if vorlage_einspielen nginx/profomat-ohne-passwort.conf "/etc/nginx/snippets/$DIENST-ohne-passwort.conf"; then snippets=1; fi
eintrag=$(mktemp)
printf '# ProfOMat – Ziel: %s/%s.conf (Kit profomat, Schritt 01)\ninclude /etc/nginx/snippets/%s-locations.conf;\n' "$LIEBLER_D" "$DIENST" "$DIENST" > "$eintrag"
liebler_app_setzen "$DIENST" "$eintrag"
rm -f "$eintrag"
# Snippets geändert, Einbindung aber gleich geblieben: nginx trotzdem prüfen und neu laden
if (( snippets )); then
    if nginx -t 2>/dev/null; then systemctl reload nginx && ok "nginx neu geladen"
    else nginx -t || true; fehler "nginx-Konfiguration fehlerhaft – vorige Snippets in $SICHERUNG"; fi
fi

anmeldung_pruefen

if [[ -x "$PROGRAMM/ProfOMat.Web" ]]; then
    systemctl restart "$DIENST"
    if gesund 60; then ok "ProfOMat läuft: $GESUND → ok"; else journalctl -u "$DIENST" -n 30 --no-pager | sed 's/^/   /'; fehler "ProfOMat antwortet nicht"; fi
else
    warn "Noch keine Anwendung in $PROGRAMM – erst $UMGEBUNG ausfüllen, dann vom Entwicklungsrechner:  .\\deploy\\server\\deploy.ps1"
fi
ok "Schritt 01 fertig · https://$HOST$PFAD/ (Zugangspasswort: Benutzer $ZUGANG_BENUTZER)"
