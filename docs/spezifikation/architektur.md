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
- **Klausur-ID:** zufällig, kurz, mit Prüfzeichen (Crockford-Base32), nicht aus der Matrikelnummer ableitbar. Die Veröffentlichung
  enthält nur sie; die letzten vier Stellen der Matrikelnummer sind weder eindeutig noch ausreichend pseudonym.
- **Räume:** Stammdaten mit Platzzahl oder Grundriss (Raster in Zellen, eine Zelle ≈ ein Tisch; Elemente als Rechtecke, Tische 1 × 1).
  Die Nummerierung wird nicht gespeichert, sondern aus Muster und Tischpositionen berechnet (`Raum.Nummerieren`). Eine Reihe sind
  alle Tische derselben Rasterzeile (bzw. -spalte). Wird der Grundriss nach dem Verteilen geändert, die Plätze neu verteilen.
- **Verteilung:** deterministisch (alphabetisch oder zufällig über Hash), Räume anteilig nach Kapazität; innerhalb des Raums
  `Raum.Streuen`: Person i bekommt den Platz an Position ⌊(i + ½) · Plätze / Personen⌋ der Nummernfolge.
- **Word-Serienbrief:** Der ProfOMat liefert die Datenquelle (XLSX über ClosedXML bzw. CSV), die Word-Vorlage bleibt bei den Lehrenden.
  Optional später ein fertiges Deckblatt-PDF je Klausur mit QR-Code (Klausur-ID) für das schnelle Punkte-Eintragen.
- **Bewertung:** Aufgaben mit Höchstpunkten, Notenschlüssel als Tabelle (Untergrenzen), Berechnung im Fachmodell, Statistik wie in der
  bisherigen Excel-Vorlage; Ausgabe PDF (MigraDoc mit Balkendiagramm) und XLSX.
