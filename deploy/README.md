# Bereitstellung

Läuft auf **https://liebler.iui.hs-osnabrueck.de/profomat** – derselbe Server wie VibeVHS, FraktionPerfekt und Podium (nginx als
Reverse Proxy, Hochschulzertifikat, Firewall aus dem VibeVHS-Kit). Gleiches Muster wie `C:\repos\fraktionperfekt\deploy`:

- `server/deploy.ps1` – testen, bauen (linux-x64; `-Modus framework | self-contained | docker`), Kit und Archiv hochladen, auf dem Server einspielen (`-Pruefen` = Trockenlauf, `-Zurueck`, `-DbNeu`, `-Wartezeit`, `-NurKit`, `-NurBauen`)
- `server/schritte/01-einrichten.sh` (Rückweg `01-zurueck.sh`) – einmalig: Dienstbenutzer, Verzeichnisse, Unit, Sicherung, nginx (ohne Passwort, wie Podium)
- `server/schritte/ausrollen.sh` – prüfen (ändert nichts) → sichern → Konfiguration ergänzen → umschalten → Start mit Countdown beobachten → bei Fehler sofort zurück; Bausteine `pruefen.sh`, `konfig-abgleich.sh`, `db-pruefen.sh`, `beobachten.sh`
- `server/docker/` – Dockerfile und Compose-Vorlage für den Modus docker
- `test/simulation.sh` – Ende-zu-Ende-Test des Kits in einem Wegwerf-Container (systemd und nginx nachgebildet)
- `server/konfiguration.env` – Host, Pfad, Port, Dienstname (eine Quelle für Skripte, Unit und nginx-Vorlagen)
- `server/systemd/`, `server/nginx/`, `server/werkzeuge/` – Vorlagen; `schritte/liebler-nginx.sh` ist in allen Kits identisch

Ausführlich (Sicherheitsstand, Erstinstallation, Update, Schemaversion, Sicherung, Rückweg): [`docs/deploy.md`](../docs/deploy.md).

Datenbank und Schlüssel werden **nie** übertragen: Sie liegen nur auf dem Server unter `/var/lib/profomat`. Geheimnisse und bewusste
Schalter stehen nur auf dem Server (`/etc/profomat/profomat.env`). Anmeldung wie Podium: Ersteinrichtung des ersten Kontos direkt nach dem
ersten Ausrollen, weitere Konten per Einladungslink.
