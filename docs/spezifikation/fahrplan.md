# ProfOMat – Fahrplan

| Iteration | Inhalt | Stand |
|---|---|---|
| 0 | Fundament: Lösung nach VibeVHS-Muster, Konten/Rollen, Katalog mit Abgleich und Alt-Status, Postausgang, Protokoll, Systemsiegel | fertig (21.11.2026) |
| 1 | Anmeldung mündlicher Drittprüfungen: Antrag, Mailbestätigung, Festlegung, Unterschriften (Schlüssel/Feld), Bestellung, PDF an Sekretariat | fertig (21.11.2026) |
| 2 | Klausuren: CSV-Import, Klausur-ID, Räume und Sitzplätze, Serienbrief-Datenquelle, Aufgaben/Punkte/Notenschlüssel, Veröffentlichung mit Statistik, Notenexport | fertig (21.11.2026) |
| 2b | Grundriss-Editor, Tischnummerierung nach Muster, gestreute Platzvergabe, Serienbrief in Austeilreihenfolge, Sitzplan | fertig (21.11.2026) |
| 2a | Klausuren-Feinschliff: QR-Code auf dem Deckblatt und Scan beim Punkte-Eintragen, Mitarbeitende für die Korrektur freigeben, Klausur der Katalogprüfung zuordnen, Sitzplan als PDF zum Aushängen | offen |
| 3 | Anmeldung über das Hochschulsystem (SAML/Shibboleth), Vorausfüllen für Studierende | nach Klärung mit dem IT-Service |
| 4 | Echte Katalogquelle (Webservice oder Exportdatei), PAdES-Siegel des Abschluss-PDFs, X.509-Zertifikate für Schlüssel | offen |
| – | Vor dem Produktivbetrieb: EF-Migrationen statt EnsureCreated, Bereitstellung (nginx, Pfad-Basis), Sicherung inkl. Siegelschlüssel | offen |

## Offene Abklärungen

- **Rechtsform des Antrags** (ATPO § 18 Abs. 1 Satz 4 „schriftlich“): Regelung nach § 16a Abs. 1 oder ATPO-Änderung, Abstimmung mit Studierendensekretariat und Justiziariat.
- Muss die/der **Zweitprüfende selbst unterschreiben**? Heute legt die/der Erstprüfende sie bzw. ihn nach Rücksprache fest; eine zusätzliche Bestätigung wäre ein kleiner Schritt.
- Zuständige Studiendekanate und Postfächer der Studierendensekretariate je Fakultät.
- SSO: lokaler SP oder DFN-AAI, Freigabe der Matrikelnummer.
- Missbrauchsschutz für das öffentliche Antragsformular (Begrenzung je Mailadresse, ggf. Captcha) vor dem Echtbetrieb.
