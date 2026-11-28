# Bereitstellung

Läuft auf **https://liebler.iui.hs-osnabrueck.de/profomat** – derselbe Server wie VibeVHS, FraktionPerfekt und Podium.

- `server/` – **gemeinsames Deploy-Kit** der vier Systeme (ProfOMat, FraktionPerfekt, VibeVHS, SchulPerfekt), beschrieben in
  [`server/README.md`](server/README.md); ProfOMat-eigen sind `konfiguration.env`, `umgebung.kit`, `umgebung.beispiel`, `nginx/locations.conf`
- `test/` – Simulation des Kits ohne Server (echtes PostgreSQL im Wegwerf-Container)
- `docker-compose.yml` – PostgreSQL 17 für Entwicklung und Tests (`127.0.0.1:5443`)

ProfOMat-Besonderheiten (Sicherheitsstand, Umstellung SQLite → PostgreSQL, Feldschlüssel, Prüferkonto): [`docs/deploy.md`](../docs/deploy.md).
Datenbank und Schlüssel werden **nie** übertragen: Sie liegen nur auf dem Server (Container `profomat-db`, `/var/lib/profomat`).
