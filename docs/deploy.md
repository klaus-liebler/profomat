# Bereitstellung auf dem Webserver (Testsystem)

Ziel: **https://liebler.iui.hs-osnabrueck.de/profomat/** auf demselben Server wie VibeVHS, FraktionPerfekt und Podium. Das Kit in
`deploy/server` folgt dem FraktionPerfekt-Kit (`C:\repos\fraktionperfekt\deploy`): self-contained Linux-Build, Programm in `/opt`,
Daten in `/var/lib`, systemd-Dienst nur auf `127.0.0.1`, nginx davor mit Zugangspasswort, jeder root-Schritt als eigenes Skript
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
| Zugangspasswort | `/etc/nginx/profomat.htpasswd` (Benutzer `profomat-test`) |
| nginx | `/etc/nginx/liebler.d/profomat.conf` → `/etc/nginx/snippets/profomat-*.conf` |
| Schema | EnsureCreated + `Schemaversion` (noch ohne Migrationen, siehe „Update“) |

## Sicherheitsstand – bitte zuerst lesen

- **Keine Hochschulanmeldung (SSO) bisher.** Deshalb verlangt nginx für **alles** unter `/profomat/` das Zugangspasswort, auch für
  das öffentliche Antragsformular und die Statuslinks der Studierenden. Ohne Passwort erreichbar ist nur `/gesund` (nur „ok“).
  `ausrollen.sh` bricht ab, solange Passwortdatei oder nginx-Einbindung fehlen.
- **Testanmeldung** (`Anmeldung__TestanmeldungErlaubt=true`): Wer das Zugangspasswort kennt, kann sich als jedes Demokonto
  anmelden, auch als Systemverwaltung. Nur zusammen mit Demodaten und nur für Testpersonen, die alles sehen dürfen.
- **Demodaten** (`Demodaten__Anlegen=true`): erfundene Konten (`…@demo.invalid`) und der Katalog aus dem Mock-Hochschulsystem.
  Ohne Demodaten ist die Datenbank leer; dann legt `Startkonto__Passwort` (mit `Startkonto__Kennung`, `Startkonto__Email`) das erste
  Konto der Systemverwaltung an, das weitere Konten anlegt.
- **Mail:** ohne `Mail__Host` wird nur simuliert (Log „Mail simuliert“). Auf dem Testsystem leer lassen, solange echte Studierende
  nicht beteiligt sein sollen.
- **Systemsiegel:** Der Schlüssel entsteht beim ersten Start in `/var/lib/profomat/schluessel/systemsiegel.pem`. Geht er verloren,
  sind bestehende Siegel nicht mehr prüfbar – deshalb sichert `profomat-sicherung.sh` den Ordner mit. `-DbNeu` lässt ihn unberührt.
- **Vor echtem Betrieb fehlt** (Fahrplan): SSO (Iteration 3), Missbrauchsschutz für das öffentliche Formular, EF-Migrationen statt
  EnsureCreated, Klärung der Rechtsform des Antrags. Erst dann `/drittpruefung/` ohne Zugangspasswort freigeben
  (`nginx/profomat-ohne-passwort.conf`).

## Vor dem ersten Lauf entscheiden bzw. eintragen

1. `deploy/server/konfiguration.env`: Host, Pfad (`/profomat`), Port (`5082`, Schritt 01 prüft, ob er frei ist), Benutzer des Zugangspassworts.
2. Zugangspasswort: `01-einrichten.sh` fragt es einmal ab (htpasswd, bcrypt). Weitere Konten: `sudo htpasswd -B /etc/nginx/profomat.htpasswd <name>`.
3. In `/etc/profomat/profomat.env` (Schritt 01 legt die Datei aus `profomat.env.beispiel` an): Demodaten + Testanmeldung **oder**
   Startkonto; optional Mail.

## Erstinstallation

Voraussetzung: Das VibeVHS-Kit ist eingerichtet (nginx mit `liebler.conf`, Zertifikat, Firewall, Pakete `sqlite3`, `apache2-utils`).

```powershell
.\deploy\server\deploy.ps1 -NurKit                                          # Kit nach /tmp/profomat-kit
ssh -t srvadmin@liebler.iui.hs-osnabrueck.de "sudo bash /tmp/profomat-kit/schritte/01-einrichten.sh"   # Benutzer, Verzeichnisse, Unit, nginx, Passwort
ssh -t srvadmin@liebler.iui.hs-osnabrueck.de "sudo nano /etc/profomat/profomat.env"                    # Entscheidungen eintragen (siehe oben)
.\deploy\server\deploy.ps1                                                  # testen, bauen, hochladen, einspielen
```

Prüfen: `https://liebler.iui.hs-osnabrueck.de/profomat/` fragt nach dem Zugangspasswort, danach die Anmeldung. Auf dem Server:
`curl http://127.0.0.1:5082/profomat/gesund` → `ok`, Logs `journalctl -u profomat -f` und `/var/log/profomat/`.

## Update

```powershell
.\deploy\server\deploy.ps1            # Tests, Build, Upload, Einspielen
.\deploy\server\deploy.ps1 -DbNeu     # nach einer Änderung der Schemaversion (siehe unten)
```

`deploy.ps1` führt zuerst alle Tests aus (`-OhneTests` nur im Notfall), baut dann in `deploy/ausgabe` (eigener Artefakt-Ordner; die
lokal laufende Instanz bleibt unberührt) und prüft, dass weder Datenbanken noch Schlüssel, `data/` oder `logs/` im Archiv liegen –
der Server prüft das noch einmal. Übertragen wird **nur** die Ausgabe von `dotnet publish`. Auf dem Server: Datenbank und Schlüssel
sichern (`vor-update-<Zeit>.db` und `…-schluessel.tar.gz`), `/opt/profomat` ersetzen (die alte Version bleibt als `.vorher`),
starten, `/gesund` abwarten. Antwortet die neue Version nicht, kommt die vorige automatisch zurück.

**Schemaversion geändert:** Solange es keine EF-Migrationen gibt, startet eine neue Version mit höherer `Schemaversion` nicht auf
einer alten Datenbank („Datenbankschema veraltet“). `ausrollen.sh` erkennt das, rollt zurück und nennt den Ausweg `-DbNeu`: Die
Datenbank wird nach `/var/lib/profomat/beiseite/` verschoben (nicht gelöscht) und neu angelegt. Scheitert auch das, kommen vorige
Version und vorige Datenbank zurück. Alle auf dem Testsystem erfassten Daten sind danach nicht mehr in der Anwendung – vor dem
Produktivbetrieb deshalb Migrationen einführen (Fahrplan).

## Sicherung und Rücksicherung

- Täglich 02:00 Uhr (`profomat-sicherung.timer`) und vor jedem Update: `sqlite3 .backup` (konsistent im laufenden Betrieb),
  Plausibilitätsprüfung über die Tabelle `Schemainfo`, dazu der Schlüsselordner als `…-schluessel.tar.gz` (0600). Aufbewahrung
  14 Tage (vor Updates 30 Tage).
- Rücksicherung: `sudo systemctl stop profomat` → Sicherung als `/var/lib/profomat/profomat.db` einsetzen (vorher `-wal`/`-shm`
  beiseitelegen, Besitzer `profomat`, Rechte 0640), bei Bedarf Schlüssel mit
  `sudo tar -xzf <…-schluessel.tar.gz> -C /var/lib/profomat` → `sudo systemctl start profomat`.
- Programm zurück: `ssh -t srvadmin@liebler.iui.hs-osnabrueck.de "sudo bash /tmp/profomat-kit/schritte/ausrollen.sh --zurueck"`.
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
