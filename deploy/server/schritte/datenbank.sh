# Datenbank beim Ausrollen (wird per „source“ eingebunden; erwartet gemeinsam.sh und pruefen.sh). KIT-DATEI.
#
# Vertrag mit der Anwendung (in allen vier Systemen gleich, siehe ../README.md):
#   <APP> --datenbankstand   liest nur; je Datenbank eine Zeile
#                            DATENBANKSTAND|rolle|datenbank|zustand|eingespielt|offen|hinweis
#                            zustand: Neu | Aktuell | Migrierbar | Neuaufbau | Fremd | Geschuetzt | NichtLesbar
#                            Exitcode: 0 = startbereit (auch neu oder mit Migration), 3 = nur mit neu aufgebauter Datenbank (-DbNeu),
#                            4 = nicht startbar, -DbNeu hilft nicht bzw. ist verboten, 5 = keine Verbindung
#   <APP> --sqlite-uebernehmen <datei> [--probe]   (nur Systeme mit alter SQLite-Datenbank, SQLITE_ALT in konfiguration.env)
#                            je Tabelle UEBERNAHME|tabelle|quelle|ziel|gleich, zuletzt UEBERNAHME_ERGEBNIS|ok|… bzw. |fehler|…
# Beide laufen mit der NEUEN Version, als Dienstbenutzer und mit der Umgebung des Dienstes, während die alte Version weiterläuft.

DB_LAGE=""            # neu | gleich | migration | neuaufbau
DB_ZEILEN=""          # Ausgabe von --datenbankstand (nur DATENBANKSTAND-Zeilen)
DB_UEBERNAHME=0       # 1 = alte SQLite-Daten werden beim Umschalten übernommen
SQLITE_DATEI=""
[[ -n "$SQLITE_ALT" ]] && SQLITE_DATEI="$DATEN/$SQLITE_ALT"

# Text der großen roten Warnung bei -DbNeu (auch vom Rückweg genutzt)
DBNEU_WARNUNG=(
    "-DbNeu: Die Datenbank $DB_NAME wird beiseitegelegt (umbenannt, nicht gelöscht),"
    "aber NICHT in die neue Version übernommen."
    "Nach dem Start sind in der Anwendung ALLE DATEN WEG – die Anwendung baut sie neu auf."
    "Zurück kommen die Daten nur mit dem Rückweg (deploy.ps1 -Zurueck),"
    "der vorige Version UND vorige Datenbank wieder einsetzt."
)

# app_werkzeug <quelle> <argumente …>: die NEUE Version einmal als Werkzeug starten – als Dienstbenutzer, mit der Umgebung des Dienstes.
#   <quelle> = entpackter Programmordner (nativ) bzw. Abbild (docker). Ändert selbst nichts; was die Argumente tun, entscheidet die App.
app_werkzeug() {
    local quelle="$1" kv; shift
    local -a args=()
    if [[ "$MODUS" == "docker" ]]; then
        # shellcheck disable=SC1090
        source "$PG_UMGEBUNG"
        args=(--rm --network "$DB_NETZ" --user "$(id -u "$DIENST"):$(id -g "$DIENST")" --read-only --tmpfs /tmp
              -v "$DATEN:$DATEN" -v "$LOGS:$LOGS" -v "$SICHERUNGEN:$SICHERUNGEN" --env-file "$UMGEBUNG")
        while IFS= read -r kv; do args+=(-e "$kv"); done < <(umgebung_zeilen docker)
        args+=(-e "ConnectionStrings__$VERBINDUNG=Host=$DB_CONTAINER;Port=5432;Database=$DB_NAME;Username=$DIENST;Password=$POSTGRES_PASSWORD")
        timeout 900 docker run "${args[@]}" "$quelle" "$@"
    else
        args=(--quiet --wait --pipe --collect -p "User=$DIENST" -p "Group=$DIENST" -p "WorkingDirectory=$quelle" -p RuntimeMaxSec=900
              -p "EnvironmentFile=$UMGEBUNG" -E DOTNET_NOLOGO=1 -E "ASPNETCORE_URLS=http://127.0.0.1:0")
        while IFS= read -r kv; do args+=(-E "$kv"); done < <(umgebung_zeilen nativ)
        systemd-run "${args[@]}" "$quelle/$APP" "$@"
    fi
}

# db_planen <quelle> <db_neu 0|1> <sqlite: uebernehmen|ignorieren|"">: was würde ein Start tun? Fehler über pruef_fehler.
db_planen() {
    local quelle="$1" db_neu="$2" sqlite="$3" ausgabe code rolle name zustand ein offen hinweis haupt_neu=0
    info "Prüflauf der neuen Version: Was würde ein Start mit den Datenbanken tun?"
    set +e; ausgabe=$(app_werkzeug "$quelle" --datenbankstand 2>&1); code=$?; set -e
    DB_ZEILEN=$(grep '^DATENBANKSTAND|' <<<"$ausgabe" || true)
    local migrierbar=0 neu=0
    while IFS='|' read -r _ rolle name zustand ein offen hinweis; do
        [[ -z "$rolle" ]] && continue
        printf '   %-12s %-28s %-12s %s\n' "$rolle" "$name" "$zustand" "$hinweis"
        case "$zustand" in
            Migrierbar) migrierbar=1 ;;
            Neu) neu=1; [[ "$name" == "$DB_NAME" ]] && haupt_neu=1 ;;
        esac
    done <<<"$DB_ZEILEN"

    case "$code" in
        0)  if (( migrierbar )); then DB_LAGE="migration"; ok "Datenbank: neue Migrationen – die Anwendung spielt sie beim Start ein (Sicherung vorher, Rückweg setzt sie zurück)"
            elif (( neu )); then DB_LAGE="neu"; ok "Datenbank: wird beim Start neu angelegt"
            else DB_LAGE="gleich"; ok "Datenbank passt zur neuen Version – keine Migration nötig"; fi
            (( db_neu )) && warn "-DbNeu ist hier NICHT nötig: Die Datenbank könnte übernommen werden." ;;
        3)  DB_LAGE="neuaufbau"
            if (( ! db_neu )); then
                achtung "Die neue Version passt nicht zur Datenbank (Schema geändert, keine Migration)." \
                        "Ausrollen geht nur mit -DbNeu – und dann gilt:" "" "${DBNEU_WARNUNG[@]}"
                pruef_fehler "Datenbank nur mit neu aufgebauter Datenbank ausrollbar –  .\\deploy\\server\\deploy.ps1 -DbNeu  (siehe Warnung oben)"
            fi ;;
        4)  pruef_fehler "Datenbank passt nicht und darf nicht neu aufgebaut werden (Zeilen oben: Fremd = jüngerer Stand als dieses Paket → Rückweg: deploy.ps1 -Zurueck; Geschuetzt = echte Daten, Anpassung per Skript)." ;;
        5)  pruef_fehler "Die neue Version erreicht PostgreSQL nicht (Hinweis oben; Verbindung in $UMGEBUNG, Container $DB_CONTAINER)." ;;
        *)  [[ -z "$DB_ZEILEN" ]] && tail -n 15 <<<"$ausgabe" | cut -c1-220 | sed 's/^/   /'
            deuten <<<"$ausgabe"
            pruef_fehler "Prüflauf der neuen Version scheitert (Exitcode $code) – so würde sie auch nicht starten." ;;
    esac
    (( db_neu )) && achtung "${DBNEU_WARNUNG[@]}"

    # Alte SQLite-Datenbank (Umstellung auf PostgreSQL)
    if [[ -n "$SQLITE_DATEI" && -f "$SQLITE_DATEI" ]]; then
        if (( haupt_neu )) && (( ! db_neu )); then
            case "$sqlite" in
                uebernehmen) sqlite_probe "$quelle" ;;
                ignorieren)  warn "Alte SQLite-Datenbank $SQLITE_DATEI wird NICHT übernommen (-SqliteIgnorieren); sie bleibt liegen, die Anwendung startet leer." ;;
                *)  pruef_fehler "Alte SQLite-Datenbank $SQLITE_DATEI gefunden, PostgreSQL ist noch leer – übernehmen mit  deploy.ps1 -SqliteUebernehmen  (empfohlen, mit Probe) oder bewusst leer starten mit  -SqliteIgnorieren" ;;
            esac
        elif [[ "$sqlite" == "uebernehmen" ]]; then
            pruef_fehler "-SqliteUebernehmen: Die Datenbank $DB_NAME ist nicht leer – übernommen wird nur in eine neue Datenbank (schon geschehen?)."
        else
            warn "Alte SQLite-Datei $SQLITE_DATEI liegt noch da (PostgreSQL ist schon in Gebrauch) – nach Prüfung nach $DATEN/beiseite/ verschieben."
        fi
    elif [[ "$sqlite" == "uebernehmen" ]]; then
        pruef_fehler "-SqliteUebernehmen: keine alte SQLite-Datei ${SQLITE_DATEI:-(SQLITE_ALT nicht gesetzt)}."
    fi
}

# Probe-Übernahme in eine Wegwerf-Datenbank: zeigt je Tabelle Zeilen und Vergleich; ändert die echte Datenbank nicht.
sqlite_probe() {
    local quelle="$1" ausgabe code tabellen abweichend
    info "Probe-Übernahme der alten SQLite-Daten in eine Wegwerf-Datenbank (Quelle bleibt unverändert) …"
    set +e; ausgabe=$(app_werkzeug "$quelle" --sqlite-uebernehmen "$SQLITE_DATEI" --probe 2>&1); code=$?; set -e
    tabellen=$(grep -c '^UEBERNAHME|' <<<"$ausgabe" || true)
    abweichend=$(grep '^UEBERNAHME|' <<<"$ausgabe" | grep -v '|gleich$' || true)
    grep -E '^(Quelle|Ziel|EF-Probe):' <<<"$ausgabe" | sed 's/^/   /' || true
    grep '^UEBERNAHME|' <<<"$ausgabe" | awk -F'|' '$4 > 0 { printf "   %-28s %8s Zeilen  %s\n", $2, $4, $5 }' | head -n 60 || true
    if (( code == 0 )) && grep -q '^UEBERNAHME_ERGEBNIS|ok|' <<<"$ausgabe" && [[ -z "$abweichend" ]]; then
        ok "Probe-Übernahme: $tabellen Tabellen, alle Zeilen und Prüfsummen gleich ($(grep '^UEBERNAHME_ERGEBNIS|' <<<"$ausgabe" | cut -d'|' -f3- | tr '|' ' '))"
        DB_UEBERNAHME=1
    else
        [[ -n "$abweichend" ]] && sed 's/^/   ABWEICHUNG: /' <<<"$abweichend"
        grep -E '^UEBERNAHME_ERGEBNIS\|fehler' <<<"$ausgabe" | cut -d'|' -f3- | sed 's/^/   /' || tail -n 15 <<<"$ausgabe" | sed 's/^/   /'
        pruef_fehler "Probe-Übernahme der SQLite-Daten fehlgeschlagen (Exitcode $code) – nichts übernommen, nichts geändert."
    fi
}

# Echte Übernahme (Schritt 4, alte Version angehalten): 0 = übernommen und geprüft
sqlite_uebernehmen() {
    local quelle="$1" ausgabe code
    info "Übernahme der alten SQLite-Daten nach PostgreSQL (eine Transaktion, je Tabelle Zeilen und Prüfsumme verglichen) …"
    set +e; ausgabe=$(app_werkzeug "$quelle" --sqlite-uebernehmen "$SQLITE_DATEI" 2>&1); code=$?; set -e
    if (( code == 0 )) && grep -q '^UEBERNAHME_ERGEBNIS|ok|' <<<"$ausgabe" && ! grep '^UEBERNAHME|' <<<"$ausgabe" | grep -qv '|gleich$'; then
        ok "übernommen: $(grep '^UEBERNAHME_ERGEBNIS|' <<<"$ausgabe" | cut -d'|' -f3- | tr '|' ' ')"
        grep '^UEBERNAHME|' <<<"$ausgabe" > "$SICHERUNGEN/uebernahme-$(date +%Y%m%d-%H%M%S).txt" || true
        return 0
    fi
    tail -n 20 <<<"$ausgabe" | cut -c1-220 | sed 's/^/   /'
    return 1
}

# Bekannte Meldungen im Startlog bzw. in der Ausgabe → Ursache und nächster Schritt (Text auf stdin)
deuten() {
    local log; log=$(cat)
    local -a hinweise=()
    [[ "$log" =~ (address\ already\ in\ use|Failed\ to\ bind) ]] && hinweise+=("Port $PORT belegt → sudo ss -ltnp 'sport = :$PORT' (läuft noch ein alter Prozess?)")
    [[ "$log" =~ (Failed\ to\ connect|Connection\ refused|No\ such\ host|Name\ or\ service\ not\ known) ]] && hinweise+=("PostgreSQL nicht erreichbar → sudo docker ps -a | grep $DB_CONTAINER; sudo docker start $DB_CONTAINER")
    [[ "$log" == *"password authentication failed"* ]] && hinweise+=("Anmeldung an PostgreSQL abgelehnt → ConnectionStrings__$VERBINDUNG in $UMGEBUNG prüfen (postgresql.sh setzt sie)")
    [[ "$log" == *"ConnectionStrings:$VERBINDUNG"* || "$log" == *"ConnectionStrings__$VERBINDUNG"* ]] && hinweise+=("Verbindung fehlt oder ist unvollständig → $UMGEBUNG (postgresql.sh)")
    [[ "$log" =~ (Access\ to\ the\ path|Permission\ denied|UnauthorizedAccess) ]] && hinweise+=("Rechte → Besitzer von $DATEN, $LOGS, $SICHERUNGEN muss $DIENST sein (einrichten.sh)")
    [[ "$log" =~ (OutOfMemory|oom-kill|Killed) ]] && hinweise+=("Speicher knapp → free -m; andere Dienste prüfen")
    [[ "$log" == *"status=203/EXEC"* ]] && hinweise+=("Programm nicht ausführbar → Archiv beschädigt oder falsche Plattform (linux-x64)")
    [[ "$log" =~ (You\ must\ install\ or\ update\ .NET|framework\ was\ not\ found) ]] && hinweise+=("ASP.NET-Core-Runtime fehlt → Modus self-contained oder docker, oder Runtime installieren (liebler-dotnet.sh)")
    [[ "$log" == *"Unhandled exception"* ]] && hinweise+=("Absturz beim Start → erste Ausnahme im Log lesen")
    if (( ${#hinweise[@]} )); then
        for h in "${hinweise[@]}"; do warn "Ursache: $h"; done
    else
        grep -E '(FTL|ERR)\]|Exception|exited' <<<"$log" | tail -n 8 | cut -c1-220 | sed 's/^/   /' || true
    fi
}

# ---------- Sichern und Rückweg ----------
DB_SICHERUNGEN=""     # „db=pfad db=pfad …“ aus der Sicherung vor dem Update
DB_VORHER=""          # Datenbanken vor dem Lauf
db_sichern() {        # vor dem Umschalten: alle Datenbanken (pg_dump) und die Schlüsselordner
    local zeilen
    DB_VORHER=$(db_liste | tr '\n' ' ')
    zeilen=$("/usr/local/sbin/$DIENST-sicherung.sh" vor-update) || fehler "Sicherung fehlgeschlagen – nichts geändert."
    DB_SICHERUNGEN=$(grep '^DUMP|' <<<"$zeilen" | awk -F'|' '{ printf "%s=%s ", $2, $3 }')
    grep -E '^(DUMP|DATEI)\|' <<<"$zeilen" | awk -F'|' '{ print "   " $3 }'
    ok "gesichert (vor dem Update): ${DB_VORHER:-keine Datenbanken}"
}

# db_zurueck <aenderung> <beiseite-name> <vorher-liste> <sicherungen>: Datenbanken auf den Stand vor dem Lauf
db_zurueck() {
    local aenderung="$1" beiseite="$2" vorher="$3" sicherungen="$4" d paar kennung
    kennung="verworfen_$(date +%Y%m%d_%H%M%S)"
    db_container_bereit || fehler "PostgreSQL-Container $DB_CONTAINER antwortet nicht – Datenbanken bitte von Hand aus $SICHERUNGEN zurücksichern."
    case "$aenderung" in
        keine) ok "Datenbanken unverändert – bleiben" ;;
        neu)   # -DbNeu: neu angelegte Datenbank beiseite, vorige zurück
            db_vorhanden "$DB_NAME" && db_umbenennen "$DB_NAME" "${DB_NAME}_$kennung"
            [[ -n "$beiseite" ]] && db_vorhanden "$beiseite" && db_umbenennen "$beiseite" "$DB_NAME"
            ok "Datenbank $DB_NAME: voriger Stand zurück (die neue liegt als ${DB_NAME}_$kennung bereit)" ;;
        migration|uebernahme)
            # neu entstandene Datenbanken verwerfen, vorhandene aus der Sicherung von vor dem Lauf
            for d in $(db_liste); do
                [[ " $vorher " == *" $d "* ]] || { db_umbenennen "$d" "${d}_$kennung"; ok "neu angelegte Datenbank $d beiseitegelegt (${d}_$kennung)"; }
            done
            for paar in $sicherungen; do
                d="${paar%%=*}"
                db_vorhanden "$d" && db_umbenennen "$d" "${d}_$kennung"
                db_aus_dump "$d" "${paar#*=}" && ok "Datenbank $d aus der Sicherung ${paar#*=} zurückgesetzt"
            done
            if [[ "$aenderung" == "uebernahme" && -n "$SQLITE_DATEI" ]]; then
                local alt; alt=$(ls -1t "$DATEN/beiseite/$(basename "$SQLITE_DATEI")".uebernommen-* 2>/dev/null | head -1 || true)
                [[ -n "$alt" ]] && mv "$alt" "$SQLITE_DATEI" && ok "alte SQLite-Datei wieder an ihrem Platz: $SQLITE_DATEI"
            fi ;;
    esac
}
