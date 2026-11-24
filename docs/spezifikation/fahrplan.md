# ProfOMat – Fahrplan

| Iteration | Inhalt | Stand |
|---|---|---|
| 0 | Fundament: Lösung nach VibeVHS-Muster, Konten/Rollen, Katalog mit Abgleich und Alt-Status, Postausgang, Protokoll, Systemsiegel | fertig (21.11.2026) |
| 1 | Anmeldung mündlicher Drittprüfungen: Antrag, Mailbestätigung, Festlegung, Unterschriften (Schlüssel/Feld), Bestellung, PDF an Sekretariat | fertig (21.11.2026) |
| 1b | Mündliche Prüfung live: Fragen, Punkte, Bemerkungen und Zurückstellungen per Tastatur protokollieren, laufende Note nach Standardschlüssel, Protokoll-PDF mit Erst-/Zweitprüfung und festgelegter Note | fertig (23.11.2026) |
| 2 | Klausuren: CSV-Import, Schlüssel (Ende der Matrikelnummer), Räume und Sitzplätze, Serienbrief-Datenquelle, Aufgaben/Punkte/Notenschlüssel, Veröffentlichung mit Statistik, Notenexport | fertig (21.11.2026) |
| 2b | Grundriss-Editor, Tischnummerierung nach Muster, gestreute Platzvergabe, Serienbrief in Austeilreihenfolge, Sitzplan | fertig (21.11.2026) |
| 2c | Persönliche Hilfe: Verteiler beschriften (Schrank nachbauen, Geräte einsetzen und beschriften, Streifen-PDF mit Schnittmarken) | fertig (23.11.2026) |
| 2a | Klausuren-Feinschliff: QR-Code auf dem Deckblatt und Scan beim Punkte-Eintragen, Mitarbeitende für die Korrektur freigeben, Klausur der Katalogprüfung zuordnen, Sitzplan als PDF zum Aushängen | offen |
| 3 | Anmeldung über das Hochschulsystem (SAML/Shibboleth), Vorausfüllen für Studierende | nach Klärung mit dem IT-Service |
| 4 | Echte Katalogquelle (Webservice oder Exportdatei), PAdES-Siegel des Abschluss-PDFs, X.509-Zertifikate für Schlüssel | offen |
| B | Server liebler.iui.hs-osnabrueck.de/profomat: Deploy-Kit (`deploy/`), Pfad-Basis, Sicherung inkl. Siegelschlüssel; Anmeldung wie Podium (Ersteinrichtung, Einladungs-/Passwort-Links, ohne nginx-Passwort), Captcha für das öffentliche Formular | fertig (23.11.2026) |
| – | Vor dem Produktivbetrieb: EF-Migrationen statt EnsureCreated, SSO, echter Mailversand, echte Katalogquelle | offen |

## Offene Abklärungen

- **Rechtsform des Antrags** (ATPO § 18 Abs. 1 Satz 4 „schriftlich“): Regelung nach § 16a Abs. 1 oder ATPO-Änderung, Abstimmung mit Studierendensekretariat und Justiziariat.
- Muss die/der **Zweitprüfende selbst unterschreiben**? Heute legt die/der Erstprüfende sie bzw. ihn nach Rücksprache fest; eine zusätzliche Bestätigung wäre ein kleiner Schritt.
- Zuständige Studiendekanate und Postfächer der Studierendensekretariate je Fakultät.
- SSO: lokaler SP oder DFN-AAI, Freigabe der Matrikelnummer.
- Missbrauchsschutz für das öffentliche Antragsformular: umgesetzt (FA-114); im Betrieb beobachten, ob die Grenzen reichen.
