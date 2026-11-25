# ProfOMat – Fahrplan

| Iteration | Inhalt | Stand |
|---|---|---|
| 0 | Fundament: Lösung nach VibeVHS-Muster, Konten/Rollen, Katalog mit Abgleich und Alt-Status, Postausgang, Protokoll, Systemsiegel | fertig (21.11.2026) |
| 1 | Anmeldung mündlicher Drittprüfungen: Antrag, Mailbestätigung, Festlegung, Unterschriften (Schlüssel/Feld), Bestellung, PDF an Sekretariat | fertig (21.11.2026) |
| 1b | Mündliche Prüfung live: Fragen, Punkte, Bemerkungen und Zurückstellungen per Tastatur protokollieren, laufende Note nach Standardschlüssel, Protokoll-PDF mit Erst-/Zweitprüfung und festgelegter Note | fertig (23.11.2026) |
| 2 | Klausuren: CSV-Import, Schlüssel (Ende der Matrikelnummer), Räume und Sitzplätze, Serienbrief-Datenquelle, Aufgaben/Punkte/Notenschlüssel, Veröffentlichung mit Statistik, Notenexport | fertig (21.11.2026) |
| 2b | Grundriss-Editor, Tischnummerierung nach Muster, gestreute Platzvergabe, Serienbrief in Austeilreihenfolge, Sitzplan | fertig (21.11.2026) |
| 2c | Persönliche Hilfe: Verteiler beschriften (Schrank nachbauen, Geräte einsetzen und beschriften, Streifen-PDF mit Schnittmarken) | fertig (23.11.2026) |
| 2d | Persönliche Hilfe: Gefache lasern (Teilungseinsätze für Kisten, 3D-Vorschau, Schnittvorlagen SVG/DXF je Platte) | fertig (25.11.2026) |
| 2a | Klausuren-Feinschliff: QR-Code auf dem Deckblatt und Scan beim Punkte-Eintragen, Mitarbeitende für die Korrektur freigeben, Klausur der Katalogprüfung zuordnen, Sitzplan als PDF zum Aushängen | offen |
| 5 | Hausarbeiten: Prüfungsliste, Themenliste (aus dem Webseitentext), Teams mit Wünschen, Zuteilung, persönliche Themenbestätigung; persönlicher Postausgang (.eml fürs Desktop-Outlook) mit Mailsignatur aus Outlook | fertig (25.11.2026) |
| 5b | Hausarbeiten: Bestätigungsmail aus editierbarer Vorlage, Einschränkungen bei der Bestätigung, Gesprächstermine im Kalender (15-Minuten-Raster, Ziehen/Klicken, automatisch einplanen), Terminmails mit Platzhaltern, ZIP aller .eml, Kalender für Outlook (.ics und Abo-Link) – fertig (25.11.2026); offen: Wunschformular für Studierende, Abgabe und Projektmesse abhaken, Bewertung und Notenexport | in Arbeit |
| 6 | Finanzen: Laborteams, Töpfe (Laborkostenstelle, Investitions- und SQM-Maßnahmen), Import der SAP-Jahresstände, Stand per KI-JSON und SQM-Excel, geplante Ausgaben, Sammel-Auslagenabrechnung, Hilfskräfte planen und Einstellungsmöglichkeiten, Finanzcockpit mit Warnung vor der Übertragsgrenze, Zeitachse aller Töpfe, neue Ausgabe planen und aufteilen, Verlauf und Buchungssuche (FA-700–711, FA-714–717) | fertig (25.11.2026) |
| 6b | Finanzen: Fristerinnerungen per Mail (FA-712), Budgetprognose für das Folgejahr aus Teilnehmerzahlen, SWS und Personen (FA-713), Einzelbuchungen von Hand, Verlauf und Jahresvergleich, Kommentare und Markierungen je Buchung (FA-718) | fertig (25.11.2026) |
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
- Finanzen, geklärt (25.11.2026): Bezugsgröße der Übertragsgrenze ist nur das „Budget Sachmittel Fak“; Klaus wünscht die Warnung (FA-715), auch wenn bei 12103012 bisher nichts eingezogen wurde.
- Finanzen, geklärt: Auftrag 58216001 sind zusätzlich vereinbarte Berufungsmittel (im Vortrag 2025 → 2026 enthalten, daher die Abweichung zum Endstand).
- Finanzen, geklärt: Hilfskraftkosten werden auf Kostenstelle und SQM aufgeteilt (in ProfOMat: Anteile je Beschäftigung).
- Zurückgestellt (25.11.2026): Mailversand im Namen der angemeldeten Person (Office 365, SMTP `smtp.office365.com:587` STARTTLS bzw. Microsoft Graph, jeweils OAuth2):
  braucht genau eine App-Registrierung für ProfOMat im Entra ID der Hochschule (selbst, falls Beschäftigte Apps registrieren dürfen, sonst IT-Service;
  Zustimmung für `SMTP.Send` bzw. `Mail.Send`), je Person eine einmalige Microsoft-Anmeldung und ein verschlüsselt gespeichertes Refresh-Token;
  SMTP mit XOAUTH2 bräuchte ein neues Paket (z. B. MailKit), Graph ginge ohne. S/MIME-Signaturen setzt dann nicht mehr Outlook. Bis dahin bleibt der
  persönliche Postausgang mit .eml fürs Desktop-Outlook (FA-022).
