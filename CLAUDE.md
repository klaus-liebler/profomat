# ProfOMat – Übergabe für Claude Code

Webanwendung für Professorinnen/Professoren der Hochschule Osnabrück (Prüfungsabläufe mit digitaler Unterschrift). Ansprechpartner: Klaus.
Gleicher Stil wie **VibeVHS** (`C:\repos\vibevhs`, dort CLAUDE.md) und FraktionPerfekt.

## Spezifikation

`docs/spezifikation/` ist führend (`anforderungen.md`, `architektur.md`, `fahrplan.md`). Fachliche Änderungen im selben Schritt dort
nachziehen; FA-/NFA-Nummern nie neu vergeben.

## Stack

.NET 11, Blazor Server + Radzen, EF Core + SQLite, Serilog, Quartz.NET, PDFsharp/MigraDoc, QRCoder. Tests: xUnit v3, Shouldly.
Nicht verwenden: QuestPDF, MediatR, AutoMapper, FluentAssertions. Neue Pakete nur nach Rückfrage (z. B. SAML-Bibliothek, ClosedXML für Iteration 2).

## Befehle

Siehe README.md. Eigene Testinstanz immer mit eigener Datenbank und eigenem Schlüsselordner (`ConnectionStrings__ProfOMat`,
`Speicher__Schluessel`, `Speicher__Logs` auf ein Temp-Verzeichnis), Port 5310 oder abweichend, wenn Klaus' Instanz läuft.
Klaus' laufende Instanz nie beenden.

## Regeln

- Deutsch in Oberfläche und Fachcode. Gesperrte Aktionen erklären (`BegruendeterButton` + `Freigabe`), nach Aktionen `NaechsteSchritte`.
- Rechte im Dienst prüfen, nicht nur in der Oberfläche. Interaktive Seiten nur über `Vorgang`.
- Unterschriften: `Signaturinhalt`-Format `profomat-antrag-v1` und `Signatur.SiegelText` nie ändern (alte Unterschriften wären nicht
  mehr prüfbar); für Änderungen neue Version einführen.
- Private Schlüssel und deren Passwörter dürfen den Browser nie verlassen (nur `signatur.js` liest sie; kein `@bind` auf Passwortfelder).
- Geheimnisse (Passwörter, Siegelschlüssel, Zertifikate) nie ins Repository. Personendaten nicht in Logs (Pseudonyme, Antragsnummern).
- CSP ohne `unsafe-inline` für Skripte: kein Inline-JavaScript.
- Datenbank: EnsureCreated + `Schemaversion` (bei Modelländerung erhöhen); vor dem Produktivbetrieb EF-Migrationen einführen.
- Build ohne Warnungen, alle Tests grün; neue Funktionen mit Tests (Rechte, Grenzfälle) und bei Oberfläche Browserprüfung (auch 390 px).
- Commits nur lokal, nie pushen.
