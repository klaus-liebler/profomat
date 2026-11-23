# ProfOMat

Webanwendung für Professorinnen und Professoren der Hochschule Osnabrück: Prüfungsabläufe digital, mit Unterschrift, ohne Papier.
Funktionen: **Anmeldung mündlicher Drittprüfungen** (ATPO § 18 Abs. 1 Satz 3), **Klausuren** (Prüfungsliste importieren, Räume und Sitzplätze, Serienbrief-Daten, Punkte, Noten, Veröffentlichung mit Statistik) und
**Mündliche Prüfung live** (Fragen, Punkte und Zurückstellungen per Tastatur protokollieren, Note läuft mit). Aufbau und Leitlinien folgen
**VibeVHS** (`C:\repos\vibevhs`).

| Dokument | Inhalt |
|---|---|
| [`docs/spezifikation/anforderungen.md`](docs/spezifikation/anforderungen.md) | Anforderungen (FA-Nummern), führend |
| [`docs/spezifikation/architektur.md`](docs/spezifikation/architektur.md) | Aufbau, Entscheidungen (Signaturkette statt PDF), SSO der Hochschule, Katalogabgleich |
| [`docs/spezifikation/fahrplan.md`](docs/spezifikation/fahrplan.md) | Iterationen und offene Abklärungen |

## Starten

```powershell
dotnet run --project src\ProfOMat.Web --launch-profile http
```

<http://localhost:5310> öffnen. In der Entwicklung entstehen Demodaten (`src/ProfOMat.Web/data/profomat.db`; zum Zurücksetzen den
Ordner `data` löschen; nach Modelländerungen meldet der Start „Datenbankschema veraltet“, dann ebenso). Mails werden nur simuliert und stehen unter **Verwaltung → Postausgang**, dort ist auch der Bestätigungslink
für Studierende zu finden.

Testkonten (alle erfunden, Testanmeldung ohne Passwort):

| Konto | Rollen |
|---|---|
| Vera Verwaltung | Systemverwaltung |
| Prof. Dr. Erika Mustermann, Prof. Dr. Lena Probst, Prof. Dr.-Ing. Jonas Tester | Prüfende |
| Prof. Dr.-Ing. Max Beispiel | Prüfende, Studiendekanat (Fakultät IuI) |
| Prof. Dr. Paula Rechner | Prüfende, Studiendekanat (Fakultät WiSo) |
| Sabine Sekretariat | Studierendensekretariat |

Ablauf zum Ausprobieren: *Antrag stellen* (ohne Anmeldung, Mail z. B. `…@stud.hs-osnabrueck.de`) → Postausgang: Link öffnen und
bestätigen → als Mustermann Termin festlegen und unterschreiben → als Beispiel bestellen → PDF.
Klausuren: als Mustermann unter *Klausuren* die Datei `tools/Testdaten/pruefungsliste-beispiel.csv` (57 erfundene Personen) hochladen. Der Raum SI 0012 hat einen Beispielgrundriss (*Räume → Grundriss*).
Mündliche Prüfung live: als Mustermann unter *Live-Protokoll* eine Bezeichnung eingeben, Enter, dann z. B. `7⏎5⏎4⏎ 8⏎⏎z⏎ 8⏎4⏎` tippen.

Einen Schlüssel zum Testen erzeugt `openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -aes-256-cbc -out test.pem`.

## Tests

```powershell
dotnet build ProfOMat.slnx                                   # Warnungen sind Fehler
dotnet test --project tests\ProfOMat.Domain.Tests
dotnet test --project tests\ProfOMat.Application.Tests
node tests\Signatur\signatur-test.mjs                        # Schlüsselformate (braucht openssl)
node tests\Browser\ablauf.mjs http://localhost:5310 <Ordner>  # Drittprüfung im Headless-Edge, Instanz muss laufen
node tests\Browser\klausur.mjs http://localhost:5310 <Ordner> # Klausurablauf (frische Datenbank)
node tests\Browser\muendlich.mjs http://localhost:5310 <Ordner> # Mündliche Prüfung live: nur Tastatur, schnelles Tippen, 390 px
```

## Betrieb (Kurzfassung)

Geheimnisse nur über Umgebungsvariablen: `Startkonto__Passwort` (erstes Konto der Systemverwaltung), `Mail__Host`, `Mail__Benutzer`,
`Mail__Passwort`. Der Ordner `Speicher:Schluessel` enthält den Systemsiegel-Schlüssel und die Cookie-Schlüssel. Er gehört in die
Sicherung, nie ins Repository.

Testsystem auf dem Hochschulserver: `.\deploy\server\deploy.ps1` (Tests, Linux-Build, Upload, Einspielen mit Sicherung und
Rückweg; `-DbNeu` nach geänderter Schemaversion). Einrichtung und Sicherheitsstand: [docs/deploy.md](docs/deploy.md).
