#!/usr/bin/env bash
# Befristetes Serverkonto „pruefer“ für Sicherheitsprüfungen (Zielbild Abschnitt 6): Anmeldung nur per SSH-Schlüssel, darf
# ausschließlich lesende Befehle mit sudo ausführen (wirksame Proxy- und Dienstkonfiguration, Firewall, Logs, Rechte der Datenordner).
# Das Konto läuft automatisch ab (useradd -e); entfernen jederzeit mit „entfernen“. KIT-DATEI (Vorlage → /usr/local/sbin/@DIENST@-pruefer-konto.sh).
#
#   sudo bash pruefer-konto.sh anlegen <tage> <datei-mit-oeffentlichem-ssh-schluessel>
#   sudo bash pruefer-konto.sh entfernen
#   sudo bash pruefer-konto.sh zeigen
set -euo pipefail
KONTO="pruefer"
DIENST="@DIENST@"
SUDOERS="/etc/sudoers.d/$DIENST-$KONTO"
BERICHT="/usr/local/sbin/$DIENST-pruefbericht"

[[ $EUID -eq 0 ]] || { echo "Bitte mit sudo aufrufen." >&2; exit 1; }

anlegen() {
    local tage="$1" schluessel="$2" bis
    [[ "$tage" =~ ^[0-9]+$ ]] && (( tage >= 1 && tage <= 90 )) || { echo "Tage: 1–90" >&2; exit 1; }
    [[ -s "$schluessel" ]] && grep -qE '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-)' "$schluessel" || { echo "Öffentlicher SSH-Schlüssel fehlt: $schluessel" >&2; exit 1; }
    bis=$(date -d "+$tage days" +%Y-%m-%d)
    if id "$KONTO" >/dev/null 2>&1; then
        chage -E "$bis" "$KONTO"
    else
        useradd -m -s /bin/bash -e "$bis" -c "Sicherheitspruefung (befristet)" "$KONTO"
        passwd -l "$KONTO" >/dev/null   # kein Passwort, nur Schlüssel
    fi
    install -d -o "$KONTO" -g "$KONTO" -m 0700 "/home/$KONTO/.ssh"
    install -o "$KONTO" -g "$KONTO" -m 0600 "$schluessel" "/home/$KONTO/.ssh/authorized_keys"

    # Bericht ohne Geheimnisse: Umgebung des Dienstes mit maskierten Werten für Passwörter, Schlüssel und Geheimnisse
    cat > "$BERICHT" <<'SKRIPT'
#!/usr/bin/env bash
# Nur lesend: Kurzbericht für Prüfende (Geheimnisse maskiert)
set -u
echo "== Dienst";  systemctl show @DIENST@ -p ActiveState,SubState,User,ExecStart --no-pager
echo "== Umgebung (Geheimnisse maskiert)"
systemctl show @DIENST@ -p Environment --no-pager | tr ' ' '\n' | sed -E 's/((Passwort|Password|Secret|Schluessel|Key)[^=]*=).*/\1***/I'
[[ -f /etc/@DIENST@/@DIENST@.env ]] && grep -vE '^\s*(#|$)' /etc/@DIENST@/@DIENST@.env | sed -E 's/((Passwort|Password|Secret|Schluessel|Key)[^=]*=).*/\1***/I'
echo "== Dateirechte"; ls -la /var/lib/@DIENST@ /var/lib/@DIENST@/schluessel /var/backups/@DIENST@ 2>/dev/null | sed -E 's/ [^ ]+$/ …&/'
echo "== Datenbank"; docker ps --filter name=@DIENST@-db --format '{{.Names}} {{.Image}} {{.Status}} {{.Ports}}' 2>/dev/null
echo "== Letzte Sicherung"; cat /var/backups/@DIENST@/letzte-sicherung.txt 2>/dev/null || echo "keine Statusdatei"
SKRIPT
    chmod 0755 "$BERICHT"

    cat > "$SUDOERS.neu" <<EOF
# Befristetes Prüferkonto (bis $bis) – nur lesende Befehle. Angelegt von pruefer-konto.sh.
$KONTO ALL=(root) NOPASSWD: /usr/sbin/nginx -T, /usr/bin/systemctl cat $DIENST, /usr/bin/systemctl status $DIENST, \\
    /usr/bin/systemctl list-timers --all, /usr/sbin/ufw status verbose, /usr/bin/journalctl -u $DIENST --no-pager -n *, \\
    /usr/bin/journalctl -u $DIENST-sicherung --no-pager -n *, $BERICHT
EOF
    visudo -cf "$SUDOERS.neu" >/dev/null
    install -m 0440 "$SUDOERS.neu" "$SUDOERS"
    rm -f "$SUDOERS.neu"
    echo "✓ Konto $KONTO angelegt bzw. verlängert bis $bis (Anmeldung nur mit dem hinterlegten SSH-Schlüssel)."
    echo "  Erlaubt (sudo): nginx -T · systemctl cat/status $DIENST · systemctl list-timers · ufw status · journalctl -u $DIENST · $BERICHT"
}

entfernen() {
    rm -f "$SUDOERS" "$BERICHT"
    if id "$KONTO" >/dev/null 2>&1; then
        pkill -u "$KONTO" 2>/dev/null || true
        userdel -r "$KONTO" 2>/dev/null || userdel "$KONTO"
    fi
    echo "✓ Konto $KONTO und seine sudo-Regel entfernt."
}

zeigen() {
    if id "$KONTO" >/dev/null 2>&1; then chage -l "$KONTO" | grep -i 'account expires\|Konto läuft ab' || true; else echo "Kein Konto $KONTO."; fi
    [[ -f "$SUDOERS" ]] && cat "$SUDOERS"
}

case "${1:-}" in
    anlegen)   [[ $# -eq 3 ]] || { echo "Aufruf: $0 anlegen <tage> <ssh-schluessel.pub>" >&2; exit 1; }; anlegen "$2" "$3" ;;
    entfernen) entfernen ;;
    zeigen)    zeigen ;;
    *)         echo "Aufruf: $0 anlegen <tage> <ssh-schluessel.pub> | entfernen | zeigen" >&2; exit 1 ;;
esac
