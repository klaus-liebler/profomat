# Konfiguration abgleichen: systemd-Unit und nginx (wird per „source“ eingebunden; erwartet gemeinsam.sh, pruefen.sh,
# liebler-nginx.sh). KIT-DATEI. Grundsatz: Bestehende Einträge werden NIE verändert.
#   • Fehlt etwas, wird es ergänzt – bei systemd in einer eigenen Drop-in-Datei (/etc/systemd/system/<dienst>.service.d/50-kit.conf),
#     die Unit selbst bleibt, wie sie ist.
#   • Steht etwas anders da als erwartet (fremder Port, anderes Programm, fremde nginx-Datei mit unserem Pfad …), bricht die
#     Prüfphase ab, bevor irgendetwas geändert wurde. Ausnahme: Härtungs-Schalter, die bewusst anders gesetzt sind (nur Warnung).
# Ablauf: *_planen (nur lesen, Fehler über pruef_fehler) → *_anwenden (ändert, merkt sich jeden Vorzustand) → bei Bedarf konfig_zurueck.

UNIT="/etc/systemd/system/$DIENST.service"
DROPIN_ORDNER="/etc/systemd/system/$DIENST.service.d"
DROPIN="$DROPIN_ORDNER/50-kit.conf"
# Abweichung erlaubt (Warnung statt Abbruch): Härtung und Neustartverhalten darf der Betrieb bewusst anders einstellen.
WEICHE_SCHLUESSEL=" Type NoNewPrivileges ProtectSystem ProtectHome PrivateTmp PrivateDevices ProtectKernelTunables ProtectKernelModules ProtectControlGroups RestrictSUIDSGID LockPersonality UMask Restart RestartSec TimeoutStopSec "

# ---------- Vorzustände merken und wiederherstellen ----------
KONFIG_PROTOKOLL=()   # Einträge „<datei>|<sicherung>“; Sicherung „-“ = Datei gab es vorher nicht
konfig_merken() {
    local d="$1" e s
    for e in "${KONFIG_PROTOKOLL[@]}"; do [[ "${e%%|*}" == "$d" ]] && return 0; done
    if [[ -e "$d" ]]; then
        s="$SICHERUNG/konfig$d"; mkdir -p "$(dirname "$s")"; cp -a "$d" "$s"
        KONFIG_PROTOKOLL+=("$d|$s")
    else
        KONFIG_PROTOKOLL+=("$d|-")
    fi
}
# konfig_zurueck [<protokolldatei>]: alles in umgekehrter Reihenfolge zurück, danach systemd und nginx neu laden.
konfig_zurueck() {
    local eintraege=("${KONFIG_PROTOKOLL[@]}") i d s
    if [[ -n "${1:-}" && -f "$1" ]]; then mapfile -t eintraege < "$1"; fi
    (( ${#eintraege[@]} )) || return 0
    for (( i=${#eintraege[@]}-1; i>=0; i-- )); do
        d="${eintraege[i]%%|*}"; s="${eintraege[i]#*|}"
        if [[ "$s" == "-" ]]; then rm -rf "$d"; ok "zurück: $d entfernt (gab es vorher nicht)"
        else cp -a "$s" "$d"; ok "zurück: $d wiederhergestellt"; fi
    done
    rmdir "$DROPIN_ORDNER" 2>/dev/null || true
    systemctl daemon-reload
    if command -v nginx >/dev/null 2>&1; then
        if nginx -t 2>/dev/null; then systemctl reload nginx 2>/dev/null || true; else warn "nginx -t meldet Fehler nach dem Zurücklegen – bitte prüfen"; fi
    fi
}
konfig_protokoll_schreiben() { printf '%s\n' "${KONFIG_PROTOKOLL[@]}" > "$1"; }

# ---------- systemd ----------
# Einträge des Abschnitts [Service] als „schlüssel<TAB>wert“; Environment je Variable als „Environment:NAME“.
_service_eintraege() {
    awk '
        /^[ \t]*[#;]/ || /^[ \t]*$/ { next }
        /^[ \t]*\[/ { a = $0; gsub(/[ \t\[\]]/, "", a); next }
        a == "Service" {
            i = index($0, "="); if (!i) next
            k = substr($0, 1, i - 1); v = substr($0, i + 1)
            gsub(/^[ \t]+|[ \t]+$/, "", k); gsub(/^[ \t]+|[ \t]+$/, "", v)
            if (k == "Environment") {
                if (v ~ /^".*"$/) v = substr(v, 2, length(v) - 2)
                j = index(v, "="); if (!j) next
                k = "Environment:" substr(v, 1, j - 1); v = substr(v, j + 1)
            }
            print k "\t" v
        }' "$@"
}

DIENST_AKTION=""        # anlegen | ergaenzen | ""
DIENST_ERGAENZUNG=()    # fehlende Zeilen für das Drop-in
dienst_planen() {
    local soll fragment ist_dateien=() k v zeile
    soll=$(mktemp); vorlage_fuellen systemd/app.service > "$soll"
    DIENST_AKTION=""; DIENST_ERGAENZUNG=()

    fragment=$(systemctl show -p FragmentPath --value "$DIENST" 2>/dev/null || true)
    if [[ -n "$fragment" && "$fragment" != "$UNIT" ]]; then
        pruef_fehler "Die Unit $DIENST.service liegt unerwartet unter $fragment (erwartet: $UNIT) – bitte von Hand klären."
        rm -f "$soll"; return
    fi
    if [[ ! -f "$UNIT" ]]; then
        DIENST_AKTION="anlegen"; info "systemd: $UNIT fehlt – wird aus der Vorlage angelegt"
        rm -f "$soll"; return
    fi

    ist_dateien=("$UNIT")
    for zeile in "$DROPIN_ORDNER"/*.conf; do [[ -f "$zeile" && "$zeile" != "$DROPIN" ]] && ist_dateien+=("$zeile"); done
    declare -A SOLL=() IST=()
    while IFS=$'\t' read -r k v; do SOLL[$k]="$v"; done < <(_service_eintraege "$soll")
    while IFS=$'\t' read -r k v; do
        if [[ "$k" == "ReadWritePaths" ]]; then IST[$k]="${IST[$k]:-} $v"; else IST[$k]="$v"; fi
    done < <(_service_eintraege "${ist_dateien[@]}")
    rm -f "$soll"

    local konflikte=0 anzeigen
    for k in "${!SOLL[@]}"; do
        v="${SOLL[$k]}"
        anzeigen="${k/Environment:/Environment=}"; [[ "$k" == Environment:* ]] && anzeigen="Environment=${k#Environment:}=$v" || anzeigen="$k=$v"
        if [[ -z "${IST[$k]+x}" ]]; then
            if [[ "$k" == Environment:* ]]; then DIENST_ERGAENZUNG+=("Environment=\"${k#Environment:}=$v\""); else DIENST_ERGAENZUNG+=("$k=$v"); fi
            continue
        fi
        if [[ "$k" == "ReadWritePaths" ]]; then
            local p fehlt=()
            for p in $v; do [[ " ${IST[$k]} " == *" $p "* ]] || fehlt+=("$p"); done
            (( ${#fehlt[@]} )) && DIENST_ERGAENZUNG+=("ReadWritePaths=${fehlt[*]}")
            continue
        fi
        [[ "${IST[$k]}" == "$v" ]] && continue
        if [[ "$WEICHE_SCHLUESSEL" == *" $k "* ]]; then
            warn "systemd: $k=${IST[$k]} (Vorlage: $v) – bewusst anders eingestellt? Bleibt so."
        else
            local wo; wo=$(grep -nH -E "^\s*${k%%:*}=" "${ist_dateien[@]}" 2>/dev/null | grep -F "${k#Environment:}" | tail -1 || true)
            pruef_fehler "systemd: erwartet $anzeigen, gefunden ${IST[$k]} (${wo%%:*}${wo:+:}$(cut -d: -f2 <<<"$wo")) – Eintrag wird nicht überschrieben; von Hand angleichen oder Vorlage ändern."
            konflikte=1
        fi
    done
    (( konflikte )) && return
    if (( ${#DIENST_ERGAENZUNG[@]} )); then
        DIENST_AKTION="ergaenzen"
        info "systemd: ${#DIENST_ERGAENZUNG[@]} fehlende(r) Eintrag/Einträge werden in $DROPIN ergänzt:"
        printf '     %s\n' "${DIENST_ERGAENZUNG[@]}"
    else
        ok "systemd: $UNIT enthält alle nötigen Einträge"
    fi
}

dienst_anwenden() {
    local tmp neu=0
    case "$DIENST_AKTION" in
        anlegen)
            konfig_merken "$UNIT"
            vorlage_fuellen systemd/app.service > "$UNIT"; chmod 0644 "$UNIT"; neu=1
            ok "systemd: $UNIT angelegt" ;;
        ergaenzen)
            konfig_merken "$DROPIN"
            tmp=$(mktemp)
            { echo "# Vom Kit ergänzt ($(date '+%d.%m.%Y %H:%M')): Einträge, die in $UNIT fehlten. Die Unit selbst bleibt unverändert."
              echo "# Wird bei jedem Ausrollen neu geschrieben; eigene Einstellungen in eine andere Datei dieses Ordners."
              echo "[Service]"; printf '%s\n' "${DIENST_ERGAENZUNG[@]}"; } > "$tmp"
            if [[ -f "$DROPIN" ]] && cmp -s "$tmp" "$DROPIN"; then rm -f "$tmp"; ok "systemd: $DROPIN unverändert"
            else install -D -m 0644 "$tmp" "$DROPIN"; rm -f "$tmp"; neu=1; ok "systemd: $DROPIN geschrieben"; fi ;;
        *)
            # nichts fehlt mehr: ein früheres Drop-in des Kits wird überflüssig
            if [[ -f "$DROPIN" ]]; then konfig_merken "$DROPIN"; rm -f "$DROPIN"; neu=1; ok "systemd: $DROPIN nicht mehr nötig, entfernt"; fi ;;
    esac
    if (( neu )); then
        systemctl daemon-reload
        if ! systemd-analyze verify "$UNIT" >/dev/null 2>&1; then warn "systemd-analyze verify meldet Hinweise zu $UNIT (journalctl prüfen)"; fi
    fi
    systemctl is-enabled --quiet "$DIENST" 2>/dev/null || { systemctl enable "$DIENST" >/dev/null 2>&1 && ok "systemd: $DIENST beim Systemstart aktiv"; }
}

# ---------- nginx (gemeinsames Schema liebler.d, siehe liebler-nginx.sh) ----------
NGINX_DATEIEN=()   # „<vorlage>|<ziel>“, die ersetzt werden (nur eigene Dateien des Kits)
NGINX_EINBINDUNG="" # Inhalt für liebler.d/<dienst>.conf
_eigene_datei() {   # <ziel> <erste-zeile-der-vorlage>: gehört die vorhandene Datei dem Kit?
    [[ "$(head -1 "$1")" == "$2" ]]
}
# Inhalt von $LIEBLER_D/<dienst>.conf (eine Zeile Kopf zur Wiedererkennung, dann die Einbindung)
nginx_einbindung() { printf '# %s – Ziel: %s/%s.conf (Deploy-Kit %s)\ninclude /etc/nginx/snippets/%s-locations.conf;' "$SYSTEM" "$LIEBLER_D" "$DIENST" "$DIENST" "$DIENST"; }

nginx_planen() {
    local paar vorlage ziel soll erste
    NGINX_DATEIEN=()
    if ! nginx -t >/dev/null 2>&1; then
        pruef_fehler "nginx-Konfiguration ist schon VOR dem Ausrollen fehlerhaft (sudo nginx -t) – erst beheben, dann ausrollen."; return
    fi
    [[ -f "$NGINX_SITE" ]] || { pruef_fehler "$NGINX_SITE fehlt – erst das nginx-Grundgerüst einrichten (VibeVHS-Kit, Schritt 03) oder NGINX_SITE in konfiguration.env anpassen."; return; }
    if ! grep -qF "$LIEBLER_INCLUDE" "$NGINX_SITE" && ! grep -qF "$LIEBLER_ANKER" "$NGINX_SITE"; then
        pruef_fehler "$NGINX_SITE enthält weder „$LIEBLER_INCLUDE“ noch die Zertifikatszeile als Anker – Einbindung bitte von Hand in den 443-Server-Block setzen."
    fi
    grep -rqsE 'map\s+\$http_upgrade\s+\$connection_upgrade' /etc/nginx \
        || pruef_fehler "nginx: „map \$http_upgrade \$connection_upgrade“ fehlt (Blazor braucht WebSockets) – gehört ins Grundgerüst (VibeVHS-Kit)."

    for paar in "nginx/proxy.conf|/etc/nginx/snippets/$DIENST-proxy.conf" "nginx/locations.conf|/etc/nginx/snippets/$DIENST-locations.conf"; do
        vorlage="${paar%%|*}"; ziel="${paar#*|}"
        soll=$(vorlage_fuellen "$vorlage"); erste=$(head -1 <<<"$soll")
        if [[ -f "$ziel" ]]; then
            if [[ "$soll" == "$(cat "$ziel")" ]]; then ok "nginx: $ziel unverändert"; continue; fi
            _eigene_datei "$ziel" "$erste" || { pruef_fehler "nginx: $ziel gibt es schon, stammt aber nicht aus diesem Kit (erste Zeile anders) – wird nicht überschrieben."; continue; }
            info "nginx: $ziel wird auf den Stand des Kits gebracht"
        else
            info "nginx: $ziel wird angelegt"
        fi
        NGINX_DATEIEN+=("$paar")
    done
    NGINX_EINBINDUNG=$(nginx_einbindung)
    # eigene Datei = erste Zeile nennt das Ziel (auch die Kopfzeilen früherer Kit-Fassungen: „… – Ziel: …/<dienst>.conf (Kit …)“)
    if [[ -f "$LIEBLER_D/$DIENST.conf" ]] && ! head -1 "$LIEBLER_D/$DIENST.conf" | grep -qF "Ziel: $LIEBLER_D/$DIENST.conf"; then
        pruef_fehler "nginx: $LIEBLER_D/$DIENST.conf stammt nicht aus diesem Kit – wird nicht überschrieben."
    fi

    # Kollisionen: derselbe Pfad oder derselbe Port in fremden Dateien → früher Abbruch
    local treffer
    treffer=$(grep -RnsE "location\s+(=\s*|\^~\s*)?${PFAD}(/|\s|\{|$)|127\.0\.0\.1:${PORT}([^0-9]|$)" \
                /etc/nginx/sites-enabled /etc/nginx/conf.d /etc/nginx/liebler.d /etc/nginx/snippets 2>/dev/null \
              | grep -vE "/(snippets/$DIENST-(proxy|locations)\.conf|liebler\.d/$DIENST\.conf):" | head -3 || true)
    if [[ -n "$treffer" ]]; then
        pruef_fehler "nginx: Pfad $PFAD oder Port $PORT ist schon anderweitig vergeben:"$'\n'"$(sed 's/^/      /' <<<"$treffer")"
    fi

    # Härtung des Grundgerüsts (gehört dem VibeVHS-Kit): nur Hinweise
    grep -RqsE '^\s*server_tokens\s+off' /etc/nginx/nginx.conf "$NGINX_SITE" || warn "nginx: server_tokens off fehlt (Versionsangabe sichtbar)"
    grep -qsE '^\s*ssl_protocols\s+TLSv1\.[23]' "$NGINX_SITE" /etc/nginx/nginx.conf /etc/nginx/snippets/*.conf || warn "nginx: ssl_protocols nicht auf TLS 1.2/1.3 beschränkt gefunden"
}

nginx_anwenden() {
    local paar vorlage ziel geaendert=0 eintrag
    for paar in "${NGINX_DATEIEN[@]}"; do
        vorlage="${paar%%|*}"; ziel="${paar#*|}"
        konfig_merken "$ziel"
        vorlage_fuellen "$vorlage" > "$ziel.neu"; chmod 0644 "$ziel.neu"; mv "$ziel.neu" "$ziel"
        ok "nginx: $ziel eingespielt"; geaendert=1
    done
    konfig_merken "$LIEBLER_D/$DIENST.conf"; konfig_merken "$NGINX_SITE"
    eintrag=$(mktemp); printf '%s\n' "$NGINX_EINBINDUNG" > "$eintrag"
    liebler_app_setzen "$DIENST" "$eintrag"     # transaktional: nginx -t, sonst voriger Stand
    rm -f "$eintrag"
    if (( geaendert )); then
        if nginx -t 2>/dev/null; then systemctl reload nginx && ok "nginx neu geladen"
        else nginx -t || true; konfig_zurueck; fehler "nginx-Konfiguration fehlerhaft – voriger Stand wiederhergestellt."; fi
    fi
}
