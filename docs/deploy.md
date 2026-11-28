# Bereitstellung auf dem Webserver

Ziel: **https://liebler.iui.hs-osnabrueck.de/profomat/** auf demselben Server wie VibeVHS, FraktionPerfekt und Podium. Ausgerollt
wird mit dem **gemeinsamen Deploy-Kit** in `deploy/server` (in allen vier Systemen gleich, Beschreibung:
[`deploy/server/README.md`](../deploy/server/README.md)); ProfOMat-eigen sind nur `konfiguration.env`, `umgebung.kit`,
`umgebung.beispiel` und `nginx/locations.conf`. Drei Betriebsarten (framework-abhängig mit der gemeinsamen ASP.NET-Core-Runtime 11,
self-contained, Docker), Datenbank PostgreSQL 17 im eigenen Container, nginx davor (TLS, ohne Passwort – die App meldet selbst an).

| | ProfOMat |
|---|---|
| Pfad | `/profomat` |
| Kestrel bzw. Container | `127.0.0.1:5082` (VibeVHS 5080, FraktionPerfekt 5081, Podium 5090) |
| Programm | `/opt/profomat` (vorige Version: `/opt/profomat.vorher`); Docker: `/opt/profomat-docker/compose.yml` |
| Datenbank | Container `profomat-db` (postgres:17, Volume `profomat-db`), nur `127.0.0.1:5443`; Datenbank `profomat` |
| Daten | `/var/lib/profomat` (`schluessel/` mit Systemsiegel-, Cookie-, TOTP- und Feldschlüsseln, SAML-Zertifikat) |
| Logs | `/var/log/profomat` (Serilog, 30 Tage) und `journalctl -u profomat` bzw. `journalctl CONTAINER_NAME=profomat` |
| Sicherungen | `/var/backups/profomat` (pg_dump mit Rücksicherungsprobe + Schlüssel, vor jedem Update und nachts) |
| Umgebung | `/etc/profomat/profomat.env` (Schalter, Geheimnisse, Datenbankverbindung; root:profomat 0640), `/etc/profomat/postgres.env` (0600) |
| nginx | `/etc/nginx/liebler.d/profomat.conf` → `/etc/nginx/snippets/profomat-*.conf` |
| Schema | EF-Core-Migrationen; Vorabprüfung `ProfOMat.Web --datenbankstand` |

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
  - Kein Passwort in `profomat.env`. **Testanmeldung und Demodaten sind verboten** – `einrichten.sh` und `ausrollen.sh` brechen ab,
    wenn `Anmeldung__TestanmeldungErlaubt=true` oder `Demodaten__Anlegen=true` gesetzt ist.
- **Studierende** brauchen kein Konto: Den Antrag auf mündliche Drittprüfung unterschreiben sie über einen persönlichen Einladungslink,
  den die Erstprüfenden im ProfOMat erzeugen und selbst per Mail verschicken (Zufallstoken, in der Datenbank nur als SHA-256,
  1–60 Tage gültig). Ein öffentliches Formular gibt es nicht mehr.
- **Mail:** ohne `Mail__Host` wird nur simuliert (Log „Mail simuliert“) – dann erreichen die Benachrichtigungen die Prüfenden und das
  Studiendekanat nicht. Für echten Betrieb den Mailserver der Hochschule eintragen.
- **Katalog:** noch Mock-Daten (erfundene Prüfungsordnungen/Prüfungen), echte Quelle folgt (Fahrplan Iteration 4).
- **Systemsiegel:** Der Schlüssel entsteht beim ersten Start in `/var/lib/profomat/schluessel/systemsiegel.pem`. Geht er verloren,
  sind bestehende Siegel nicht mehr prüfbar – deshalb sichert `profomat-sicherung.sh` den Ordner mit. `-DbNeu` lässt ihn unberührt.

## Umstellung SQLite → PostgreSQL (einmalig, 11/2026)

Bis 11/2026 lag die Datenbank als SQLite-Datei in `/var/lib/profomat/profomat.db`. Übernommen wird sie **einmal** und **prüfbar**:

```powershell
.\deploy\server\deploy.ps1 -Schritt einrichten         # Kit-Stand auf dem Server: Unit-Ergänzungen, neue Sicherung, Prüferkonto
.\deploy\server\deploy.ps1 -Schritt postgresql         # Container profomat-db, Passwort, Verbindung in profomat.env
.\deploy\server\deploy.ps1 -Pruefen -SqliteUebernehmen # Trockenlauf mit Probe-Übernahme in eine Wegwerf-Datenbank
.\deploy\server\deploy.ps1 -SqliteUebernehmen          # echte Übernahme und Start
```

Was dabei passiert (`ProfOMat.Web --sqlite-uebernehmen <datei> [--probe]`, `SqliteUebernahme.cs`):

1. Die Quelle wird nie verändert: Die App zieht eine konsistente Kopie (SQLite-Backup, auch bei WAL) und wertet die **Kopie** mit der
   bekannten Kette (Schemaversion 7 bis 20) auf den letzten SQLite-Stand auf.
2. Das Ziel muss leer sein. Es wird vollständig migriert und dann Tabelle für Tabelle per `COPY` befüllt – in **einer** Transaktion.
   Werte gehen roh über (verschlüsselte Felder bleiben Geheimtext, Zeitpunkte Millisekunden, Passwort-Hashes unverändert).
3. Vor dem Festschreiben vergleicht sie je Tabelle **Zeilenzahl und SHA-256 über alle Zeilen** aus Quelle und Ziel; weicht etwas ab,
   wird zurückgerollt. Danach lädt EF jede Entität einmal (Konverter, Pflichtfelder).
4. Das Kit macht das zweimal: in Schritt 1 als **Probe** (Wegwerf-Datenbank `profomat_uebernahmeprobe`, danach gelöscht, alte Version
   läuft weiter), in Schritt 4 echt bei angehaltener alter Version. Danach liegt die SQLite-Datei unter
   `/var/lib/profomat/beiseite/profomat.db.uebernommen-<Zeit>`, der Bericht unter `/var/backups/profomat/uebernahme-<Zeit>.txt`.
5. Scheitert der Start, setzt der automatische Rückweg die vorige (SQLite-)Version mit ihrer Datei wieder ein; die neue Datenbank
   bleibt als `profomat_verworfen_<Zeit>` im Container.

Wer die alten Daten nicht braucht: `deploy.ps1 -SqliteIgnorieren` (startet leer, die Datei bleibt liegen).

## Erstinstallation (neuer Server)

Voraussetzung: nginx mit TLS-Server-Block (`NGINX_SITE`), Docker, ASP.NET-Core-Runtime 11 (für `framework`).

```powershell
.\deploy\server\deploy.ps1 -Schritt einrichten    # Benutzer, Verzeichnisse, Unit, Sicherung, nginx
.\deploy\server\deploy.ps1 -Schritt postgresql    # Datenbank
.\deploy\server\deploy.ps1                        # testen, bauen, hochladen, einspielen
# sofort danach: https://liebler.iui.hs-osnabrueck.de/profomat/anmelden → Ersteinrichtung
```

Prüfen: `https://liebler.iui.hs-osnabrueck.de/profomat/anmelden` zeigt die Ersteinrichtung (bzw. danach die Anmeldung);
persönliche Links der Studierenden sind ohne Anmeldung erreichbar. Auf dem Server: `curl http://127.0.0.1:5082/profomat/gesund`
→ `ok`, Logs `journalctl -u profomat -f` und `/var/log/profomat/`.

## Update

```powershell
.\deploy\server\deploy.ps1 -Pruefen          # Trockenlauf: bauen, hochladen, auf dem Server alles prüfen – ändert nichts
.\deploy\server\deploy.ps1                   # Tests, Build, Upload, Einspielen mit Startbeobachtung
.\deploy\server\deploy.ps1 -Modus docker     # dasselbe als Container (Wechsel jederzeit, Daten bleiben im Datenbank-Container)
.\deploy\server\deploy.ps1 -Zurueck          # letzten Lauf zurücknehmen (Programm, Konfiguration, Datenbank)
.\deploy\server\deploy.ps1 -Neustart         # nach Änderungen an /etc/profomat/profomat.env
```

Ablauf, Prüfungen, Konfigurationsabgleich, Rückweg und Betriebsarten beschreibt [`deploy/server/README.md`](../deploy/server/README.md).
Neue EF-Migrationen spielt die Anwendung beim Start ein; der Prüflauf zeigt sie vorher als „Migrierbar“, die Sicherung davor setzt
ein Rückweg wieder ein. Eine Datenbank mit Migrationen, die das Paket nicht kennt (ältere Version), bricht vor jeder Änderung ab.

**Getestet:** `deploy/test/simulation.sh` spielt das Kit in einem Wegwerf-Container mit echtem PostgreSQL durch (56 Prüfungen:
Einrichtung, Datenbank, SQLite-Übernahme mit Probe, Absturz, Hänger, Migration, Rückweg mit Datenbank, fremder Stand, `-DbNeu`,
Konflikte in Unit, nginx und Port, verbotene Schalter, Prüfsumme, Sicherung mit Rücksicherungsprobe). Der Docker-Modus ist noch nicht
auf einem echten Server gelaufen.

## Sicherung und Rücksicherung

- Nachts (`profomat-sicherung.timer`) und vor jedem Update: `pg_dump` aller Datenbanken des Containers, jede **in eine
  Wegwerf-Datenbank zurückgespielt** (Rücksicherungsprobe), dazu der Schlüsselordner als `…-schluessel.tar.gz`. Aufbewahrung
  14 Tage (vor Updates 30 Tage). Status: `/var/backups/profomat/letzte-sicherung.txt`.
- Rücksicherung von Hand: Dienst anhalten, dann
  `sudo docker exec -i profomat-db pg_restore -U profomat -d profomat --clean --if-exists < /var/backups/profomat/<…>-profomat.dump`,
  bei Bedarf Schlüssel mit `sudo tar -xzf <…-schluessel.tar.gz> -C /var/lib/profomat`, Dienst starten.
- Letzten Lauf zurück (Programm, Konfiguration, ggf. Datenbank): `.\deploy\server\deploy.ps1 -Zurueck`.
- Alles aus (Daten bleiben): `.\deploy\server\deploy.ps1 -Schritt einrichten-zurueck`.

## Betriebsdetails

- **Pfad-Basis:** `PathBase=/profomat` (Unit). Die Anwendung setzt `<base href>`, alle Links sind relativ. Anfragen außerhalb der
  Pfad-Basis beantwortet sie mit einer leeren 404 (sonst setzte z. B. `/favicon.ico` über die 404-Seite ein zweites
  Antiforgery-Cookie mit `Path=/`, und Formulare scheiterten mit 400).
- **Proxy:** `ReverseProxy__Aktiv=true`, Weiterleitungs-Header nur von Loopback und nur für den eigenen Host;
  `ProfOMat__OeffentlicheAdresse` (Links in Mails und PDFs) setzt die Unit aus Host und Pfad.
- **Schriften:** Die PDFs betten Liberation Sans/Mono ein – auf dem Server müssen keine Schriften installiert sein.
- **Zeitzone:** gerechnet wird mit `ProfOMat:Zeitzone` (Europe/Berlin), gespeichert in UTC; die Serveruhr muss stimmen
  (das Kit warnt sonst).
- **Härtung:** eigener Dienstbenutzer ohne Shell, `ProtectSystem=strict`, Schreibrechte nur auf `/var/lib/profomat` und
  `/var/log/profomat`, `UMask=0027`, Schlüsselordner 0700.

## Stufe 2: Feldschlüssel, Sicherung, Prüfungen

**Feldschlüssel als systemd-Credential** (statt `feldschluessel.txt` im Schlüsselordner, der mitgesichert wird):

```bash
sudo install -d -m 0700 /etc/credstore.encrypted
sudo cp /var/lib/profomat/schluessel/feldschluessel.txt /root/feldschluessel.txt      # vorhandenen Schlüssel übernehmen!
sudo systemd-creds encrypt --name=profomat-feldschluessel /root/feldschluessel.txt /etc/credstore.encrypted/profomat-feldschluessel
sudo shred -u /root/feldschluessel.txt
# Drop-in: LoadCredentialEncrypted=profomat-feldschluessel:/etc/credstore.encrypted/profomat-feldschluessel
```

Danach die Datei im Schlüsselordner entfernen und eine Zweitablage im Passwortsafe anlegen. Schlüsselwechsel: neue Zeile
`2:<Base64 von 32 Byte>` anhängen (die alte bleibt), Dienst neu starten – der Bestand wird beim Start neu verschlüsselt.

**Verschlüsselte Sicherung:** `age-keygen -o profomat-sicherung.key` offline erzeugen, nur den öffentlichen Schlüssel auf den Server:
`/etc/profomat/sicherung.env` mit `AGE_EMPFAENGER="age1…"` und optional `KOPIE_ZIEL="rsync:benutzer@host:/pfad"`; auf dem Server
`apt install age`. Die letzte Sicherung steht in `/var/backups/profomat/letzte-sicherung.txt` (auch im Sicherheitsbericht).

**Pakete:** `deploy.ps1` legt neben `profomat.tar.gz` die Prüfsumme (`.sha256`) und die Stückliste (`profomat-sbom.cdx.json`, auch im
Paket als `sbom.cdx.json`) ab; das CycloneDX-Werkzeug kommt aus `dotnet-tools.json` (`dotnet tool restore`).

**Prüferkonto:** `sudo /usr/local/sbin/profomat-pruefer-konto.sh anlegen 14 pruefer.pub` – entfernen mit `… entfernen`.

**Schalter** (in `/etc/profomat/profomat.env`): `Sicherheit__VerwaltungNetze__0=131.173.0.0/16`, `Betrieb__Metriken__Netze__0=…`,
`Betrieb__Otlp__Endpunkt=…`, `Betrieb__Logformat=json`, `Betrieb__SecurityTxt__Kontakt=mailto:…`.
