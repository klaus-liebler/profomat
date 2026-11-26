# Voraussetzungen vor dem Ausrollen prüfen – ÄNDERT NICHTS (wird per „source“ eingebunden; erwartet gemeinsam.sh).
# Fehler werden gesammelt und am Ende gemeinsam gemeldet (pruef_ende), damit man alles in einem Durchgang beheben kann.
# Allgemein gehalten für Ubuntu/Debian mit systemd; andere Distributionen laufen mit Warnung, solange die Programme da sind.

PRUEF_FEHLER=()
pruef_fehler() { printf '\033[1;31m✗\033[0m %s\n' "$*"; PRUEF_FEHLER+=("$*"); }

# Am Ende der Prüfphase: bei Fehlern abbrechen, bevor irgendetwas geändert wurde.
pruef_ende() {
    local f
    if (( ${#PRUEF_FEHLER[@]} )); then
        printf '\n\033[1;31m%s Voraussetzung(en) nicht erfüllt – NICHTS geändert:\033[0m\n' "${#PRUEF_FEHLER[@]}"
        for f in "${PRUEF_FEHLER[@]}"; do printf '  \033[31m• %s\033[0m\n' "$f"; done
        exit 1
    fi
    ok "alle Voraussetzungen erfüllt"
}

# Paketnamen (Debian/Ubuntu) für fehlende Programme – nur für die Meldung
declare -A PAKET_FUER=( [nginx]=nginx [curl]=curl [sqlite3]=sqlite3 [tar]=tar [ss]=iproute2 [journalctl]=systemd
                        [systemctl]=systemd [docker]=docker.io [flock]=util-linux [df]=coreutils [awk]=gawk )

system_pruefen() {
    local id="" like="" name=""
    if [[ -r /etc/os-release ]]; then
        # shellcheck disable=SC1091
        id=$(. /etc/os-release; echo "${ID:-}"); like=$(. /etc/os-release; echo "${ID_LIKE:-}"); name=$(. /etc/os-release; echo "${PRETTY_NAME:-}")
    fi
    case "$id" in
        ubuntu) ok "System: $name" ;;
        debian) ok "System: $name (wie Ubuntu unterstützt)" ;;
        *)  if [[ " $like " == *" debian "* || " $like " == *" ubuntu "* ]]; then warn "System: $name – Debian-Abkömmling, nicht getestet"
            else warn "System: ${name:-unbekannt} – nicht getestet; Paketnamen in den Meldungen sind die von Ubuntu"; fi ;;
    esac
    [[ -d /run/systemd/system ]] || pruef_fehler "systemd läuft nicht (kein /run/systemd/system) – das Kit braucht systemd."
}

# programme_pruefen <programm> … : alle fehlenden auf einmal melden, mit apt-Befehl
programme_pruefen() {
    local p fehlt=() pakete=()
    for p in "$@"; do command -v "$p" >/dev/null 2>&1 || { fehlt+=("$p"); pakete+=("${PAKET_FUER[$p]:-$p}"); }; done
    if (( ${#fehlt[@]} )); then
        pruef_fehler "Programme fehlen: ${fehlt[*]} – installieren mit: sudo apt install $(printf '%s\n' "${pakete[@]}" | sort -u | tr '\n' ' ')"
    else
        ok "Programme vorhanden: $*"
    fi
}

docker_pruefen() {
    command -v docker >/dev/null 2>&1 || { pruef_fehler "Modus docker: docker fehlt – sudo apt install docker.io docker-compose-v2"; return; }
    docker compose version >/dev/null 2>&1 \
        || { pruef_fehler "Modus docker: „docker compose“ fehlt – sudo apt install docker-compose-v2"; return; }
    docker info >/dev/null 2>&1 || { pruef_fehler "Modus docker: Docker-Dienst läuft nicht – sudo systemctl enable --now docker"; return; }
    ok "Docker $(docker version --format '{{.Server.Version}}' 2>/dev/null) mit Compose $(docker compose version --short 2>/dev/null)"
}

# Belegt jemand anderes den Port? Der eigene Dienst bzw. Container darf ihn halten (wird beim Umschalten gestoppt).
port_pruefen() {
    local zeilen prozess
    zeilen=$(ss -ltnpH "sport = :$PORT" 2>/dev/null || true)
    [[ -z "$zeilen" ]] && { ok "Port $PORT frei"; return; }
    prozess=$(grep -o 'users:(([^)]*' <<<"$zeilen" | head -1 | sed 's/users:((//')
    if systemctl is-active --quiet "$DIENST" 2>/dev/null; then
        local pid; pid=$(systemctl show -p MainPID --value "$DIENST" 2>/dev/null || echo 0)
        if [[ "$zeilen" == *"pid=$pid,"* ]] || [[ "$zeilen" == *"pid=$pid)"* ]]; then ok "Port $PORT gehört dem laufenden Dienst $DIENST"; return; fi
    fi
    if [[ "$prozess" == *docker-proxy* ]] && docker_container_aktiv; then ok "Port $PORT gehört dem Container $DIENST"; return; fi
    pruef_fehler "Port $PORT ist belegt von ${prozess:-unbekannt} (sudo ss -ltnp 'sport = :$PORT') – Prozess beenden oder in konfiguration.env einen freien PORT wählen."
}

docker_container_aktiv() {
    command -v docker >/dev/null 2>&1 && [[ "$(docker inspect -f '{{.State.Running}}' "$DIENST" 2>/dev/null)" == "true" ]]
}

# platz_pruefen <archiv>: genug Platz für entpacktes Paket, vorige Version und Sicherung?
platz_pruefen() {
    local archiv="$1" bedarf_kb frei_kb db_kb=0 ort
    bedarf_kb=$(( $(stat -c %s "$archiv") / 1024 * 4 + 200 * 1024 ))   # entpackt ≈ 3× Archiv, plus Reserve
    [[ -f "$DB" ]] && db_kb=$(( $(stat -c %s "$DB") / 1024 ))
    for ort in /opt "$DATEN" "$SICHERUNGEN"; do
        [[ -d "$ort" ]] || continue
        frei_kb=$(df --output=avail -k "$ort" | tail -1 | tr -d ' ')
        local noetig=$bedarf_kb
        [[ "$ort" == "$SICHERUNGEN" || "$ort" == "$DATEN" ]] && noetig=$(( db_kb * 2 + 50 * 1024 ))
        if (( frei_kb < noetig )); then
            pruef_fehler "Zu wenig Platz in $ort: frei $((frei_kb / 1024)) MB, nötig etwa $((noetig / 1024)) MB (df -h $ort)."
            return
        fi
    done
    ok "Speicherplatz reicht"
}

# Grundgerüst aus Schritt 01 (Dienstbenutzer, Verzeichnisse, Umgebungsdatei, Sicherungsskript)
grundgeruest_pruefen() {
    local fehlt=()
    id "$DIENST" >/dev/null 2>&1 || fehlt+=("Dienstbenutzer $DIENST")
    [[ -d "$DATEN" ]] || fehlt+=("$DATEN")
    [[ -d "$SICHERUNGEN" ]] || fehlt+=("$SICHERUNGEN")
    [[ -x "/usr/local/sbin/$DIENST-sicherung.sh" ]] || fehlt+=("/usr/local/sbin/$DIENST-sicherung.sh")
    if (( ${#fehlt[@]} )); then
        pruef_fehler "Schritt 01 fehlt (${fehlt[*]}) – erst: sudo bash $KIT/schritte/01-einrichten.sh"
    else
        ok "Grundgerüst aus Schritt 01 vorhanden"
    fi
    if [[ -f "$UMGEBUNG" ]] && grep -qsE '^\s*(Anmeldung__TestanmeldungErlaubt|Demodaten__Anlegen)\s*=\s*[Tt]rue' "$UMGEBUNG"; then
        pruef_fehler "In $UMGEBUNG ist Testanmeldung oder Demodaten eingeschaltet – auf dem öffentlichen Server verboten. Zeile entfernen."
    fi
}

uhr_pruefen() {
    if timedatectl show -p NTPSynchronized --value 2>/dev/null | grep -q yes; then ok "Serveruhr synchronisiert"
    else warn "Serveruhr nicht per NTP synchronisiert (timedatectl) – Fristen und Zeitstempel könnten abweichen"; fi
}

# paket_pruefen <entpackter-ordner>: Inhalt passt zum Modus? Keine Daten/Schlüssel im Paket?
paket_pruefen() {
    local ordner="$1" verboten
    [[ -f "$ordner/$APP" || -f "$ordner/$APP.dll" ]] || { pruef_fehler "Paket enthält weder $APP noch $APP.dll – falsches Archiv?"; return; }
    verboten=$(find "$ordner" \( -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm' -o -name '*.pem' -o -name '*.pfx' -o -name '*.key' \
               -o -path '*/data/*' -o -path '*/schluessel/*' \) -print -quit)
    [[ -z "$verboten" ]] || pruef_fehler "Paket enthält Datenbank-, Schlüssel- oder data-Dateien (${verboten#"$ordner"/}) – so nie ausrollen."
    local eigen=0
    grep -qs '"includedFrameworks"' "$ordner/$APP.runtimeconfig.json" && eigen=1
    case "$MODUS" in
        framework)
            if (( eigen )); then pruef_fehler "Modus framework, aber das Paket bringt eine eigene Runtime mit – deploy.ps1 -Modus self-contained verwenden."
            else dotnet_pruefen "$ordner" "$APP" || pruef_fehler "Gemeinsame ASP.NET-Core-Runtime fehlt (Hinweise oben)."; fi ;;
        self-contained)
            if (( eigen )); then ok "Paket bringt seine Runtime selbst mit (self-contained)"
            else pruef_fehler "Modus self-contained, aber das Paket ist framework-abhängig – mit deploy.ps1 -Modus self-contained neu bauen."; fi ;;
        docker)
            if (( eigen )); then warn "Modus docker mit self-contained-Paket: läuft, das Abbild wird aber unnötig groß"; fi
            [[ -f "$KIT/docker/Dockerfile" && -f "$KIT/docker/compose.yml" ]] || pruef_fehler "Kit-Dateien docker/Dockerfile bzw. docker/compose.yml fehlen." ;;
        *) pruef_fehler "Unbekannter Modus „$MODUS“ (framework | self-contained | docker)." ;;
    esac
}
