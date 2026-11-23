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
| Schema | EnsureCreated + `Schemaversion` (noch ohne Migrationen, siehe „Update“) |

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
- **Studierende** stellen den Antrag auf mündliche Drittprüfung ohne Anmeldung. Schutz des öffentlichen Formulars: Sicherheitsaufgabe
  (Captcha nach dem ALTCHA-Prinzip, selbst gehostet, ohne Bilderrätsel und ohne Dritte), unsichtbares Köderfeld, höchstens 3
  unbestätigte Anträge je Mailadresse und Tag, nur Hochschul-Mailadressen; erst der Link in der Bestätigungsmail leitet weiter.
- **Mail:** ohne `Mail__Host` wird nur simuliert (Log „Mail simuliert“) – dann erreichen Bestätigungsmails die Studierenden nicht.
  Für echten Betrieb den Mailserver der Hochschule eintragen.
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
Datenbank wird nach `/var/lib/profomat/beiseite/` verschoben (nicht gelöscht) und neu angelegt – danach ist wieder die
Ersteinrichtung fällig (sofort erledigen). Scheitert auch das, kommen vorige
Version und vorige Datenbank zurück. Alle auf dem Server erfassten Daten sind danach nicht mehr in der Anwendung – vor dem
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
