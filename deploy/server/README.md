# Deploy-Kit

Gemeinsames Werkzeug zum Ausrollen der vier Systeme **ProfOMat, FraktionPerfekt, VibeVHS und SchulPerfekt** auf einen Linux-Server
(Ubuntu/Debian mit systemd; andere Distributionen laufen mit Warnung, solange die Programme da sind). Die Kit-Dateien sind in allen
vier Repositories **byte-gleich**; was ein System ausmacht, steht in vier eigenen Dateien. Ausgangsstand ist ProfOMat (11/2026).

**Grundsätze:** erst prüfen, dann ändern (alle Probleme gesammelt, Abbruch vor der ersten Änderung) · Bestehendes nie überschreiben,
nur ergänzen · Sicherung vor jeder Änderung · Start beobachten und bei Fehlern sofort zurück · Rückweg jederzeit, auch von Hand ·
übertragen wird nur das Programm, nie Daten oder Schlüssel.

## Betriebsarten

| Art (`MODUS`) | Auf dem Server nötig | Paket |
|---|---|---|
| `framework` (Standard) | ASP.NET-Core-Runtime 11 (gemeinsam für alle Anwendungen, `schritte/liebler-dotnet.sh`) | nur die App, ~20 MB |
| `self-contained` | nichts von .NET | App mit eigener Runtime, ~70 MB |
| `docker` | Docker mit Compose | Abbild wird auf dem Server gebaut (Basis `mcr.microsoft.com/dotnet/aspnet:11.0`) |

Die **Datenbank** (PostgreSQL 17) läuft in jeder Betriebsart als eigener Container `<dienst>-db` (Volume `<dienst>-db`, nur
`127.0.0.1:<DB_PORT>`, im Modus docker zusätzlich im Netz `<dienst>-netz`). Ein Wechsel der Betriebsart (`deploy.ps1 -Modus docker`)
lässt die Daten, wo sie sind. Docker braucht deshalb jeder Server.

## Dateien

| Datei | Kit oder System | Inhalt |
|---|---|---|
| `konfiguration.env` | **System** | Name, Dienst, Programm, Server, Pfad, Port, Betriebsart, Datenbank, Fehlermuster, verbotene Schalter |
| `umgebung.kit` | **System** | feste Umgebung der Anwendung (`SCHLÜSSEL=WERT`, Vorsilbe `nativ:`/`docker:`), landet in Unit bzw. compose.yml |
| `umgebung.beispiel` | **System** | Vorlage für `/etc/<dienst>/<dienst>.env` (Geheimnisse und Schalter, nur auf dem Server) |
| `nginx/locations.conf` | **System** | `location`-Blöcke (öffentliche Teile, Ausnahmen) |
| `deploy.ps1` | Kit | Windows-Seite: Tests, Build, Prüfsumme, SBOM, Hochladen, Aufruf auf dem Server |
| `kit-abgleich.ps1` | Kit | Kit-Dateien zwischen den Repositories vergleichen bzw. übernehmen |
| `schritte/einrichten.sh` (`-zurueck`) | Kit | Dienstbenutzer, Verzeichnisse, Umgebungsdatei, Unit, Sicherung, nginx-Einbindung |
| `schritte/postgresql.sh` (`-zurueck`) | Kit | Datenbank-Container, Passwort, Verbindung, pg_dump 17 auf dem Host |
| `schritte/ausrollen.sh` | Kit | Ausrollen in sechs Schritten, Rückweg (`--zurueck`), Neustart (`--neustart`) |
| `schritte/gemeinsam.sh`, `pruefen.sh`, `konfig-abgleich.sh`, `datenbank.sh`, `beobachten.sh` | Kit | Bausteine (per `source`) |
| `schritte/liebler-nginx.sh`, `liebler-dotnet.sh` | Kit | nginx-Schema mit `liebler.d/` (eine Datei je Anwendung) und gemeinsame .NET-Runtime |
| `systemd/`, `docker/`, `nginx/proxy.conf`, `werkzeuge/` | Kit | Vorlagen mit Platzhaltern `@DIENST@`, `@PORT@` … |
| `../test/` | Kit | Simulation des ganzen Kits ohne Server |

Weitere Dateien in `schritte/` (z. B. die nummerierten Server-Schritte im VibeVHS-Repo) gehören zum jeweiligen System.

## Vertrag mit der Anwendung

Damit das Kit eine Anwendung ausrollen kann, muss sie Folgendes können (alle vier tun das):

- **Konfiguration aus der Umgebung**: `ASPNETCORE_URLS`, `PathBase` bzw. eigene Schlüssel aus `umgebung.kit`,
  `ConnectionStrings__<VERBINDUNG>` (PostgreSQL), Geheimnisse aus `/etc/<dienst>/<dienst>.env`.
- **Lebenszeichen** unter `<PFAD><GESUND_PFAD>` (Standard `/gesund`): 200, sobald die Datenbank erreichbar ist.
- **`<APP> --datenbankstand`** – liest nur und meldet je Datenbank eine Zeile
  `DATENBANKSTAND|rolle|datenbank|zustand|eingespielt|offen|hinweis` mit `zustand` = `Neu`, `Aktuell`, `Migrierbar`, `Neuaufbau`,
  `Fremd`, `Geschuetzt` oder `NichtLesbar`. Exitcode 0 = startbereit (auch neu oder mit Migration), 3 = nur mit neu aufgebauter
  Datenbank (`-DbNeu`), 4 = nicht startbar und `-DbNeu` hilft nicht bzw. ist verboten, 5 = keine Verbindung.
- **Migrationen beim Start**: Ein Start legt eine fehlende Datenbank an und spielt offene Migrationen ein.
- optional **`<APP> --sqlite-uebernehmen <datei> [--probe]`** für die einmalige Übernahme einer alten SQLite-Datenbank (`SQLITE_ALT`):
  je Tabelle `UEBERNAHME|tabelle|zeilen-quelle|zeilen-ziel|gleich`, zuletzt `UEBERNAHME_ERGEBNIS|ok|…` bzw. `|fehler|…`.
  `--probe` arbeitet in einer Wegwerf-Datenbank `<db>_uebernahmeprobe` und löscht sie wieder.

Beide Werkzeug-Aufrufe startet das Kit mit der **neuen** Version, als Dienstbenutzer und mit der Umgebung des Dienstes (nativ über
`systemd-run`, im Modus docker über `docker run` mit dem neuen Abbild) – während die alte Version weiterläuft.

## Erstinstallation

Voraussetzung auf dem Server: nginx mit einem TLS-Server-Block (`NGINX_SITE`, mit `map $http_upgrade $connection_upgrade` für
WebSockets), Docker, für `framework` die ASP.NET-Core-Runtime 11. Andere Reverse Proxys gehen ebenso (Vorgaben: TLS am Proxy,
WebSockets durchreichen, `X-Forwarded-*` setzen); dann die `location`-Blöcke aus `nginx/locations.conf` sinngemäß übertragen.

```powershell
.\deploy\server\deploy.ps1 -Schritt einrichten    # Benutzer, Verzeichnisse, Unit, Sicherung, nginx
.\deploy\server\deploy.ps1 -Schritt postgresql    # Datenbank-Container und Verbindung
.\deploy\server\deploy.ps1 -Pruefen               # Trockenlauf: alles prüfen, nichts ändern
.\deploy\server\deploy.ps1                        # ausrollen
```

Danach Geheimnisse und Schalter in `/etc/<dienst>/<dienst>.env` eintragen (Vorlage `umgebung.beispiel`) und mit
`deploy.ps1 -Neustart` übernehmen. Gibt es noch kein Konto, erinnert das Kit an die Ersteinrichtung (`ERSTEINRICHTUNG_SQL`).

## Ausrollen

`deploy.ps1` testet (`LOESUNG`), baut (`dotnet publish -r linux-x64`), legt SBOM und SHA-256 daneben, lädt Kit und Archiv nach
`/tmp` und ruft `schritte/ausrollen.sh` mit sudo auf:

1. **Prüfen** (ändert nichts): System, Programme, Docker, Port (mit Prozessname), Platz (auch für die Sicherung), Prüfsumme,
   Paketinhalt (keine Daten/Schlüssel), Runtime bzw. Basisabbild, systemd-Unit (fehlt → anlegen; fehlende Einträge → Drop-in;
   Widerspruch → Abbruch), nginx (fremde Dateien mit unserem Pfad oder Port → Abbruch), verbotene Schalter (`VERBOTEN_MUSTER`),
   **Datenbankstand der neuen Version** und ggf. **Probe-Übernahme** der alten SQLite-Daten.
2. **Sichern**: jede Datenbank per `pg_dump` mit Rücksicherungsprobe, die Schlüsselordner (`SICHERN_ORDNER`), Stand für den Rückweg.
3. **Ergänzen**: Unit bzw. Drop-in, nginx-Snippets und `liebler.d/<dienst>.conf` (mit `nginx -t`, sonst sofort zurück).
4. **Umschalten**: alte Version anhalten, ggf. `-DbNeu` (Hauptdatenbank → `<db>_alt_<Zeit>`) bzw. echte SQLite-Übernahme
   (eine Transaktion, je Tabelle Zeilen und Prüfsumme verglichen; die SQLite-Datei wandert nach `/var/lib/<dienst>/beiseite/`),
   neue Version an ihre Stelle (`/opt/<dienst>`, vorige bleibt als `.vorher`; im Modus docker das neue Abbild), starten.
5. **Beobachten**: Countdown (`WARTEZEIT`) bis zum automatischen Rückweg; sofort zurück bei Absturz, Neustart durch systemd/Docker
   oder einer Zeile auf `FEHLER_MUSTER` im Startlog. Erfolg = `/gesund` antwortet und bleibt `STABIL` Sekunden ohne Fehler.
6. **Abschluss**: neuer Datenbankstand, Fehlerzeilen seit dem Start, alte Abbilder aufräumen.

| Aufruf | Wirkung |
|---|---|
| `deploy.ps1 -Pruefen` | nur Schritt 1 |
| `deploy.ps1 -Modus docker` / `self-contained` | andere Betriebsart (Wechsel jederzeit, Rückweg inklusive) |
| `deploy.ps1 -SqliteUebernehmen` | einmalige Übernahme der alten SQLite-Daten (Probe in Schritt 1, echte Übernahme in Schritt 4) |
| `deploy.ps1 -SqliteIgnorieren` | alte SQLite-Datei bewusst liegen lassen, leer starten |
| `deploy.ps1 -DbNeu` | Hauptdatenbank beiseitelegen und neu aufbauen lassen (große rote Warnung, „ja“ eintippen) |
| `deploy.ps1 -Zurueck` | letzten Lauf zurücknehmen: Programm, Konfiguration, Datenbanken (der aktuelle Stand bleibt als `…_verworfen_<Zeit>`) |
| `deploy.ps1 -Neustart` | laufende Version neu starten (nach Änderungen an der Umgebungsdatei) |
| `deploy.ps1 -Schritt <name>` | einen Schritt aus `schritte/` auf dem Server ausführen |
| `deploy.ps1 -NurBauen` / `-NurKit` | nur bauen bzw. nur das Kit hochladen |

## Sicherung

`/usr/local/sbin/<dienst>-sicherung.sh` (Timer nachts, dazu vor jedem Ausrollen): jede Datenbank des Containers per `pg_dump`
(ohne beiseitegelegte und Probedatenbanken), jedes Mal **in eine Wegwerf-Datenbank zurückgespielt** (Rücksicherungsprobe), die
Schlüsselordner als tar.gz, Prüfsummen, Statusdatei `letzte-sicherung.txt`. Optional (`/etc/<dienst>/sicherung.env`) mit `age`
verschlüsselt und außer Haus kopiert (`rsync:` bzw. `rclone:`). Aufbewahrung: nachts 14, vor Updates 30 Tage.

Rücksichern von Hand: `docker exec -i <dienst>-db pg_restore -U <dienst> -d <db> --clean --if-exists < <datei>.dump`.

## Übernahme in ein System

1. Kit-Dateien übernehmen: `.\deploy\server\kit-abgleich.ps1 -Quelle C:\repos\profomat -Uebernehmen` (im Ziel-Repo).
2. `konfiguration.env`, `umgebung.kit`, `umgebung.beispiel`, `nginx/locations.conf` für das System anlegen (die erste Zeile von
   `locations.conf` beibehalten, wenn es auf dem Server schon ein Snippet gibt – daran erkennt das Kit seine eigene Datei).
3. Vertrag erfüllen (`--datenbankstand`, `/gesund`, Migrationen beim Start).
4. Simulation laufen lassen, dann `deploy.ps1 -Pruefen` gegen den Server.

Änderungen am Kit immer in einem Repository machen, Simulation laufen lassen und mit `kit-abgleich.ps1` in die anderen drei bringen;
`kit-abgleich.ps1` ohne Schalter zeigt, welche Kit-Dateien abweichen.

## Simulation

Spielt das ganze Kit ohne Server durch – Einrichtung, Datenbank, SQLite-Übernahme (Probe und echt), Migration, Absturz und Hänger
mit Rückweg, fremder Datenbankstand, `-DbNeu`, Rückweg von Hand, Konfigurationskonflikte, Sicherung. systemd, journalctl, nginx und
docker sind nachgebildet, die Datenbank ist ein echtes PostgreSQL im Wegwerf-Container:

```powershell
docker run --rm -v "${PWD}/deploy:/deploy" ubuntu:24.04 bash /deploy/test/simulation.sh
```
