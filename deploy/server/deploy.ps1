<#
.SYNOPSIS
    ProfOMat auf liebler.iui.hs-osnabrueck.de ausrollen (gleiches Muster wie VibeVHS und FraktionPerfekt): Linux-Build, Kit und
    Archiv hochladen, auf dem Server mit sudo einspielen (schritte/ausrollen.sh: prüfen → sichern → Konfiguration ergänzen →
    umschalten → Start beobachten mit Countdown → bei Fehler automatisch zurück). Übertragen wird NUR das Programm (dotnet publish) –
    Datenbank, Schlüssel (Systemsiegel) und Logs bleiben auf dem Server in /var/lib/profomat bzw. /var/log/profomat.
    Drei Arten (-Modus, Standard aus konfiguration.env):
      framework       nur die App; die ASP.NET-Core-Runtime ist auf dem Server installiert (VibeVHS-Schritt 09)   ~20 MB
      self-contained  App mit eigener Runtime, braucht auf dem Server nichts von .NET                              ~70 MB
      docker          Container: Abbild wird auf dem Server gebaut (Runtime aus mcr.microsoft.com/dotnet/aspnet)
.EXAMPLE
    .\deploy\server\deploy.ps1 -Pruefen      # bauen, hochladen und auf dem Server NUR prüfen (Trockenlauf, ändert nichts)
.EXAMPLE
    .\deploy\server\deploy.ps1 -Modus docker # als Container ausrollen
.EXAMPLE
    .\deploy\server\deploy.ps1 -Zurueck      # letzten Lauf zurücknehmen (Programm, Konfiguration, ggf. Datenbank)
.EXAMPLE
    .\deploy\server\deploy.ps1                # testen, bauen, hochladen, einspielen (fragt nach dem sudo-Passwort)
.EXAMPLE
    .\deploy\server\deploy.ps1 -DbNeu         # wie oben, Datenbank aber beiseitelegen und neu anlegen lassen
                                              # (nötig bei geänderter Schemaversion, solange es keine EF-Migrationen gibt)
.EXAMPLE
    .\deploy\server\deploy.ps1 -NurKit        # nur das Kit nach /tmp/profomat-kit hochladen (für Schritt 01)
.EXAMPLE
    .\deploy\server\deploy.ps1 -NurBauen      # nur bauen (deploy\ausgabe\profomat.tar.gz), keine Verbindung
#>
param(
    [string] $Ziel = "srvadmin@liebler.iui.hs-osnabrueck.de",
    [string] $Schluessel = "$env:USERPROFILE\.ssh\id_ed25519_liebler_iui_hs_osnabrueck",
    [switch] $NurKit,
    [switch] $NurBauen,
    # Paket mit eigener .NET-Runtime (self-contained, ~70 statt ~20 MB) – nur, falls die gemeinsame Runtime fehlt
    [switch] $MitRuntime,
    # Art des Ausrollens (Standard: MODUS in konfiguration.env); -MitRuntime entspricht -Modus self-contained
    [ValidateSet("", "framework", "self-contained", "docker")]
    [string] $Modus = "",
    # Nur prüfen: bauen, hochladen, auf dem Server alle Voraussetzungen prüfen – nichts ändern
    [switch] $Pruefen,
    # Letzten Lauf zurücknehmen (fragt auf dem Server nach)
    [switch] $Zurueck,
    # Sekunden bis zum automatischen Rückweg, wenn die neue Version nicht antwortet (Standard: WARTEZEIT in konfiguration.env)
    [int] $Wartezeit = 0,
    # Datenbank auf dem Server vor dem Start nach /var/lib/profomat/beiseite/ verschieben (nicht löschen); die Anwendung legt sie
    # neu an. Scheitert der Start, kommen vorige Version und vorige Datenbank automatisch zurück.
    [switch] $DbNeu,
    # Rückfrage bei -DbNeu überspringen
    [switch] $OhneRueckfrage,
    # Tests vor dem Bauen überspringen (nur im Notfall)
    [switch] $OhneTests
)

$ErrorActionPreference = "Stop"
$wurzel = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ausgabe = Join-Path $wurzel "deploy\ausgabe"
$sshArgs = @()
if (Test-Path $Schluessel) { $sshArgs = @("-i", $Schluessel) }
# Windows-eigenes tar (bsdtar) verwenden – ein GNU-tar aus Git Bash im PATH deutet „C:\…“ als entfernten Rechner
$tar = Join-Path $env:SystemRoot "System32\tar.exe"
if (-not (Test-Path $tar)) { $tar = "tar" }

# Pfad und Host aus der gemeinsamen Konfiguration (eine Quelle für Skripte, Unit und nginx)
$konfig = @{}
Get-Content (Join-Path $PSScriptRoot "konfiguration.env") |
    ForEach-Object { if ($_ -match '^\s*([A-Z_]+)="([^"]*)"') { $konfig[$Matches[1]] = $Matches[2] } }

function Kit-Hochladen {
    Write-Host "» Kit nach ${Ziel}:/tmp/profomat-kit" -ForegroundColor Cyan
    $kit = Join-Path $ausgabe "profomat-kit.tar.gz"
    New-Item -ItemType Directory -Force $ausgabe | Out-Null
    # tar statt scp -r: Zielordner wird jedes Mal sauber ersetzt; Zeilenenden bleiben LF (.gitattributes)
    & $tar -czf $kit -C (Join-Path $wurzel "deploy\server") .
    if ($LASTEXITCODE -ne 0) { throw "Kit packen fehlgeschlagen." }
    scp @sshArgs $kit "${Ziel}:/tmp/profomat-kit.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw "Kit hochladen fehlgeschlagen." }
    ssh @sshArgs $Ziel "rm -rf /tmp/profomat-kit && mkdir -p /tmp/profomat-kit && tar -xzf /tmp/profomat-kit.tar.gz -C /tmp/profomat-kit && rm /tmp/profomat-kit.tar.gz && chmod +x /tmp/profomat-kit/schritte/*.sh /tmp/profomat-kit/werkzeuge/*.sh"
    if ($LASTEXITCODE -ne 0) { throw "Kit auspacken fehlgeschlagen." }
}

if (-not $Modus) { $Modus = if ($MitRuntime) { "self-contained" } elseif ($konfig["MODUS"]) { $konfig["MODUS"] } else { "framework" } }
if ($MitRuntime -and $Modus -ne "self-contained") { throw "-MitRuntime passt nicht zu -Modus $Modus." }

function Achtung([string[]] $zeilen) {
    $banner = @(
        "   █████   ██████ ██   ██ ████████ ██    ██ ███    ██  ██████ ",
        "  ██   ██ ██      ██   ██    ██    ██    ██ ████   ██ ██      ",
        "  ███████ ██      ███████    ██    ██    ██ ██ ██  ██ ██   ███",
        "  ██   ██ ██      ██   ██    ██    ██    ██ ██  ██ ██ ██    ██",
        "  ██   ██  ██████ ██   ██    ██     ██████  ██   ████  ██████ ")
    Write-Host ""
    $banner | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host ""
    $zeilen | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Red }
    Write-Host ""
}

if ($Zurueck) {
    if ($DbNeu -or $Pruefen -or $NurKit -or $NurBauen) { throw "-Zurueck steht allein." }
    Kit-Hochladen
    ssh @sshArgs -t $Ziel "sudo bash /tmp/profomat-kit/schritte/ausrollen.sh --zurueck"
    if ($LASTEXITCODE -ne 0) { throw "Rückweg fehlgeschlagen oder abgebrochen – Meldung oben." }
    return
}

if ($DbNeu -and ($NurKit -or $NurBauen)) { throw "-DbNeu wirkt nur beim Einspielen, nicht zusammen mit -NurKit oder -NurBauen." }
if ($DbNeu -and -not $Pruefen) {
    Achtung @(
        "-DbNeu: Die vorhandene Datenbank wird gesichert und beiseitegelegt,",
        "aber NICHT in die neue Version übernommen.",
        "Nach dem Start sind in der Anwendung ALLE DATEN WEG",
        "(Konten, Anträge, Klausuren, Finanzen …) – Ersteinrichtung nötig.",
        "Zurück kommen die Daten nur mit  deploy.ps1 -Zurueck  (vorige Version UND vorige Datenbank).")
    if (-not $OhneRueckfrage -and (Read-Host "  Wirklich mit neuer, leerer Datenbank ausrollen? Dann „ja“ eintippen") -ne "ja") {
        Write-Host "Abgebrochen – nichts geändert."; return
    }
}

if ($NurKit) { Kit-Hochladen; return }

# Keine Zeilenenden-Überraschungen auf dem Server: Skripte und Vorlagen müssen LF haben
$crlf = Get-ChildItem (Join-Path $wurzel "deploy\server") -Recurse -File -Include *.sh, *.conf, *.service, *.timer, *.env, *.beispiel, *.yml, Dockerfile |
    Where-Object { [IO.File]::ReadAllText($_.FullName).Contains("`r`n") }
if ($crlf) { throw "CRLF in $($crlf[0].FullName) – Skripte müssen LF haben (deploy\server\.gitattributes)." }

if (-not $OhneTests) {
    Write-Host "» Tests" -ForegroundColor Cyan
    dotnet test (Join-Path $wurzel "ProfOMat.slnx") --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests fehlgeschlagen – nichts ausgerollt." }
}

$eigeneRuntime = if ($Modus -eq "self-contained") { "true" } else { "false" }
Write-Host ("» dotnet publish (linux-x64, Modus {0})" -f $Modus) -ForegroundColor Cyan
$build = Join-Path $ausgabe "profomat"
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
# Eigener Artefakt-Ordner: Die lokal laufende Instanz (bin\) bleibt unberührt
dotnet publish (Join-Path $wurzel "src\ProfOMat.Web") -c Release -r linux-x64 --self-contained $eigeneRuntime -o $build -p:DebugType=None `
    --artifacts-path (Join-Path $ausgabe "artefakte")
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen." }
Remove-Item (Join-Path $build "appsettings.Development.json") -ErrorAction SilentlyContinue

# Schemastand fürs Paket: ausrollen.sh vergleicht ihn VOR jeder Änderung mit der Datenbank auf dem Server
# („Migration vorhanden“ oder nur mit -DbNeu). Läuft mit der lokalen Runtime; die App startet dabei nicht.
$schema = (& dotnet (Join-Path $build "ProfOMat.Web.dll") --schemainfo) -join "`n"
if ($LASTEXITCODE -eq 0 -and $schema -match 'schemaversion=\d+') {
    [IO.File]::WriteAllText((Join-Path $build "schemainfo.txt"), $schema.Trim() + "`n")
    Write-Host ("  Schema: {0}" -f ($schema.Trim() -replace "`n", ", ")) -ForegroundColor DarkGray
} else {
    Write-Host "! schemainfo.txt nicht erzeugt – der Server fragt das Programm selbst bzw. erkennt Schemafehler beim Start." -ForegroundColor Yellow
}

# Sicherheitsnetz: niemals Daten, Datenbanken, Schlüssel oder Logs mit ausliefern
$verboten = Get-ChildItem $build -Recurse -File |
    Where-Object { $_.Name -match '\.(db(-wal|-shm)?|pem|pfx|key)$' -or $_.FullName -match '[\\/](data|logs|schluessel)[\\/]' }
if ($verboten) { throw "Build enthält Daten- oder Schlüsseldateien (z. B. $($verboten[0].FullName)) – abgebrochen." }

$archiv = Join-Path $ausgabe "profomat.tar.gz"
& $tar -czf $archiv -C $ausgabe profomat
if ($LASTEXITCODE -ne 0) { throw "Archiv packen fehlgeschlagen." }
Write-Host ("✓ gebaut: {0} ({1:N1} MB)" -f $archiv, ((Get-Item $archiv).Length / 1MB)) -ForegroundColor Green
if ($NurBauen) { return }

Kit-Hochladen
Write-Host "» Archiv hochladen" -ForegroundColor Cyan
scp @sshArgs $archiv "${Ziel}:/tmp/profomat.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "Archiv hochladen fehlgeschlagen." }
$optionen = @("--modus", $Modus)
if ($DbNeu) { $optionen += "--db-neu" }
if ($Pruefen) { $optionen += "--pruefen" }
if ($Wartezeit -gt 0) { $optionen += @("--wartezeit", $Wartezeit) }
Write-Host ("» {0} (sudo fragt nach dem Passwort von srvadmin)" -f $(if ($Pruefen) { "auf dem Server prüfen" } else { "einspielen" })) -ForegroundColor Cyan
ssh @sshArgs -t $Ziel "sudo bash /tmp/profomat-kit/schritte/ausrollen.sh $($optionen -join ' ') /tmp/profomat.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "Abgebrochen – Meldung oben. Eine vorhandene Version läuft unverändert weiter bzw. wurde wiederhergestellt." }
if ($Pruefen) { Write-Host "✓ Prüfung bestanden – auf dem Server wurde nichts geändert." -ForegroundColor Green; return }
Write-Host ("✓ ausgerollt: https://{0}{1}/" -f $konfig["HOST"], $konfig["PFAD"]) -ForegroundColor Green
Write-Host "  Erstes Ausrollen? Sofort die Anmeldeseite öffnen und das erste Konto anlegen (Ersteinrichtung)." -ForegroundColor Yellow
