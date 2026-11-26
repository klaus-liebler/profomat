# Bereitstellung auf dem Webserver

Ziel: **https://liebler.iui.hs-osnabrueck.de/profomat/** auf demselben Server wie VibeVHS, FraktionPerfekt und Podium. Das Kit in
`deploy/server` folgt dem FraktionPerfekt-Kit (`C:\repos\fraktionperfekt\deploy`): framework-abhängiger Linux-Build (gemeinsame ASP.NET-Core-Runtime aus VibeVHS-Schritt 09; `deploy.ps1 -MitRuntime` = self-contained), Programm in `/opt`,
Daten in `/var/lib`, systemd-Dienst nur auf `127.0.0.1`, nginx davor (TLS, ohne Passwort – die App meldet selbst an, wie Podium), jeder root-Schritt als eigenes Skript
mit Sicherung und Rückweg. Die nginx-Einbindung nutzt das gemeinsame Schema `schritte/liebler-nginx.sh` (identisch in allen Kits).

| | ProfOMat |
|---|---|
| Pfad | `/profomat` |
| Kestrel | `127.0.0.1:5082` (VibeVHS 5080, FraktionPerfekt 5081, Podium 5090) |
| Programm | `/opt/profomat` (vorige Version: `/opt/profomat.vorher`) |
| Daten | `/var/lib/profomat` (`profomat.db`, `schluessel/` mit Systemsiegel- und Cookie-Schlüsseln) |
| Logs | `/var/log/profomat` (Serilog, 30 Tage) und `journalctl -u profomat` |
| Sicherungen | `/var/backups/profomat` (Datenbank + Schlüssel, vor jedem Update und täglich 02:00 Uhr) |
| Umgebung | `/etc/profomat/profomat.env` (Schalter und Geheimnisse, root:profomat 0640) |
| nginx | `/etc/nginx/liebler.d/profomat.conf` → `/etc/nginx/snippets/profomat-*.conf` |
| Schema | EnsureCreated + `Schemaversion`, Aufwertungen ab Version 7 im Code (siehe „Update“) |

## Sicherheitsstand – bitte zuerst lesen

- **Anmeldung wie Podium, ohne nginx-Passwort.** Die Seite ist öffentlich erreichbar; die App schützt sich selbst:
  - **Ersteinrichtung:** Solange die Datenbank kein Konto enthält, zeigt `/profomat/anmelden` die Ersteinrichtung – dort einmalig
    Kennung, Name, Mail und Passwort des ersten Kontos (Systemverwaltung, auf Wunsch zugleich Prüfende) festlegen. Weil bis dahin jede
    Person mit Zugang zur Seite das Konto anlegen könnte: **direkt nach dem ersten Ausrollen** erledigen (`ausrollen.sh` erinnert daran).
  - **Kolleginnen und Kollegen** lädt die Systemverwaltung unter *Verwaltung → Konten* per Einladungslink ein (Rollen fest, 1–30 Tage,
    einmal verwendbar). Passwort vergessen → **Passwort-Link** beim Konto. Die Tokens stehen hinter `#` und erreichen nie ein
    Server-Log; in der Datenbank liegt nur ihr SHA-256.
  - Passwörter: PBKDF2-HMAC-SHA512 (210 000 Iterationen), mindestens 12 Zeichen; nach 5 Fehlversuchen je IP und Kennung wachsende
    Wartezeit bis 5 Minuten. Sperren, Rollenänderung oder neues Passwort beenden bestehende Sitzungen.
  - Kein Passwort in `profomat.env`. **Testanmeldung und Demodaten sind verboten** – `01-einrichten.sh` und `ausrollen.sh` brechen ab,
    wenn `Anmeldung__TestanmeldungErlaubt=true` oder `Demodaten__Anlegen=true` gesetzt ist.
- **Studierende** brauchen kein Konto: Den Antrag auf mündliche Drittprüfung unterschreiben sie über einen persönlichen Einladungslink,
  den die Erstprüfenden im ProfOMat erzeugen und selbst per Mail verschicken (Zufallstoken, in der Datenbank nur als SHA-256,
  1–60 Tage gültig). Ein öffentliches Formular gibt es nicht mehr.
- **Mail:** ohne `Mail__Host` wird nur simuliert (Log „Mail simuliert“) – dann erreichen die Benachrichtigungen die Prüfenden und das
  Studiendekanat nicht. Für echten Betrieb den Mailserver der Hochschule eintragen.
- **Katalog:** noch Mock-Daten (erfundene Prüfungsordnungen/Prüfungen), echte Quelle folgt (Fahrplan Iteration 4).
- **Systemsiegel:** Der Schlüssel entsteht beim ersten Start in `/var/lib/profomat/schluessel/systemsiegel.pem`. Geht er verloren,
  sind bestehende Siegel nicht mehr prüfbar – deshalb sichert `profomat-sicherung.sh` den Ordner mit. `-DbNeu` lässt ihn unberührt.

## Vor dem ersten Lauf entscheiden bzw. eintragen

1. `deploy/server/konfiguration.env`: Host, Pfad (`/profomat`), Port (`5082`, Schritt 01 prüft, ob er frei ist).
2. In `/etc/profomat/profomat.env` (Schritt 01 legt die Datei aus `profomat.env.beispiel` an): optional Mailserver. Sonst nichts.

## Erstinstallation

Voraussetzung: Das VibeVHS-Kit ist eingerichtet (nginx mit `liebler.conf`, Zertifikat, Firewall, Pakete `sqlite3`, `apache2-utils`).

```powershell
.\deploy\server\deploy.ps1 -NurKit                                          # Kit nach /tmp/profomat-kit
ssh -t srvadmin@liebler.iui.hs-osnabrueck.de "sudo bash /tmp/profomat-kit/schritte/01-einrichten.sh"   # Benutzer, Verzeichnisse, Unit, nginx
.\deploy\server\deploy.ps1                                                  # testen, bauen, hochladen, einspielen
# sofort danach: https://liebler.iui.hs-osnabrueck.de/profomat/anmelden → Ersteinrichtung
```

Prüfen: `https://liebler.iui.hs-osnabrueck.de/profomat/anmelden` zeigt die Ersteinrichtung (bzw. danach die Anmeldung);
`…/profomat/drittpruefung/antrag` ist ohne Anmeldung erreichbar. Auf dem Server:
`curl http://127.0.0.1:5082/profomat/gesund` → `ok`, Logs `journalctl -u profomat -f` und `/var/log/profomat/`.

## Update

```powershell
.\deploy\server\deploy.ps1 -Pruefen          # Trockenlauf: bauen, hochladen, auf dem Server alles prüfen – ändert nichts
.\deploy\server\deploy.ps1                   # Tests, Build, Upload, Einspielen mit Startbeobachtung
.\deploy\server\deploy.ps1 -Modus docker     # dasselbe als Container
.\deploy\server\deploy.ps1 -DbNeu            # nur wenn die Datenbank nicht migrierbar ist (große rote Warnung)
.\deploy\server\deploy.ps1 -Zurueck          # letzten Lauf zurücknehmen
```

`deploy.ps1` führt zuerst alle Tests aus (`-OhneTests` nur im Notfall), baut in `deploy/ausgabe` (eigener Artefakt-Ordner; die
lokal laufende Instanz bleibt unberührt), legt `schemainfo.txt` ins Paket (`ProfOMat.Web --schemainfo`: erwartete Schemaversion und
älteste aufwertbare Version) und prüft, dass weder Datenbanken noch Schlüssel, `data/` oder `logs/` im Archiv liegen. Übertragen wird
**nur** die Ausgabe von `dotnet publish`. Auf dem Server arbeitet `schritte/ausrollen.sh` in sechs Abschnitten:

| Abschnitt | Was passiert | Bei Problemen |
|---|---|---|
| 1 Prüfen | System (Ubuntu/Debian, systemd), Programme je Modus (`curl`, `sqlite3`, `nginx` …; bei Docker `docker compose` und das Basisabbild), Uhr, Grundgerüst aus Schritt 01, Paket passt zum Modus (Runtime vorhanden bzw. self-contained), Platz, Port, systemd-Unit, nginx, Datenbank | **Ändert nichts.** Alle Probleme werden gesammelt mit Fundstelle gemeldet, dann Abbruch. `-Pruefen` endet hier. |
| 2 Sichern | Datenbank und Schlüssel (`vor-update-<Zeit>`), Stand für den Rückweg (`/root/profomat-sicherung/letzter-lauf.env`) | Abbruch ohne Änderung |
| 3 Ergänzen | Fehlende Einträge in systemd und nginx (siehe unten) | ab hier: automatischer Rückweg |
| 4 Umschalten | alte Version anhalten, neue an ihre Stelle (`/opt/profomat.vorher` bzw. voriges Abbild bleibt), starten | automatischer Rückweg |
| 5 Beobachten | rückwärts laufender Balken mit Countdown (`WARTEZEIT`, Standard 120 s, `-Wartezeit`) | **sofortiger** Rückweg, wenn der Dienst stirbt oder neu startet oder im Startlog eine Zeile auf `FEHLER_MUSTER` passt (z. B. „Datenbankschema veraltet“, „Unhandled exception.“, „FTL“); sonst Rückweg bei Ablauf ohne Antwort von `/gesund` |
| 6 Abschluss | Erfolg erst, wenn `/gesund` antwortet und der Dienst danach 10 s stabil bleibt | – |

**Konfiguration: bestehende Einträge werden nie verändert.** Fehlt in der systemd-Unit ein Eintrag der Vorlage (z. B. eine
Umgebungsvariable), schreibt das Skript ihn in ein eigenes Drop-in `/etc/systemd/system/profomat.service.d/50-kit.conf`; die Unit
selbst bleibt, wie sie ist. Steht ein Eintrag **anders** da als erwartet (z. B. `ASPNETCORE_URLS=http://0.0.0.0:5082`), bricht die
Prüfung mit Datei und Zeile ab. Ausnahme: Härtungs- und Neustart-Schalter (`ProtectSystem`, `RestartSec` …) darf der Betrieb bewusst
anders setzen, das gibt nur eine Warnung. Bei nginx schreibt das Kit nur seine eigenen Dateien (`snippets/profomat-*.conf`,
`liebler.d/profomat.conf`, erkannt an der ersten Zeile); eine gleichnamige fremde Datei, ein anderer Block für `/profomat` oder
`127.0.0.1:5082`, eine schon vorher fehlerhafte Konfiguration (`nginx -t`) oder ein fehlendes `map $http_upgrade` führen zum Abbruch.

**Datenbank und Migration.** Vor jeder Änderung vergleicht das Skript die Schemaversion der Datei mit `schemainfo.txt`:

| Lage | Folge |
|---|---|
| gleich | ausrollen |
| älter, aber ≥ `migrierbar_ab` (Aufwertungen 7 → 18 stehen im Code, `DatenbankInitialisierung`) | ausrollen; die App wertet beim Start auf. Der Rückweg setzt die Sicherung von vor dem Lauf wieder ein. |
| älter als `migrierbar_ab`, neuer als das Paket oder unlesbar | **Abbruch vor jeder Änderung** mit großer roter Warnung und dem Hinweis auf `-DbNeu` |

**`-DbNeu`** legt die Datei samt `-wal`/`-shm` nach `/var/lib/profomat/beiseite/` (nicht löschen) und lässt die Anwendung eine neue
anlegen. `deploy.ps1` zeigt vorher in großen roten Buchstaben, dass danach in der Anwendung **alle Daten weg** sind (Ersteinrichtung
nötig), und verlangt die Eingabe „ja“. Zurück kommen die Daten nur mit `-Zurueck`.

**Rückweg.** Automatisch bei jedem Abbruch ab Abschnitt 3 und bei gescheitertem Start; von Hand mit `deploy.ps1 -Zurueck`
(bzw. `sudo bash /tmp/profomat-kit/schritte/ausrollen.sh --zurueck`). Er setzt Programm, Konfiguration (aus der Sicherung des
Laufs) und – nach `-DbNeu` oder einer Migration – die Datenbank auf den Stand vor dem Lauf und startet die vorige Variante. Hat
seither jemand gearbeitet, warnt er rot und fragt nach; die heutige Datei bleibt unter `beiseite/…verworfen-<Zeit>`.

**Drei Arten (`-Modus`, Standard `MODUS` in `konfiguration.env`):**

| Modus | Paket | Auf dem Server nötig | Läuft als |
|---|---|---|---|
| `framework` | nur die App (~20 MB) | ASP.NET-Core-Runtime 11 (VibeVHS-Schritt 09, Updates per Timer) | systemd-Dienst `profomat` |
| `self-contained` | App mit Runtime (~70 MB, früher `-MitRuntime`) | nichts von .NET | systemd-Dienst `profomat` |
| `docker` | wie `framework`; das Abbild baut der Server (`docker/Dockerfile`, Basis `mcr.microsoft.com/dotnet/aspnet:11.0`) | Docker mit Compose-Plugin, Zugriff auf mcr.microsoft.com | Container `profomat` (`/opt/profomat-docker/compose.yml`), nur `127.0.0.1:5082`, schreibgeschützt, ohne Capabilities, Logs ins Journal |

ProfOMat nutzt SQLite; die Datei liegt auch im Docker-Modus im Datenordner des Hosts, ein eigener Datenbank-Container entfällt
(SchoolCraft bringt dafür `postgres:17` mit). Ein Wechsel des Modus ist ein normales Ausrollen: Das Skript hält die alte Variante
an und startet sie beim Rückweg wieder.

**Getestet:** `deploy/test/simulation.sh` spielt in einem Wegwerf-Container 14 Abläufe durch (Erstinstallation, Absturz, Hänger,
Absturz in der Stabilitätsprüfung, Migration, Rückweg mit Datenbank, `-DbNeu`, Konflikte in Unit, nginx und Port, verbotene
Testanmeldung) – systemd und nginx sind dort nachgebildet. Der Docker-Modus ist noch nicht auf einem echten Server gelaufen.

## Sicherung und Rücksicherung

- Täglich 02:00 Uhr (`profomat-sicherung.timer`) und vor jedem Update: `sqlite3 .backup` (konsistent im laufenden Betrieb),
  Plausibilitätsprüfung über die Tabelle `Schemainfo`, dazu der Schlüsselordner als `…-schluessel.tar.gz` (0600). Aufbewahrung
  14 Tage (vor Updates 30 Tage).
- Rücksicherung: `sudo systemctl stop profomat` → Sicherung als `/var/lib/profomat/profomat.db` einsetzen (vorher `-wal`/`-shm`
  beiseitelegen, Besitzer `profomat`, Rechte 0640), bei Bedarf Schlüssel mit
  `sudo tar -xzf <…-schluessel.tar.gz> -C /var/lib/profomat` → `sudo systemctl start profomat`.
- Letzten Lauf zurück (Programm, Konfiguration, ggf. Datenbank): `.\deploy\server\deploy.ps1 -Zurueck`.
- Alles aus (Daten bleiben): `sudo bash /tmp/profomat-kit/schritte/01-zurueck.sh`.

## Betriebsdetails

- **Pfad-Basis:** `PathBase=/profomat` (Unit). Die Anwendung setzt `<base href>`, alle Links sind relativ. Anfragen außerhalb der
  Pfad-Basis beantwortet sie mit einer leeren 404 (sonst setzte z. B. `/favicon.ico` über die 404-Seite ein zweites
  Antiforgery-Cookie mit `Path=/`, und Formulare scheiterten mit 400).
- **Proxy:** `ReverseProxy__Aktiv=true`, Weiterleitungs-Header nur von Loopback und nur für den eigenen Host;
  `ProfOMat__OeffentlicheAdresse` (Links in Mails und PDFs) setzt die Unit aus Host und Pfad.
- **Schriften:** Die PDFs betten Liberation Sans/Mono ein – auf dem Server müssen keine Schriften installiert sein.
- **Zeitzone:** gerechnet wird mit `ProfOMat:Zeitzone` (Europe/Berlin), gespeichert in UTC; die Serveruhr muss stimmen
  (Schritt 01 warnt sonst).
- **Härtung:** eigener Dienstbenutzer ohne Shell, `ProtectSystem=strict`, Schreibrechte nur auf `/var/lib/profomat` und
  `/var/log/profomat`, `UMask=0027`, Schlüsselordner 0700.
