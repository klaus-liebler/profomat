# Start der neuen Version beobachten (wird per „source“ eingebunden; erwartet gemeinsam.sh).
# Rückwärts laufender Balken mit Countdown bis zum automatischen Rückweg. Abgebrochen wird SOFORT, wenn
#   • der Dienst bzw. Container nicht mehr läuft oder neu gestartet wurde (Absturz),
#   • im Startlog eine Zeile auf FEHLER_MUSTER passt (konfiguration.env), z. B. „Datenbankschema veraltet“,
#   • die Wartezeit abläuft, ohne dass /gesund geantwortet hat.
# Erfolg: /gesund antwortet und der Dienst bleibt danach STABIL Sekunden ohne Fehler am Laufen.

STABIL="${STABIL:-10}"
BEOBACHT_GRUND=""

# ---------- Zugriff je Modus ----------
dienst_status() {      # active | activating | failed | inactive … bzw. bei Docker running | restarting | exited …
    if [[ "$MODUS" == "docker" ]]; then docker inspect -f '{{.State.Status}}' "$DIENST" 2>/dev/null || echo "fehlt"
    else systemctl show -p ActiveState --value "$DIENST" 2>/dev/null || echo "unbekannt"; fi
}
dienst_neustarts() {
    if [[ "$MODUS" == "docker" ]]; then docker inspect -f '{{.RestartCount}}' "$DIENST" 2>/dev/null || echo 0
    else systemctl show -p NRestarts --value "$DIENST" 2>/dev/null || echo 0; fi
}
dienst_log() {         # <seit-epoch>
    if [[ "$MODUS" == "docker" ]]; then docker logs --since "$1" "$DIENST" 2>&1 || true
    else journalctl -u "$DIENST" --since "@$1" -o cat --no-pager 2>/dev/null || true; fi
}

# ---------- Anzeige ----------
_balken() {            # <rest> <max> <text>
    local rest="$1" max="$2" text="$3" breite=30 voll i b="" farbe='1;33'
    (( rest < 0 )) && rest=0
    voll=$(( (rest * breite + max - 1) / max ))
    for (( i=0; i<breite; i++ )); do if (( i < voll )); then b+="█"; else b+="░"; fi; done
    (( rest * 5 <= max )) && farbe='1;31'
    if [[ -t 1 ]]; then
        printf '\r\033[K\033[%sm⏳ [%s] %3d s bis Rückweg\033[0m  %s' "$farbe" "$b" "$rest" "$text"
    elif (( rest % 10 == 0 )); then
        printf '⏳ %3d s bis Rückweg · %s\n' "$rest" "$text"
    fi
}
_balken_ende() { [[ -t 1 ]] && printf '\r\033[K'; return 0; }

# beobachten <start-epoch>: 0 = neue Version läuft stabil, 1 = gescheitert (Grund in BEOBACHT_GRUND)
beobachten() {
    local start="$1" max="$WARTEZEIT" t0 jetzt rest status n0 n log treffer gesund_seit=0 text
    t0=$(date +%s)
    n0=$(dienst_neustarts)
    info "Startbeobachtung: höchstens $max s bis zum automatischen Rückweg (Abbruch sofort bei Absturz oder Fehler im Log)"
    while :; do
        jetzt=$(date +%s); rest=$(( max - (jetzt - t0) ))
        status=$(dienst_status)
        log=$(dienst_log "$start")
        if treffer=$(grep -m1 -iE -- "$FEHLER_MUSTER" <<<"$log"); then
            _balken_ende; BEOBACHT_GRUND="Fehler im Startlog: ${treffer:0:300}"; return 1
        fi
        case "$status" in
            failed|inactive|deactivating|exited|dead|fehlt)
                _balken_ende; BEOBACHT_GRUND="Dienst läuft nicht mehr (Status: $status)"; return 1 ;;
        esac
        n=$(dienst_neustarts)
        if [[ "$status" == "restarting" ]] || (( n > n0 )); then
            _balken_ende; BEOBACHT_GRUND="Dienst ist abgestürzt und wurde neu gestartet (Neustarts: $n)"; return 1
        fi
        if (( ! gesund_seit )) && curl -fsS -m 2 -o /dev/null "$GESUND" 2>/dev/null; then gesund_seit=$jetzt; fi
        if (( gesund_seit )) && (( jetzt - gesund_seit >= STABIL )); then
            _balken_ende; ok "neue Version läuft stabil (/gesund antwortet seit $STABIL s, keine Fehler im Log)"; return 0
        fi
        if (( rest <= 0 && ! gesund_seit )); then
            _balken_ende; BEOBACHT_GRUND="keine Antwort von $GESUND nach $max s (Status: $status)"; return 1
        fi
        if (( gesund_seit )); then text="Dienst: $status · /gesund: ok · Stabilitätsprüfung $(( STABIL - (jetzt - gesund_seit) )) s"
        else text="Dienst: $status · /gesund: noch keine Antwort"; fi
        _balken "$rest" "$max" "$text"
        sleep 1
    done
}
