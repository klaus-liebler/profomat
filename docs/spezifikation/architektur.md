# ProfOMat – Architektur und Entscheidungen

## Aufbau (wie VibeVHS)

.NET 11, Blazor Server (SSR + InteractiveServer) mit Radzen, EF Core + SQLite, Serilog, Quartz.NET, PDFsharp/MigraDoc, QRCoder.

```
src/ProfOMat.Domain          Fachmodell: Konto, Katalog (Fakultät, Studiengang, Prüfung), Antrag + Signatur, Postausgang/Dokument/Protokoll
src/ProfOMat.Application     Dienste (KontoDienst, KatalogDienst, DrittpruefungDienst), Ports, Rechte, Signaturtext und -prüfung
src/ProfOMat.Infrastructure  EF Core (EnsureCreated + Schemaversion), Systemsiegel, PDF, SMTP, Quartz-Jobs
src/ProfOMat.Adapters.Mocks  Mock-Hochschulsystem (Katalog), Demodaten
src/ProfOMat.Web             Seiten, Anmeldung (Cookie), signatur.js (WebCrypto, Unterschriftsfeld)
tests/…                      Domain, Application (SQLite in-memory, echtes Siegel/PDF), Signatur (Node), Browser (Headless-Edge über CDP)
```

Interaktive Seiten arbeiten nur über `Vorgang` (eigener DbContext je Aktion). Ids sind UUIDv7, Zeitpunkte werden als UTC-Millisekunden gespeichert.

## Entscheidung: Vorgang mit Signaturkette statt eines PDFs, das reihum signiert wird

**Frage:** Ist es sinnvoll, den kompletten Prozess als PDF abzubilden, das von Erst-, Zweitprüfenden und Studiendekanat signiert wird?

**Entscheidung:** Nein – führend ist der strukturierte Vorgang im ProfOMat. Jede Unterschrift gilt einem kanonischen Text
des vollständigen Stands einschließlich des Hashs der vorherigen Unterschrift (Hash-Kette) und wird vom Server versiegelt.
Das **PDF ist die Ausfertigung am Ende** (für Sekretariat, Akte, Studierende) und kann zusätzlich ein PAdES-Siegel der
Hochschule tragen (FA-132).

Gründe:

1. **Gezeichnete Unterschriften sind keine PDF-Signaturen.** Ein Bild im PDF beweist nichts; mischt man es in ein
   reihum signiertes PDF, muss jede Änderung als inkrementelles Update erfolgen, sonst brechen frühere Signaturen. Das
   ist fehleranfällig und lässt sich in Acrobat für Laien kaum nachvollziehen.
2. **PAdES im Browser mit hochgeladenem Schlüssel** braucht eine PDF- und CMS-Bibliothek im Browser und für jede Person ein
   X.509-Zertifikat aus einer anerkannten PKI. Ohne das zeigt Acrobat ohnehin nur „Gültigkeit unbekannt“. Ein nackter
   Schlüssel reicht dafür nicht, für die Signaturkette im ProfOMat aber schon.
3. **Der Ablauf braucht ohnehin strukturierte Daten**: Status, Aufgabenlisten, Erinnerungen, Termin und Ort, Zuständigkeiten,
   Versand. Ein PDF als führendes Dokument würde das alles doppelt halten.
4. **Die Rechtsfrage löst das Format nicht.** ATPO § 18 Abs. 1 Satz 4 verlangt einen *schriftlichen* Antrag an das
   Studierendensekretariat. Ersatz der Schriftform durch ein elektronisches Dokument verlangt nach § 3a VwVfG (über das
   NVwVfG) grundsätzlich eine qualifizierte elektronische Signatur. Weder eine Schlüsseldatei noch eine gezeichnete
   Unterschrift ist das, auch nicht in einem PDF. Tragfähig wird das Verfahren durch eine Regelung der Hochschule:
   ATPO § 16a Abs. 1 lässt die Studiendekanin bzw. den Studiendekan „nähere Regelungen zur Durchführung des Verfahrens“
   im elektronischen Prüfungsverwaltungssystem erlassen; alternativ eine Änderung der ATPO, etwa „schriftlich oder
   elektronisch über das dafür vorgesehene System“. **Offene Abklärung mit Studierendensekretariat und Justiziariat.**

Mehrwert der gewählten Lösung: Jede Unterschrift ist einzeln nachprüfbar (Text-Hash, Kette, Systemsiegel, bei Schlüsseln
die Signatur selbst). Veränderungen in der Datenbank fallen auf. Der Prüfwert im PDF verbindet Ausfertigung und Vorgang.

## Unterschreiben

- **Text:** `Signaturinhalt` (Format `profomat-antrag-v1`, nie ändern – für Neues `v2`). Mehrzeilige Freitexte werden eingerückt,
  damit niemand Feldzeilen einschmuggeln kann. Zeitpunkte UTC auf Millisekunden.
- **Schlüssel:** `wwwroot/signatur.js` liest PKCS#8 (auch PBES2-verschlüsselt), PKCS#1, SEC1, DER, JWK. Signiert wird per WebCrypto
  mit einer nicht exportierbaren Kopie; an den Server gehen nur Signatur und öffentlicher JWK. Algorithmen: ECDSA P-256/384/521
  (SHA-256/384/512), RSASSA-PKCS1-v1_5 mit SHA-256 (RSA ≥ 2048 Bit). Fingerabdruck nach RFC 7638.
- **Zuordnung zur Person:** Beschäftigte hinterlegen ihren öffentlichen Schlüssel einmal im Profil (Besitznachweis per Signatur). Bei
  Studierenden bindet die bestätigte Hochschul-Mailadresse (später das SSO), ihr Schlüssel ist „nicht hinterlegt“.
- **Gezeichnet:** PNG 900×300 aus dem Canvas, serverseitig auf Format und Größe geprüft; Bild-Hash im Siegel.
- **Systemsiegel:** ECDSA P-256, Schlüssel in `Speicher:Schluessel/systemsiegel.pem` (nie im Repo, gehört in die Sicherung). Alte
  Schlüssel als `systemsiegel-*.pem` daneben legen, dann bleiben alte Siegel prüfbar.
- **Später:** X.509-Zertifikate (Kette gegen eine Hochschul- oder eigene CA prüfen), Ed25519, PAdES-Siegel des Abschluss-PDFs.

## Anmeldung über das Hochschulsystem (Hochschule Osnabrück)

Befund (November 2026):

- Die Hochschule betreibt einen **Shibboleth-IdP** mit der Entity-ID `https://shib-idp.hs-osnabrueck.de/idp/shibboleth`,
  registriert in der **DFN-AAI** (seit 2016) und in eduGAIN, SAML 2.0, Scope `hs-osnabrueck.de`
  ([REFEDS MET](https://met.refeds.org/met/entity/https%3A%2F%2Fshib-idp.hs-osnabrueck.de%2Fidp%2Fshibboleth/?federation=dfn-aai)).
  Stud.IP der Hochschule bietet den Login per Shibboleth bereits an.

Vorgehen:

1. **ProfOMat als SAML-Service-Provider** einrichten (Bibliothek nach Rückfrage, z. B. Sustainsys.Saml2 oder ITfoxtec.Identity.Saml2;
   gleiche Claims wie die lokale Anmeldung, siehe `Web/Sicherheit/Anmeldung.cs`).
2. Mit dem IT-Service der Hochschule (Betreiber des IdP) klären: **lokaler SP** am IdP (einfacher, reicht für einen internen
   Dienst) oder Registrierung in der DFN-AAI über die Hochschule als Föderationsmitglied. Benötigt werden: SP-Metadaten mit
   Zertifikat, Assertion Consumer Service unter der öffentlichen Adresse, Kontakt und Datenschutzerklärung.
3. **Attributfreigabe** beantragen: `eduPersonPrincipalName` bzw. `subject-id` (dauerhafte Kennung), `mail`, `givenName`, `sn`,
   `eduPersonScopedAffiliation` (student/faculty/employee). Für das Vorausfüllen bei Studierenden zusätzlich die **Matrikelnummer**,
   üblich als `schacPersonalUniqueCode` (`urn:schac:personalUniqueCode:de:hs-osnabrueck.de:Matrikelnummer:…`). Das braucht eine
   Begründung (Datenminimierung) und einen Eintrag im Verzeichnis der Verarbeitungstätigkeiten.
4. **Kontoverknüpfung:** beim ersten SSO-Login über die Mailadresse mit dem lokalen Konto verbinden (Herkunft „Hochschule“,
   Kennung = eppn); die Rollen bleiben im ProfOMat. Studierende brauchen weiterhin kein Konto. Mit SSO entfällt für sie die
   Mailbestätigung (FA-102).
5. Keine direkte LDAP-Anmeldung: Dabei liefen die Hochschulpasswörter durch die Anwendung.

## Studiengänge und Prüfungen aus dem Hochschulsystem

Port `IHochschulKatalog` (heute `MockHochschulKatalog`), Abgleich über `KatalogDienst.AbgleichenAsync` (Quartz, nachts 03:30).
Schlüssel: Studiengang mit Prüfungsordnung, Prüfungsnummer. Nichts wird gelöscht (FA-011).

Für die echte Quelle kommt infrage (mit dem IT-Service zu klären, welches Campus-Management-System und welche Wege freigegeben sind):

- **HISinOne-Webservices** (SOAP, rund 76 Dienste, Zugang über ein technisches Konto; Studiengänge, Prüfungsordnungen,
  Prüfungen), falls die Hochschule HISinOne einsetzt.
- **Regelmäßiger Export** (CSV/XML) der Prüfungsverwaltung in ein Austauschverzeichnis. Laut Auskunft gibt es heute nur
  CSV-Exporte von Prüfungslisten (siehe Iteration 2). Ein Katalogimport per CSV-Upload ist deshalb der wahrscheinlichste erste Schritt.

## Klausuren (Iteration 2, Entwurf)

- **Import** der CSV-Prüfungsliste: Spaltenzuordnung über Kopfzeile, Abgleich je Matrikelnummer (neu, unverändert, entfallen = nicht
  mehr angemeldet, bleibt sichtbar). Achtung: In der Datei ist `first_name1` der Nachname.
- **Schlüssel (FA-202):** die letzten vier Stellen der Matrikelnummer, bei gleichen Endziffern in der Klausur so viele Stellen wie nötig
  (`Klausur.SchluesselVergeben`, nach jedem Abgleich neu). Entscheidung von Klaus (24.11.2026) statt der früheren zufälligen Klausur-ID:
  vertraut aus der Excel-Vorlage, für Studierende ohne Deckblatt nachvollziehbar. Bewusst in Kauf genommen: Die Endziffern sind nur
  schwach pseudonym (wer die Matrikelnummer kennt, erkennt die Person); deshalb enthält die Veröffentlichung keine Nicht-Anwesenden.
- **Räume:** Stammdaten mit Platzzahl oder Grundriss (Raster in Zellen, eine Zelle ≈ ein Tisch; Elemente als Rechtecke, Tische 1 × 1).
  Die Nummerierung wird nicht gespeichert, sondern aus Muster und Tischpositionen berechnet (`Raum.Nummerieren`). Eine Reihe sind
  alle Tische derselben Rasterzeile (bzw. -spalte). Wird der Grundriss nach dem Verteilen geändert, die Plätze neu verteilen.
- **Verteilung:** deterministisch (alphabetisch oder zufällig über Hash), Räume anteilig nach Kapazität; innerhalb des Raums
  `Raum.Streuen`: Person i bekommt den Platz an Position ⌊(i + ½) · Plätze / Personen⌋ der Nummernfolge.
- **Word-Serienbrief:** Der ProfOMat liefert die Datenquelle (XLSX über ClosedXML bzw. CSV), die Word-Vorlage bleibt bei den Lehrenden.
  Optional später ein fertiges Deckblatt-PDF je Klausur mit QR-Code (Schlüssel) für das schnelle Punkte-Eintragen.
- **Bewertung:** Aufgaben mit Höchstpunkten, Notenschlüssel als Tabelle (Untergrenzen), Berechnung im Fachmodell, Statistik wie in der
  bisherigen Excel-Vorlage; Ausgabe PDF (MigraDoc mit Balkendiagramm) und XLSX.

## Finanzen (Iteration 6, Entwurf)

### Regeln der Fakultät IuI (Quellen: Haushaltsinfos 2026, Budgetierungskonzept 2004, FR-Beschlüsse 2017/2020, Budgetierungsrichtlinie 2026)

- **Sachmittelbudget der Laborkostenstelle** (im Januar verabschiedet, als „Budget Sachmittel Fak“, Kostenart 930300, zum 01.01. gebucht):
  Vorjahresdaten mit 12,50 € je Studierendem und anrechenbarer SWS (Praktika, Projekte, Studienarbeiten; gezählt werden Anmeldungen
  in Campusnet, nicht bestandene Leistungen; welche Veranstaltung mit wie vielen SWS anrechenbar ist, regelt die FR-Liste
  „Budgetierungsfähige Lehrveranstaltungen“), 200 € je verfallener SWS (höchstens 800 € je Semester) und je nicht anrechenbarer SWS
  aus Abschlussarbeiten, dazu personenbezogen 1.250 € je Prof./LfbA (nach Stellenhülsen) und 800 € je WM/MTV/Doktorand/in.
  Frei verwendbar; der Rest wird vorgetragen (930601 „Budget aus Vorjahr“ bzw. 930600 „Saldo aus Vorjahren“, gebucht im Februar bis Juni).
- **Investitionen:** Volumen und Aufteilung nach Stellenhülsen im Januar, Anträge der Laborbereiche (Bagatellgrenze 2.000 €), Beschluss
  im Fakultätsrat (März bis Mai). Jede Maßnahme hat einen eigenen Innenauftrag (71…), zweckgebunden; Bestellschluss 31.08. des
  Folgejahres. Reste gehen nach Abschluss an den Sammelauftrag der Fakultät zurück.
- **Studienqualitätsmittel:** Anträge im Oktober für SoSe und WiSe des Folgejahres (Bagatellgrenze 2.000 €), Bewilligung durch das Präsidium
  im Dezember/Januar, Mittelfreigabe 01.03., Verausgabung bis 31.01. des Folgejahres (Stichtag: Rechnungsabgabe). Projektbezogen abzurechnen,
  keine Verrechnung innerhalb des Bereichs; deshalb ist **jeder Antrag ein eigener Topf**. Kategorien laut Antragsformular (1 Fakultäts-Overhead
  … 8 Internationales); Personalkosten nach den „Verrechnungswerten“ bzw. der „Haushaltsbelastung“ je Stunde.

### Modell

- `Laborteam` (Name, Mitglieder = Konten) – Zugriff ausschließlich über die Mitgliedschaft (`FinanzDienst` prüft jede Aktion).
- `Topf` (Art Kostenstelle | Investition | Sqm, Kennung = KST-Nummer bzw. Antragsnummer, Innenauftrag, Bezeichnung, bewilligt
  Personal/Sach, Kategorie, Laufzeit von/bis, Bestellschluss, Rechnungsschluss, verantwortlich, archiviert).
- `Buchung` (Topf, Jahr, Erfasst, Belegdatum, Buchungsdatum, Kostenart, Kostenartbezeichnung, Betrag, Text, Belegnummer) – nur
  Kostenstelle und Investitionen; `Kostenstellenstand` je Topf und Jahr (Stand der Datei, Exportdatum aus dem Dateinamen, Prüfsumme).
- `Massnahmenstand` (Topf, Stichtag, verausgabt, Quelle Excel | KI-JSON | Hand) – für SQM, deren Einzelbuchungen nicht vorliegen.
- `GeplanteAusgabe` mit `Anteil`en je Topf, `Auslagenabrechnung` mit `Auslagenposition`en, `Hilfskraftbeschaeftigung` mit Anteilen,
  `Stundensatz` (Jahr, Art, €/Std).

### Rechenregeln

- **Vorzeichen im SAP-Auszug:** positiv = Ausgabe, negativ = Zugang (Budget, Vortrag, Zuweisung, Gutschrift, Erlös). Rest = −Σ Beträge.
- **Verfügbar** = −Σ Zugänge des Jahres (bei fehlendem Vortrag zusätzlich „Vortrag erwartet“ = Rest des Vorjahres, gekennzeichnet).
  **Ausgegeben** = Σ Ausgaben. **Gebunden** = offene geplante Ausgaben + offene/eingereichte Auslagen + Hilfskraft-Monate nach dem
  letzten Buchungsmonat des Ist-Stands. **Frei** = Verfügbar − Ausgegeben − Gebunden.
- **Zuordnung zur Laufzeit nach Belegdatum**, nicht nach Buchungsdatum: SQM-Rechnungen bis 31.01. werden oft erst im Februar/März gebucht.
- **Ersetzen statt Abgleichen:** Der SAP-Auszug hat keinen eindeutigen Schlüssel (Belegnummern fehlen oder gelten für mehrere Positionen,
  Stornos und Neubuchungen). Weil jede Datei den ganzen Jahresstand enthält, ersetzt der neueste Stand das Jahr. Verknüpfungen geplanter
  Ausgaben mit Buchungen werden über einen Fingerabdruck (Belegdatum, Kostenart, Betrag, Text, Belegnummer, laufende Nummer bei
  Gleichheit) beim Ersetzen übernommen; was nicht mehr passt, wird zur Prüfung markiert.

- **Übertragsgrenze (FA-715):** Grenze = 12 % × „Budget Sachmittel Fak“ des laufenden Jahres. Prognose zum 31.12. = Frei ohne die
  Bindungen, die erst nach dem 31.12. fällig sind. Warnung, wenn die Prognose die Grenze übersteigt; Betrag = Prognose − Grenze.
- **Zeitachse (FA-716):** Monatsraster; jeder Topf ist eine Reihe (Anfangswert = Frei heute + Bindungen, dann Abflüsse im Monat ihrer
  Fälligkeit, Zugänge im Monat ihrer Freigabe). Erwartete Werte sind von gebuchten getrennt (andere Darstellung). Nach dem Laufzeitende
  eines SQM-Topfs wird der Rest als „verfällt“ ausgewiesen und fällt auf 0; bei der Kostenstelle wird zum 01.01. der Übertrag über der
  Grenze als „Einzug möglich“ markiert, aber nicht abgezogen. Darstellung mit `RadzenChart` (kein neues Paket), Tabelle darunter für
  390 px und Barrierefreiheit.
- **Aufteilen (FA-717):** reine Funktion im Fachmodell (`Aufteilung.Vorschlagen(betrag, zweck, monat, töpfe)`), Reihenfolge:
  (1) Mittel mit Verfall vor dem übernächsten Monat bzw. Überhang über der Übertragsgrenze, (2) zweckgebundene Töpfe passenden Zwecks
  mit früherem Laufzeitende zuerst, (3) Laborkostenstelle. Nur Töpfe, deren Laufzeit/Bestellschluss den gewünschten Monat abdeckt.

- **Hilfskräfte (FA-708/709):** Monatskosten = Stunden × Satz des Jahres („Haushaltsbelastung“; eigener Wert des Teams für das Jahr
  oder den letzten früheren, sonst Fakultät 2026) × Anteil. Gebunden sind die Monate nach dem Ist-Stand des Topfs (Kostenstelle:
  Monat der letzten Buchung; Maßnahme: Stichtag des Stands). Der Rechner verteilt ganze Monate, zuerst auf Töpfe, die früher
  auslaufen, die Kostenstelle zuletzt; übernommen wird je Abschnitt eine Beschäftigung zu 100 % aus diesem Topf.
- **Auslagen (FA-707):** binden bis „erstattet“, fällig 30 Tage nach der Einreichung (bzw. ab heute, solange offen).

- **SQM-Excel (FA-704):** Leser in der Infrastruktur (ClosedXML), Kopfzeile mit „Antrag-Nr.“ je Blatt. Übernommen werden nur Anträge, die
  schon Töpfe des Teams sind, oder deren Antragsteller/in (Nachname) zum Team gehört – und diese nur auf Auswahl; alle anderen Zeilen
  werden gezählt, aber weder angezeigt noch gespeichert. Ein neuer Stand entsteht nur bei geändertem Stichtag oder Betrag.

- **Buchungsnotizen (FA-718):** Schlüssel (Topf, Fingerabdruck); Fingerabdruck = Belegdatum, Kostenart, Betrag, Text, Belegnummer. Gleiche Buchungen
  teilen sich eine Notiz. Nach einem Import ohne passende Buchung „verwaist“ die Notiz (bleibt sichtbar, löschbar).
- **Einzelbuchungen (FA-705):** zählen zum Stand einer Maßnahme hinzu, soweit ihr Datum nach dessen Stichtag liegt (frühere sind im Stand enthalten).
- **Budgetprognose (FA-713):** je Kostenstelle und Budgetjahr; die gespeicherte Prognose des Folgejahres ersetzt in der Zeitachse den Vorjahreswert
  des Januar-Budgets. Neue Prognosen übernehmen Personen, Veranstaltungen (Semester weitergezählt) und Sätze aus dem Vorjahr als Vorlage.
- **Erinnerungen (FA-712):** Quartz-Job täglich 7:00 (`Finanzen:ErinnerungCron`), Schlüssel je Frist (Datum + Topf bzw. Text) in `Finanzerinnerungen`.
- **Berufungsmittel:** Auftrag 58216001 (Berufungs- und Bleibemittel, Richtlinie Ziffer 5.4 Abs. 5) fließt über den Vortrag in die Kostenstelle;
  er zählt nicht zur Bezugsgröße der Übertragsgrenze.

### Stand per KI-Chat (FA-703)

Die Investitionsliste gibt es nur als PDF. Statt eines eigenen PDF-Parsers liefert ProfOMat einen Prompt; die Auswertung macht ein
KI-Chat nach Wahl der Lehrenden, ProfOMat prüft nur das Ergebnis. Format (Beträge in Euro als Zahl, Rest positiv = noch verfügbar):

```json
{
  "format": "profomat-investitionen-v1",
  "stichtag": "2026-10-27",
  "massnahmen": [
    {
      "antragsnummer": "AUT-07-26",
      "laborbereich": "Automatisierungstechnik",
      "budget": 3200.00,
      "ausgegeben": 0.00,
      "rest": 3200.00,
      "buchungen": [
        { "datum": "2026-07-17", "kostenart": "980800", "betrag": 7482.36, "text": "Dell/6x PC …" }
      ]
    }
  ]
}
```

Prüfungen: Format und Stichtag, `budget − ausgegeben = rest` (auf 1 ct), Summe der Buchungen = `ausgegeben`; Abweichungen werden
gezeigt, nicht still korrigiert. Im Prompt steht ausdrücklich, dass im PDF negative Salden noch verfügbare Mittel bedeuten und dass die
Budgetzeilen (930300) keine Ausgaben sind.
