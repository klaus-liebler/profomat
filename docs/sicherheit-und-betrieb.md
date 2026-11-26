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
| Anmeldung | Lokale Konten mit PBKDF2 und Anmeldebremse; **kein zweiter Faktor**, Hochschul-SSO (Shibboleth/SAML) geplant. |
| Daten | SQLite-Datei nur für den Dienstbenutzer lesbar; **keine Feldverschlüsselung**, Sicherungen unverschlüsselt. |
| Betrieb | systemd mit Härtung, Sicherung vor jedem Update und täglich, automatischer Rückweg beim Ausrollen; Logs nur lokal. |

## 1. Netz und Erreichbarkeit

| Punkt | Stand | Umsetzung / offen |
|---|---|---|
| Nur intern / VPN | ❌ | Bewusst öffentlich: Studierende unterschreiben ohne Konto über persönliche Links (`deploy/server/nginx/profomat-locations.conf`). Für einen rein internen Betrieb müsste nginx die Verwaltungspfade per `allow`/`deny` auf Hochschulnetz und VPN beschränken; die Studierendenlinks blieben öffentlich. |
| Host-Firewall | ✅ | ufw auf dem gemeinsamen Server: 80 und 443 offen, SSH nur aus dem VPN (VibeVHS-Kit, Schritt 07). |
| Kestrel nur lokal | ✅ | `ASPNETCORE_URLS=http://127.0.0.1:5082` in `deploy/server/systemd/profomat.service`. |
| Datenbank nicht im Netz | ✅ | SQLite-Datei `/var/lib/profomat/profomat.db`, kein Netzwerkdienst. |

## 2. Webserver (nginx)

| Punkt | Stand | Umsetzung / offen |
|---|---|---|
| TLS 1.2/1.3, HTTP → HTTPS | ✅ | Gemeinsames Grundgerüst `liebler.conf` (VibeVHS-Kit), Hochschulzertifikat. |
| HSTS | ⚠️ | In `liebler.conf` noch auskommentiert; die Anwendung setzt HSTS außerhalb der Entwicklung selbst. |
| Security-Header (CSP, X-Frame-Options, nosniff) | ✅ | In der Anwendung (`Sicherheitsheader.cs`): CSP ohne `unsafe-inline` für Skripte, `frame-ancestors 'none'`. nginx setzt zusätzlich nur `nosniff`. |
| `server_tokens off` | ✅ | `liebler.conf`. |
| Rate-Limit | ⚠️ | Nur in der Anwendung (Anmeldebremse je IP und Kennung). Kein `limit_req` in nginx. |
| `client_max_body_size` | ✅ | 10 MB (`profomat-proxy.conf`). |
| IP-Allowlist | ❌ | Nicht gesetzt (siehe Netz). |

## 3. Anwendung (ASP.NET Core)

| Punkt | Stand | Umsetzung / offen |
|---|---|---|
| Rechte (RBAC, Least Privilege) | ✅ | Rechte im Anwendungsdienst, nicht nur in der Oberfläche (CLAUDE.md, `Freigabe`). |
| SQL-Injection | ✅ | EF Core mit Parametern. |
| CSRF | ✅ | Antiforgery in Blazor-Formularen. |
| Eingabeprüfung | ✅ | Serverseitig in den Diensten; Uploads mit Größenlimit. |
| Private Schlüssel der Unterschrift | ✅ | Verlassen den Browser nie (`signatur.js`); der Server sieht nur Signaturen. |

## 4. Anmeldung

| Verfahren | Stand | Umsetzung / offen |
|---|---|---|
| Lokales Konto mit Passwort | ✅ | PBKDF2-HMAC-SHA512, 210 000 Iterationen, mind. 12 Zeichen; wachsende Wartezeit nach 5 Fehlversuchen; Sperren/Rollenwechsel beenden Sitzungen. Einladung und Passwort-Link nur als SHA-256 gespeichert. |
| Zweiter Faktor (TOTP/Passkey) | ❌ | Nicht vorhanden. |
| Hochschul-SSO (Shibboleth, SAML 2.0) | 🔜 | Vorgehen in [architektur.md](spezifikation/architektur.md#anmeldung-über-das-hochschulsystem-hochschule-osnabrück). |
| Mehrere Verfahren je Instanz freischaltbar | 🔜 | Gemeinsamer Baustein für alle vier Systeme in Planung. |
| Testanmeldung | ✅ gesperrt | `ausrollen.sh` bricht ab, wenn Testanmeldung oder Demodaten in `profomat.env` eingeschaltet sind. |
| Ersteinrichtung | ⚠️ | Solange kein Konto existiert, kann jede Person das erste Konto anlegen; `ausrollen.sh` erinnert daran. |

## 5. Daten und Verschlüsselung

| Punkt | Stand | Umsetzung / offen |
|---|---|---|
| Dateirechte | ✅ | `UMask=0027`, Daten nur für den Dienstbenutzer. |
| Feldverschlüsselung | ❌ | Keine Spalte ist verschlüsselt. Kandidaten: Matrikelnummern, Noten, Bemerkungen aus mündlichen Prüfungen. Geplant nach dem SchoolCraft-Muster (AES-GCM, Schlüssel außerhalb der Datenbank). |
| Laufwerksverschlüsselung | ❌ | Nicht durch das Kit eingerichtet (Sache des Servers). |
| Sicherungen verschlüsselt | ❌ | Liegen als Datei in `/var/backups/profomat` (nur root). |
| Systemsiegel- und Cookie-Schlüssel | ✅ | `/var/lib/profomat/schluessel`, nie im Repository, in der Sicherung enthalten. |
| Personendaten in Logs | ✅ | Nur Pseudonyme und Antragsnummern (Regel in CLAUDE.md). |

## 6. Betrieb

| Punkt | Stand | Umsetzung / offen |
|---|---|---|
| Dienst | ✅ | systemd mit `NoNewPrivileges`, `ProtectSystem=strict`, `PrivateTmp` u. a. |
| Ausrollen | ✅ | `deploy/server/deploy.ps1` + `schritte/ausrollen.sh`: Prüfphase ohne Änderung (Programme, Port, Platz, Unit, nginx, Datenbank), Sicherung, nur ergänzende Konfigurationsänderungen (systemd-Drop-in), Startbeobachtung mit Countdown und sofortigem Rückweg bei Absturz oder Fehler im Log, Rückweg von Hand (`-Zurueck`). Drei Arten: framework, self-contained, docker. Docker-Modus bisher nur in der Simulation geprüft, nicht auf einem echten Server. |
| Datenbankschema | ⚠️ | `EnsureCreated` mit Schemaversion und handgeschriebenen Aufwertungen ab Version 7; das Deploy-Skript prüft vorher, ob die vorhandene Datenbank migrierbar ist, sonst nur `-DbNeu` mit roter Warnung. **Keine EF-Migrationen.** |
| Sicherung | ✅ | Vor jedem Update und täglich 02:00 Uhr (Datenbank und Schlüssel). |
| Patch-Management | ⚠️ | .NET-Runtime per Timer (VibeVHS-Kit), OS über Ubuntu-Updates; NuGet-Prüfung `dotnet list package --vulnerable` nur von Hand. |
| Logs | ⚠️ | Serilog in `/var/log/profomat` (30 Tage) und journald. **Keine Anbindung an einen zentralen Log-Server** (syslog/GELF/OTel geplant). |
| Monitoring | ⚠️ | `/profomat/gesund` antwortet „ok“. Keine Metriken (Prometheus/OTel geplant). |

## Nächste Schritte

1. Zweiter Faktor bzw. Hochschul-SSO, wählbar je Instanz.
2. Feldverschlüsselung für Prüfungsdaten.
3. EF-Migrationen statt `-DbNeu`.
4. Log-Versand und Metriken für den Hochschulbetrieb.
5. HSTS und `limit_req` in nginx, verschlüsselte Sicherungen.
