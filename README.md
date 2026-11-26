# ProfOMat

Webanwendung für Professorinnen und Professoren der Hochschule Osnabrück: Prüfungsabläufe digital, mit Unterschrift, ohne Papier.
Funktionen: **Anmeldung mündlicher Drittprüfungen** (ATPO § 18 Abs. 1 Satz 3), **Klausuren** (Prüfungsliste importieren, Räume und Sitzplätze, Serienbrief-Daten, Punkte, Noten, Veröffentlichung mit Statistik) und
**Mündliche Prüfung live** (Fragen, Punkte, Bemerkungen und Zurückstellungen per Tastatur protokollieren, Note läuft mit; Protokoll-PDF mit Erst-/Zweitprüfung, festgelegter Note und Unterschriftsfeldern). Dazu unter *Nerd Tools* **Verteiler beschriften** (Schaltschrank nachbauen, Geräte beschriften, Streifen-PDF mit Schnittmarken) und **Gefache lasern** (Teilungseinsätze für Euro-Lagerboxen u. a., 3D-Vorschau, Schnittvorlagen SVG/DXF
für den Lasercutter). Aufbau und Leitlinien folgen
**VibeVHS** (`C:\repos\vibevhs`).

| Dokument | Inhalt |
|---|---|
| [`docs/spezifikation/anforderungen.md`](docs/spezifikation/anforderungen.md) | Anforderungen (FA-Nummern), führend |
| [`docs/spezifikation/architektur.md`](docs/spezifikation/architektur.md) | Aufbau, Entscheidungen (Signaturkette statt PDF), SSO der Hochschule, Katalogabgleich |
| [`docs/spezifikation/fahrplan.md`](docs/spezifikation/fahrplan.md) | Iterationen und offene Abklärungen |
| [`docs/sicherheit-und-betrieb.md`](docs/sicherheit-und-betrieb.md) | Sicherheit und Betrieb: ehrlicher Ist-Stand mit Fundstellen |

## Sicherheit und Betrieb (für Technik)

> Für IT-Betrieb, Datenschutz und technisch Interessierte – ehrlicher Stand mit ✅/⚠️/❌. Wer nur die Funktionen sehen will,
> springt weiter zu [Starten](#starten).

| Ebene | Stand in einem Satz |
|---|---|
| Netz | Öffentlich erreichbar über `https://liebler.iui.hs-osnabrueck.de/profomat/`, kein VPN und keine IP-Beschränkung; SSH nur aus dem VPN. |
| Webserver (nginx) | TLS 1.2/1.3, Umleitung auf HTTPS, `server_tokens off`; HSTS, CSP und Rate-Limits setzt die Anwendung, nicht nginx. |
| Anwendung | Rechte im Dienst, EF Core, Antiforgery, strenge CSP; Kestrel nur auf `127.0.0.1:5082`. |
| Anmeldung | Lokale Konten mit PBKDF2 und Anmeldebremse; **kein zweiter Faktor**, Hochschul-SSO (Shibboleth/SAML) geplant. |
| Daten | SQLite-Datei nur für den Dienstbenutzer lesbar; **keine Feldverschlüsselung**, Sicherungen unverschlüsselt. |
| Betrieb | systemd mit Härtung, Sicherung vor jedem Update und täglich, automatischer Rückweg beim Ausrollen; Logs nur lokal. |

Ausführlich mit Fundstellen im Code und offenen Punkten: [sicherheit-und-betrieb.md](docs/sicherheit-und-betrieb.md).

## Starten

```powershell
dotnet run --project src\ProfOMat.Web --launch-profile http
```

<http://localhost:5310> öffnen. In der Entwicklung entstehen Demodaten (`src/ProfOMat.Web/data/profomat.db`; zum Zurücksetzen den
Ordner `data` löschen; nach Modelländerungen meldet der Start „Datenbankschema veraltet“, dann ebenso). Mails werden nur simuliert und stehen unter **Verwaltung → Postausgang**, dort ist auch der Bestätigungslink
für Studierende zu finden.

Testkonten (Testanmeldung ohne Passwort; Mailadressen `…@demo.invalid`, außer Liebler und Niemeyer alle Personen erfunden):

| Konto | Rollen |
|---|---|
| Vera Verwaltung | Systemverwaltung |
| Prof. Dr. Klaus Liebler, Prof. Dr. Philip Niemeyer, Prof. Dr. Erika Mustermann, Prof. Dr. Lena Probst, Prof. Dr.-Ing. Jonas Tester | Prüfende |
| Prof. Dr.-Ing. Max Beispiel | Prüfende, Studiendekanat (Fakultät IuI) |
| Prof. Dr. Paula Rechner | Prüfende, Studiendekanat (Fakultät WiSo) |
| Sabine Sekretariat | Studierendensekretariat |

Ablauf zum Ausprobieren: als Mustermann *Einladung erstellen* (Zweitprüfende: Probst) → Link in einem privaten Fenster öffnen,
Matrikelnummer ergänzen und unterschreiben → als Mustermann Termin ergänzen und unterschreiben → als Probst unterschreiben → als Beispiel
bestellen → PDF. Ohne Studiendekanat an der Fakultät (*Studiengänge und Prüfungen*) leitet Probst das PDF stattdessen weiter.
Klausuren: als Mustermann unter *Klausuren* die Datei `tools/Testdaten/pruefungsliste-beispiel.csv` (57 erfundene Personen) hochladen. Der Raum SI 0012 hat einen Beispielgrundriss (*Räume → Grundriss*).
Hausarbeiten: unter *Hausarbeiten* `tools/Testdaten/pruefungsliste-mehrere.csv` hochladen und eine Prüfung wählen, unter *Themen* die Themenliste
der Modulwebseite einfügen, Teams mit Wünschen anlegen, zuteilen, *Bestätigungsmails erstellen* → *Mails zum Versenden* (.eml fürs Desktop-Outlook;
Signatur vorher unter *Profil → Mailsignatur* hochladen).
Mündliche Prüfung live: als Klaus Liebler (oder Philip Niemeyer, Erika Mustermann) unter *Live-Protokoll* eine Bezeichnung eingeben, Enter, dann z. B. `7⏎5⏎4⏎ 8⏎⏎z⏎ 8⏎4⏎` tippen.
Verteiler beschriften: unter *Nerd Tools → Verteiler beschriften* einen Schrank anlegen, freien Platz (+) anklicken, Gerät wählen, rechts beschriften, unten *PDF erstellen*.
Gefache lasern: unter *Nerd Tools → Gefache lasern* ein Gefach anlegen (Euro-Lagerbox vorbelegt), Höhe und Fächer einstellen, unten die
Schnittvorlagen laden; *Öffentlich sichtbar* zeigt es allen anderen, die es als Vorlage übernehmen können. Das three.js-Bündel der 3D-Vorschau baut `tools/Three/README.md`.
Der Katalog (Mock) enthält die Maschinenbau-Studiengänge der Hochschule (Osnabrück und Lingen); Prüfungsordnungen und Prüfungen darin sind erfunden.

Einen Schlüssel zum Testen erzeugt `openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -aes-256-cbc -out test.pem`.

## Tests

```powershell
dotnet build ProfOMat.slnx                                   # Warnungen sind Fehler
dotnet test --project tests\ProfOMat.Domain.Tests
dotnet test --project tests\ProfOMat.Application.Tests
node tests\Signatur\signatur-test.mjs                        # Schlüsselformate (braucht openssl)
node tests\Browser\ablauf.mjs http://localhost:5310 <Ordner>  # Drittprüfung per Einladung im Headless-Edge (alle Rollen, PDF, 390 px), Instanz muss laufen
node tests\Browser\klausur.mjs http://localhost:5310 <Ordner> # Klausurablauf (frische Datenbank)
node tests\Browser\muendlich.mjs http://localhost:5310 <Ordner> # Mündliche Prüfung live: Tastatur, Bemerkung, Protokoll-PDF, 390 px
node tests\Browser\beschriftung.mjs http://localhost:5310 <Ordner> # Verteiler beschriften: Klick, Ziehen, Pfeiltasten, PDF, 390 px
node tests\Browser\gefach.mjs http://localhost:5310 <Ordner>       # Gefache lasern: 3D, Höhe ≤ Stapelhöhe, Fachgrößen, SVG/DXF/ZIP, öffentlich/Vorlage, 390 px
node tests\Browser\hausarbeit.mjs http://localhost:5310 <Ordner>  # Hausarbeit: Signatur, Themen, Teams, .eml, Bestätigung mit Einschränkungen, Gesprächskalender, Terminmails, ZIP, .ics/Abo, 390 px (frische Datenbank)
node tests\Browser\finanzen.mjs http://localhost:5310 <Ordner>    # Finanzen: Team, SAP-Auszüge, Cockpit, Zeitachse, Aufteilen, Planung, Auslagen (XLSX), Hilfskräfte, SQM-Excel, KI-JSON, 390 px
node tests\Browserinanzen.mjs http://localhost:5310 <Ordner>    # Finanzen: Team, SAP-Auszüge, Cockpit, Zeitachse, Aufteilen, Planung, Auslagen (XLSX), Hilfskräfte, SQM-Excel, KI-JSON, 390 px
```

## Betrieb (Kurzfassung)

Anmeldung wie Podium: Auf einer leeren Datenbank legt man das erste Konto auf der Anmeldeseite an (Ersteinrichtung), weitere nur per
Einladungslink (*Verwaltung → Konten*). Geheimnisse nur über Umgebungsvariablen: `Mail__Host`, `Mail__Benutzer`, `Mail__Passwort`. Der Ordner `Speicher:Schluessel` enthält den Systemsiegel-Schlüssel und die Cookie-Schlüssel. Er gehört in die
Sicherung, nie ins Repository.

Testsystem auf dem Hochschulserver: `.\deploy\server\deploy.ps1` (Tests, Linux-Build, Upload, Einspielen mit Sicherung und
Rückweg; `-DbNeu` nach geänderter Schemaversion). Einrichtung und Sicherheitsstand: [docs/deploy.md](docs/deploy.md).
