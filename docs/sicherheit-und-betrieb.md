# ProfOMat – Sicherheit und Betrieb

> **Für Technik, Datenschutz und IT-Betrieb.** Wer sich nur für die Funktionen interessiert, springt direkt zur
> [README](../README.md#starten) bzw. zu den [Anforderungen](spezifikation/anforderungen.md).

**Diese Seite zeigt den Ist-Stand.** Wie ProfOMat sicher betrieben werden soll, beschreibt das [Zielbild](zielbild-sicherheit-und-betrieb.md).

Stand: 26.11.2026, abgeglichen mit Code und Konfiguration im Repository (nicht mit einem laufenden Server).
✅ umgesetzt · ⚠️ teilweise · ❌ nicht umgesetzt · 🔜 geplant (mit Fundstelle)

## Auf einen Blick

| Ebene | Stand in einem Satz |
|---|---|
| Netz | Öffentlich erreichbar über `https://liebler.iui.hs-osnabrueck.de/profomat/`, kein VPN und keine IP-Beschränkung; SSH nur aus dem VPN. |
| Webserver (nginx) | TLS 1.2/1.3, Umleitung auf HTTPS, `server_tokens off`; HSTS, CSP und Rate-Limits setzt die Anwendung, nicht nginx. |
| Anwendung | Rechte im Dienst, EF Core, Antiforgery, strenge CSP; Kestrel nur auf `127.0.0.1:5082`. |
| Anmeldung | ASP.NET Core Identity: Verfahren je Instanz schaltbar (SAML, OIDC, Passwort + TOTP, Passkey); TOTP Pflicht für Systemverwaltung und Studiendekanat; Sperre nach Fehlversuchen. **SAML an den IdP der Hochschule noch nicht angebunden.** |
| Daten | PostgreSQL 17 nur lokal bzw. im Container-Netz, eigenes Passwort je Instanz; **Feldverschlüsselung** (AES-256-GCM) für Matrikelnummern und Noten mündlicher Prüfungen; Sicherungen mit `age` verschlüsselbar (auf dem Server noch einzurichten). |
| Betrieb | systemd mit Härtung, Sicherung mit Rücksicherungsprobe, automatischer Rückweg; Gesundheitsendpunkte, OpenTelemetry und `/metrics` vorbereitet (Standard aus); Sicherheitsprotokoll und Rolle Revision. |

## 1. Netz und Erreichbarkeit

| Punkt | Stand | Umsetzung / offen |
|---|---|---|
| Nur intern / VPN | ❌ | Bewusst öffentlich: Studierende unterschreiben ohne Konto über persönliche Links (`deploy/server/nginx/profomat-locations.conf`). Für einen rein internen Betrieb müsste nginx die Verwaltungspfade per `allow`/`deny` auf Hochschulnetz und VPN beschränken; die Studierendenlinks blieben öffentlich. |
| Host-Firewall | ✅ | ufw auf dem gemeinsamen Server: 80 und 443 offen, SSH nur aus dem VPN (VibeVHS-Kit, Schritt 07). |
| Kestrel nur lokal | ✅ | `ASPNETCORE_URLS=http://127.0.0.1:5082` in `deploy/server/systemd/profomat.service`. |
| Datenbank nicht im Netz | ✅ | PostgreSQL 17 im eigenen Container `profomat-db`, nur auf `127.0.0.1` (nativ) bzw. im Container-Netz ohne veröffentlichten Port (Docker); Passwort zufällig je Instanz in `/etc/profomat/postgres.env` (0600). Bis 11/2026 SQLite. |

## 2. Webserver (nginx)

| Punkt | Stand | Umsetzung / offen |
|---|---|---|
| TLS 1.2/1.3, HTTP → HTTPS | ✅ | Gemeinsames Grundgerüst `liebler.conf` (VibeVHS-Kit), Hochschulzertifikat. |
| HSTS | ⚠️ | In `liebler.conf` noch auskommentiert; die Anwendung setzt HSTS außerhalb der Entwicklung selbst. |
| Security-Header (CSP, X-Frame-Options, nosniff) | ✅ | In der Anwendung (`Sicherheitsheader.cs`): CSP ohne `unsafe-inline` für Skripte, `frame-ancestors 'none'`. nginx setzt zusätzlich nur `nosniff`. |
| `server_tokens off` | ✅ | `liebler.conf`. |
| Rate-Limit | ⚠️ | Nur in der Anwendung (Rate-Limit je IP für `/anmelden`, `/einladung`, `/Saml2`, `/signin-oidc` und die Studierendenlinks, Anmeldebremse je IP und Kennung, Sperre des Kontos). Kein `limit_req` in nginx. |
| `client_max_body_size` | ✅ | 10 MB (`profomat-proxy.conf`). |
| IP-Allowlist | ⚠️ | In der Anwendung schaltbar (`Sicherheit:VerwaltungNetze`, Studierendenlinks bleiben öffentlich); auf dem Server nicht gesetzt. |

## 3. Anwendung (ASP.NET Core)

| Punkt | Stand | Umsetzung / offen |
|---|---|---|
| Rechte (RBAC, Least Privilege) | ✅ | Rechte im Anwendungsdienst, nicht nur in der Oberfläche (CLAUDE.md, `Freigabe`). |
| SQL-Injection | ✅ | EF Core mit Parametern. |
| CSRF | ✅ | Antiforgery in Blazor-Formularen. |
| Eingabeprüfung | ✅ | Serverseitig in den Diensten; Uploads mit Größenlimit. |
| Private Schlüssel der Unterschrift | ✅ | Verlassen den Browser nie (`signatur.js`); der Server sieht nur Signaturen. |

## 4. Anmeldung

Grundlage ist ASP.NET Core Identity mit eigenem Speicher über das Kontomodell (Ablauf und Fundstellen:
[architektur.md](spezifikation/architektur.md#anmeldung-aspnet-core-identity)).

| Verfahren | Stand | Umsetzung / offen |
|---|---|---|
| Verfahren je Instanz freischaltbar | ✅ | `Anmeldung:Verfahren` (Saml, Oidc, Passwort, Passkey); getrennte Kacheln auf `/anmelden`. Unvollständige Konfiguration eines genannten Verfahrens verhindert den Start. Der gemeinsame Baustein für die anderen drei Systeme fehlt noch. |
| Konto mit Passwort | ✅ | PBKDF2-HMAC-SHA512, 210 000 Iterationen (Identity-Format; ältere Hashes gelten weiter und werden erneuert), mind. 12 Zeichen; Einladung und Passwort-Link nur als SHA-256 gespeichert. |
| Sperre nach Fehlversuchen | ✅ | Identity-Sperre: 5 Fehlversuche (Passwort oder Code) → 15 min gesperrt; zusätzlich wachsende Wartezeit je IP und Kennung (Anmeldebremse). |
| Zweiter Faktor (TOTP) | ✅ | Authenticator-App, Pflicht für Systemverwaltung und Studiendekanat (Einrichtung erzwungen bei der ersten Anmeldung), freiwillig für andere; Geheimnis mit Data Protection verschlüsselt, Wiederherstellungscodes nur als Hash. Die Systemverwaltung kann zurücksetzen. |
| Passkey (WebAuthn) | ✅ | Identity-Passkeys, Nutzerprüfung im Gerät Pflicht (gilt als zweiter Faktor), ohne Inline-JavaScript (`passkey.js`). Die Attestierung wird nicht gegen Herstellerlisten geprüft (Identity-Vorgabe). |
| Hochschul-SSO (Shibboleth, SAML 2.0) | ⚠️ | Im Code fertig (Sustainsys.Saml2, SP-Metadaten unter `/profomat/Saml2`, Zugehörigkeitsfilter, Verknüpfung über subject-id/pairwise-id/eppn, REFEDS-MFA). **Offen:** Registrierung beim IdP der Hochschule und Attributfreigabe; auf dem Server ist SAML deshalb aus. |
| Firmen-/Behördenkonto (OIDC) | ✅ | Code-Flow mit PKCE, MFA über `amr`/`acr`; Client-Geheimnis nur per Umgebung. Nicht gegen einen echten Anbieter geprüft. |
| MFA-Nachweis bei SSO | ✅ | Für Systemverwaltung und Studiendekanat verlangt; fehlt er, fragt ProfOMat den eigenen TOTP-Faktor ab (`Anmeldung:MfaBeiSsoVerlangen`). |
| Sitzungsende | ✅ | Identity-Sicherheitsstempel im Cookie; Prüfung bei jeder Anfrage und für offene Seiten jede Minute. |
| Link per Mail für Externe | ❌ | Nur für Studierende in den Fachabläufen (persönliche Links), nicht als allgemeines Anmeldeverfahren. |
| Testanmeldung | ✅ gesperrt | Nur in der Entwicklungsumgebung wirksam; `ausrollen.sh` bricht zusätzlich ab, wenn Testanmeldung oder Demodaten in `profomat.env` eingeschaltet sind. |
| Ersteinrichtung | ⚠️ | Solange kein Konto existiert, kann jede Person das erste Konto anlegen (danach sofort TOTP-Pflicht); `ausrollen.sh` erinnert daran. |

## 5. Daten und Verschlüsselung

| Punkt | Stand | Umsetzung / offen |
|---|---|---|
| Dateirechte | ✅ | `UMask=0027`, Daten nur für den Dienstbenutzer. |
| Feldverschlüsselung | ✅ | AES-256-GCM mit Schlüsselversion für Matrikelnummern (Drittprüfung, Klausur, Hausarbeit, mündliche Prüfung) sowie Endnote, Abweichungsbegründung und Bemerkungen mündlicher Prüfungen. Signaturtexte und Mails im Postausgang bleiben bewusst im Klartext: Der unterschriebene Inhalt muss unverändert und ohne Schlüssel prüfbar bleiben; geschützt über Laufwerks- und Sicherungsverschlüsselung (Entscheidung 26.11.2026). **Offen:** Schlüssel liegt ohne systemd-Credential im Schlüsselordner (und damit in dessen Sicherung) – auf dem Server als Credential ablegen. |
| Laufwerksverschlüsselung | ❌ | Nicht durch das Kit eingerichtet (Sache des Servers). |
| Sicherungen verschlüsselt | ⚠️ | `sicherung.sh` verschlüsselt mit `age`, sobald `/etc/profomat/sicherung.env` einen öffentlichen Schlüssel nennt; Kopie außer Haus per rsync/rclone. Auf dem Server noch nicht eingerichtet. Sicherungen vor Updates bleiben für den Rückweg unverschlüsselt (0640). |
| Systemsiegel-, Cookie- und SAML-Schlüssel | ✅ | `/var/lib/profomat/schluessel` (auch die Data-Protection-Schlüssel für die TOTP-Geheimnisse und `saml-sp.pfx`), nie im Repository, in der Sicherung enthalten. Geht der Ordner verloren, müssen alle ihren zweiten Faktor neu einrichten. |
| Personendaten in Logs | ✅ | Nur Pseudonyme und Antragsnummern (Regel in CLAUDE.md). |

## 6. Betrieb

| Punkt | Stand | Umsetzung / offen |
|---|---|---|
| Dienst | ✅ | systemd mit `NoNewPrivileges`, `ProtectSystem=strict`, `PrivateTmp` u. a. |
| Ausrollen | ✅ | `deploy/server/deploy.ps1` + `schritte/ausrollen.sh`: Prüfphase ohne Änderung (Programme, Port, Platz, Unit, nginx, Datenbank), Sicherung, nur ergänzende Konfigurationsänderungen (systemd-Drop-in), Startbeobachtung mit Countdown und sofortigem Rückweg bei Absturz oder Fehler im Log, Rückweg von Hand (`-Zurueck`). Drei Arten: framework, self-contained, docker. Docker-Modus bisher nur in der Simulation geprüft, nicht auf einem echten Server. |
| Datenbankschema | ✅ | EF-Core-Migrationen; vor jedem Ausrollen meldet die NEUE Version per `--datenbankstand`, was ein Start täte (neu, aktuell, migrierbar, fremd). Alte SQLite-Daten einmalig per `--sqlite-uebernehmen` mit Zeilen- und Prüfsummenvergleich je Tabelle (Trockenlauf `--probe`). |
| Sicherung | ✅ | Vor jedem Update und täglich 02:00 Uhr (Datenbank und Schlüssel), jedes Mal mit Rücksicherungsprobe (integrity_check, Schema, Tabellen) und Statusdatei. |
| Patch-Management | ⚠️ | .NET-Runtime per Timer (VibeVHS-Kit), OS über Ubuntu-Updates; NuGet-Prüfung `dotnet list package --vulnerable` nur von Hand. |
| Logs | ⚠️ | Serilog in `/var/log/profomat` (30 Tage) und journald, auf Wunsch als JSON (`Betrieb:Logformat=json`); Tokens in Pfaden maskiert. OTLP vorbereitet (`Betrieb:Otlp:Endpunkt`). **Keine Anbindung an einen zentralen Log-Server eingerichtet.** |
| Monitoring | ✅ | `/gesund` (wie bisher), `/gesund/live`, `/gesund/bereit` (Datenbank, Platz, Zertifikatsablauf), `/gesund/version`; Prometheus `/metrics` nur aus `Betrieb:Metriken:Netze` (Standard aus). |

## 7. Prüfungen und Audit

| Punkt | Stand | Umsetzung / offen |
|---|---|---|
| Sicherheitsprotokoll | ✅ | Nur anfügen (Trigger), hash-verkettet, Kettenprüfung auf `/revision`; Anmeldungen, Fehlversuche, Rechte, Exporte, Konfigurationsänderungen. |
| Rolle Revision | ✅ | Nur lesend: Protokoll, Rechte-Matrix, Schalter, Sicherheitsbericht (`/revision/sicherheitsbericht.json`). |
| Serverkonto `pruefer` | ✅ | `deploy/server/werkzeuge/pruefer-konto.sh anlegen <tage> <schluessel.pub>` – befristet, nur lesende sudo-Befehle. |
| security.txt | ⚠️ | Aus `Betrieb:SecurityTxt:Kontakt`; auf dem Server noch nicht gesetzt. |
| Pakete | ✅ | SHA-256 und SBOM (CycloneDX) je Paket aus `deploy.ps1`; `ausrollen.sh` prüft die Prüfsumme. |

## Nächste Schritte

1. SAML beim IdP der Hochschule registrieren (lokaler SP oder DFN-AAI), Attributfreigabe, REFEDS-MFA klären; danach `Anmeldung:Verfahren` auf dem Server erweitern.
2. Auf dem Server einrichten: Feldschlüssel als systemd-Credential, `age`-Sicherung mit Kopie außer Haus, Log-Weiterleitung, `security.txt` (docs/deploy.md, Abschnitt Stufe 2).
3. EF-Migrationen statt `-DbNeu`.
4. Laufwerksverschlüsselung (LUKS bzw. Hypervisor) für den Server klären – sie schützt auch Signaturtexte und Postausgang.
5. HSTS und `limit_req` in nginx, verschlüsselte Sicherungen.
