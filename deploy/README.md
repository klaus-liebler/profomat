# Bereitstellung

Testsystem auf **https://liebler.iui.hs-osnabrueck.de/profomat** – derselbe Server wie VibeVHS, FraktionPerfekt und Podium (nginx als
Reverse Proxy, Hochschulzertifikat, Firewall aus dem VibeVHS-Kit). Gleiches Muster wie `C:\repos\fraktionperfekt\deploy`:

- `server/deploy.ps1` – testen, bauen (self-contained linux-x64), Kit und Archiv hochladen, auf dem Server einspielen (`-DbNeu`, `-NurKit`, `-NurBauen`)
- `server/schritte/01-einrichten.sh` (Rückweg `01-zurueck.sh`) – einmalig: Dienstbenutzer, Verzeichnisse, Unit, Sicherung, nginx mit Zugangspasswort
- `server/schritte/ausrollen.sh` – Update mit Sicherung (Datenbank + Schlüssel), Gesundheitsprüfung und automatischem Rückweg
- `server/konfiguration.env` – Host, Pfad, Port, Dienstname (eine Quelle für Skripte, Unit und nginx-Vorlagen)
- `server/systemd/`, `server/nginx/`, `server/werkzeuge/` – Vorlagen; `schritte/liebler-nginx.sh` ist in allen Kits identisch

Ausführlich (Sicherheitsstand, Erstinstallation, Update, Schemaversion, Sicherung, Rückweg): [`docs/deploy.md`](../docs/deploy.md).

Datenbank und Schlüssel werden **nie** übertragen: Sie liegen nur auf dem Server unter `/var/lib/profomat`. Geheimnisse und bewusste
Schalter stehen nur auf dem Server (`/etc/profomat/profomat.env`, `/etc/nginx/profomat.htpasswd`).
