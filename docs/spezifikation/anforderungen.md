# ProfOMat – Anforderungen

Webanwendung, die Professorinnen und Professoren der Hochschule Osnabrück bei wiederkehrenden Prüfungsabläufen entlastet.
Aufbau, Leitlinien und Bausteine wie VibeVHS/FraktionPerfekt. FA-/NFA-Nummern werden nie neu vergeben.

## Grundlagen

| Nr. | Anforderung | Stand |
|---|---|---|
| FA-001 | Anmeldung für Beschäftigte mit lokalem Konto (Kennung + Passwort, PBKDF2 nach ASP.NET Core Identity, mind. 12 Zeichen). | umgesetzt |
| FA-002 | Später zusätzlich Anmeldung über das Hochschulsystem (SAML 2.0, Shibboleth-IdP der Hochschule), siehe architektur.md. Lokale Konten bleiben als Rückfall. | geplant |
| FA-003 | Rollen: Prüfende, Studiendekanat, Studierendensekretariat, Systemverwaltung; mehrere Rollen gleichzeitig. Rechte werden im Dienst geprüft, gesperrte Aktionen erklären sich (Tooltip). | umgesetzt |
| FA-004 | Startkonto der Systemverwaltung nur aus der Umgebung (`Startkonto__Passwort`), Testanmeldung nur in der Entwicklung. | umgesetzt |
| FA-010 | Katalog: Fakultäten, Studiengänge (mit Prüfungsordnung), Prüfungen – regelmäßig (nachts) und auf Knopfdruck aus dem Hochschulsystem abgeglichen. | umgesetzt (Quelle: Mock) |
| FA-011 | Katalogeinträge werden **nie gelöscht**. Verschwindet ein Eintrag im Hochschulsystem oder wird er von Hand archiviert, wird er „alt“. | umgesetzt |
| FA-012 | Auswahlen zeigen alte Studiengänge/Prüfungen nur, wenn man es ausdrücklich wünscht („auch alte anzeigen“). | umgesetzt |
| FA-013 | Fehlende Studiengänge/Prüfungen lassen sich lokal ergänzen; der Abgleich fasst lokale Einträge nicht an. Von Hand Archiviertes holt der Abgleich nicht zurück. | umgesetzt |
| FA-014 | Je Fakultät (abweichend je Studiengang) zuständiges Studiendekanat und Postfach des Studierendensekretariats. | umgesetzt |
| FA-020 | Jede Mail läuft über den Postausgang (Wiederholung mit Rückzug, Anzeige für die Systemverwaltung, ohne Mailserver nur simuliert). | umgesetzt |
| FA-021 | Protokoll jeder fachlichen Aktion (Studierende nur mit Antragsnummer). | umgesetzt |

## Iteration 1 – Anmeldung mündlicher Drittprüfungen (ATPO § 18 Abs. 1 Satz 3)

| Nr. | Anforderung | Stand |
|---|---|---|
| FA-100 | Studierende beantragen ohne Konto: Vorname, Nachname, Matrikelnummer, Hochschul-Mailadresse, Studiengang, Prüfung, gewünschte/r Erstprüfende/r (aus der Liste der Prüfenden). | umgesetzt |
| FA-101 | Freitexte: terminliche Einschränkungen/Wünsche und Wunsch für die/den Zweitprüfende/n (ATPO § 24 Abs. 4). | umgesetzt |
| FA-102 | Mit Anmeldung über das Hochschulsystem werden Name, Mail und Matrikelnummer vorausgefüllt. | geplant (mit FA-002) |
| FA-103 | Der Antrag wird unterschrieben, wahlweise (a) mit eigenem privaten Schlüssel per Drag & Drop, signiert im Browser, oder (b) im Unterschriftsfeld mit Maus/Stift/Finger. | umgesetzt |
| FA-104 | Unterschrieben wird ein angezeigter, kanonischer Text („what you see is what you sign“); der Server bildet ihn selbst nach und lehnt Abweichungen ab. Vorlagen gelten 30 Minuten. | umgesetzt |
| FA-105 | Schlüsselformate: PKCS#8 (auch passwortgeschützt, PBES2/PBKDF2/AES), PKCS#1, SEC1, DER, JWK; RSA ≥ 2048 Bit, EC P-256/P-384/P-521. Privater Schlüssel und Passwort verlassen den Browser nie. | umgesetzt |
| FA-106 | Identität der Studierenden: Bestätigungslink an die Hochschul-Mailadresse (Domain einstellbar); erst nach Klick auf „bestätigen“ geht der Antrag weiter. Unbestätigte Anträge verfallen nach 7 Tagen. | umgesetzt |
| FA-107 | Statusseite für Studierende über den geheimen Link (gespeichert nur als Hash): Stand, Termin, Unterschriften, PDF; Zurückziehen solange offen. | umgesetzt |
| FA-110 | Die gewählte Erstprüferin/der gewählte Erstprüfer erhält eine Mail mit **direktem Link** zur Bearbeitung. | umgesetzt |
| FA-111 | Erstprüfende legen nach Rücksprache Termin, Ort und Zweitprüfende (mit Konto oder ohne) fest, können vormerken und unterschreiben dann (Schlüssel oder Unterschriftsfeld). Studierende erfahren den vorläufigen Termin. | umgesetzt |
| FA-112 | Erstprüfende können mit Begründung ablehnen; die Begründung geht an die Studierenden. | umgesetzt |
| FA-113 | Beschäftigte unterschreiben mit Schlüssel nur, wenn er im Profil hinterlegt ist (Besitznachweis per Signatur, Widerruf möglich). | umgesetzt |
| FA-120 | Danach unterschreibt das zuständige Studiendekanat (Bestellung der Prüfenden, ATPO § 24 Abs. 2). Wer selbst prüft, bestellt sich nicht selbst – dann jede andere Person mit der Rolle. | umgesetzt |
| FA-121 | Das Studiendekanat kann mit Rückfrage zurückgeben; die Unterschrift der Erstprüfenden wird dann „überholt“ (bleibt sichtbar). | umgesetzt |
| FA-130 | Nach der letzten Unterschrift entsteht ein PDF (alle Angaben, Unterschriften mit Prüfergebnis, Prüfwert, QR-Code zur Prüfansicht, Anhang: unterschriebener Text) und geht per Mail an das Studierendensekretariat; Kopie an Studierende, Erst- und Zweitprüfende. | umgesetzt |
| FA-131 | Jede Unterschrift ist mit dem Systemsiegel (ECDSA-Schlüssel des Servers) versiegelt und über eine Hash-Kette mit der vorherigen verbunden; Veränderungen in der Datenbank werden angezeigt. | umgesetzt |
| FA-132 | Das fertige PDF zusätzlich mit einem PAdES-Siegel (Zertifikat der Hochschule) versehen, damit es auch außerhalb des ProfOMat prüfbar ist. | geplant |

## Iteration 2 – Klausuren: Vorbereitung, Bewertung, Veröffentlichung

| Nr. | Anforderung | Stand |
|---|---|---|
| FA-200 | Prüfungsliste aus dem Hochschulsystem als CSV hochladen (Spalten `pruefer, exam_name, pruefungsname, pruefungsnummer, row, matriculation_number1, first_name1 (= Nachname), first_name, hochschulmail`; UTF-8, Komma). Erneutes Hochladen gleicht ab (Nachmeldungen, Abmeldungen), statt zu duplizieren. | umgesetzt |
| FA-201 | Die Klausur trägt Prüfungsnummer (aus der Liste) und Termin; Zuordnung zum Katalog über die Prüfungsnummer folgt mit der echten Katalogquelle. | teilweise |
| FA-202 | Jede Klausur erhält eine eindeutige, nicht sprechende Klausur-ID (z. B. `RT-26S-4K7Q`, mit Prüfzeichen gegen Tippfehler), die auf dem Deckblatt steht und in der Veröffentlichung statt Namen/Matrikelnummer erscheint. | umgesetzt |
| FA-203 | Räume mit Platzzahl (und optional Sitzplan: Reihen, Plätze je Reihe, „jeder zweite Platz“) werden gepflegt; je Klausur werden Räume gewählt, die Teilnehmenden verteilt (alphabetisch oder zufällig) und erhalten Raum + Sitzplatz. Reicht die Kapazität nicht, wird das begründet angezeigt. | umgesetzt |
| FA-204 | Ausgabe für den Klausurdruck: CSV-Datenquelle für den Word-Serienbrief (Semikolon, UTF-8 mit BOM; Felder KlausurID, Nachname, Vorname, Matrikelnummer, EMail, Raum, Raumbezeichnung, Platz, Klausur, Pruefungsnummer, Datum, Uhrzeit, Pruefer, Listennummer), Sitzlisten je Raum (PDF, mit Unterschriftsspalte) und Aushang (Namensbereiche je Raum bzw. bei zufälliger Verteilung Name → Raum/Platz). | umgesetzt |
| FA-205 | Aufgaben mit Höchstpunktzahl festlegen (inkl. Sonderpunkte); Notenschlüssel festlegen (Grenzen je Note). Standard: ab 0 → 5,0; 49,5 → 4,0; 54,5 → 3,7; 59,5 → 3,3; 64,5 → 3,0; 69,5 → 2,7; 74,5 → 2,3; 79,5 → 2,0; 84,5 → 1,7; 89,5 → 1,3; 94,5 → 1,0 (bezogen auf 100 Punkte), je Klausur editierbar und auf andere Höchstpunktzahlen umrechenbar (halbe Punkte). | umgesetzt |
| FA-206 | Punkte je Aufgabe eintragen (Tastatureingabe, Suche per Klausur-ID tolerant gegen O/0, I/1), Anwesenheit vermerken, Rest als nicht erschienen; Summe und Note werden berechnet. QR-Scan folgt. | umgesetzt |
| FA-207 | Veröffentlichung mit Statistik wie die bisherige Excel-Vorlage: Anwesende, aktiv Teilnehmende (> Schwelle, z. B. 20 Punkte), Durchfallquote, Durchschnittsnote, Max-/Minimalpunktzahl, Anzahl je Note, Notenverteilung als Diagramm; Ergebnisliste nur mit Klausur-ID. Ausgabe als PDF und XLSX. | umgesetzt |
| FA-208 | Export der Noten für die Eintragung im Hochschulsystem (Matrikelnummer, Note) als CSV. | umgesetzt |
| FA-209 | Grundriss-Editor je Raum: Raster (Spalten × Zeilen), Tafel, Projektion, Türen, Fenster, Pult als Rechtecke; Tischmatrix (Reihen, Tische je Reihe, Lücken, Beginn) anlegen, einzelne Tische an- und abschalten, Radierer. Die Platzzahl ergibt sich aus den Tischen. | umgesetzt |
| FA-210 | Tischnummern automatisch nach wählbarem Muster: Startecke (oben/unten, links/rechts), reihen- oder spaltenweise, wahlweise Schlangenlinie. | umgesetzt |
| FA-211 | Verteilungsoptimiert: Ist ein Raum nicht voll, werden die belegten Plätze gleichmäßig über die Nummernfolge gestreut (größtmögliche Lücken). Der Serienbrief ist zum Austeilen sortiert: Raum für Raum, darin nach Tischnummer, mit den Spalten Platz, Reihe und PlatzInReihe. | umgesetzt |
| FA-212 | Sitzplan je Raum in der Klausur (belegte und freie Tische, Klausur-ID und Name beim Darüberfahren). | umgesetzt |
