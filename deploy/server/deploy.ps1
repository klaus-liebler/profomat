<#
.SYNOPSIS
    ProfOMat auf liebler.iui.hs-osnabrueck.de ausrollen (gleiches Muster wie VibeVHS und FraktionPerfekt): framework-abhängiger
    Linux-Build (Runtime einmalig auf dem Server, VibeVHS-Schritt 09), Kit und Archiv hochladen, auf dem Server mit sudo einspielen. Übertragen wird NUR das Programm (dotnet publish) –
    Datenbank, Schlüssel (Systemsiegel) und Logs bleiben auf dem Server in /var/lib/profomat bzw. /var/log/profomat.
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

if ($DbNeu -and ($NurKit -or $NurBauen)) { throw "-DbNeu wirkt nur beim Einspielen, nicht zusammen mit -NurKit oder -NurBauen." }
if ($DbNeu -and -not $OhneRueckfrage) {
    Write-Host "! -DbNeu: Die Datenbank auf dem Testsystem wird beiseitegelegt und neu angelegt (danach Ersteinrichtung auf der Anmeldeseite)." -ForegroundColor Yellow
    Write-Host "  Alle dort erfassten Daten sind danach nicht mehr in der Anwendung (Datei bleibt unter /var/lib/profomat/beiseite/)." -ForegroundColor Yellow
    if ((Read-Host "  Fortfahren? (j/N)") -notmatch '^(j|ja|y|yes)$') { Write-Host "Abgebrochen – nichts geändert."; return }
}

if ($NurKit) { Kit-Hochladen; return }

# Keine Zeilenenden-Überraschungen auf dem Server: Skripte und Vorlagen müssen LF haben
$crlf = Get-ChildItem (Join-Path $wurzel "deploy\server") -Recurse -File -Include *.sh, *.conf, *.service, *.timer, *.env, *.beispiel |
    Where-Object { [IO.File]::ReadAllText($_.FullName).Contains("`r`n") }
if ($crlf) { throw "CRLF in $($crlf[0].FullName) – Skripte müssen LF haben (deploy\server\.gitattributes)." }

if (-not $OhneTests) {
    Write-Host "» Tests" -ForegroundColor Cyan
    dotnet test (Join-Path $wurzel "ProfOMat.slnx") --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests fehlgeschlagen – nichts ausgerollt." }
}

$eigeneRuntime = if ($MitRuntime) { "true" } else { "false" }
Write-Host ("» dotnet publish (linux-x64, {0})" -f $(if ($MitRuntime) { "mit eigener Runtime" } else { "framework-abhängig" })) -ForegroundColor Cyan
$build = Join-Path $ausgabe "profomat"
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
# Eigener Artefakt-Ordner: Die lokal laufende Instanz (bin\) bleibt unberührt
dotnet publish (Join-Path $wurzel "src\ProfOMat.Web") -c Release -r linux-x64 --self-contained $eigeneRuntime -o $build -p:DebugType=None `
    --artifacts-path (Join-Path $ausgabe "artefakte")
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen." }
Remove-Item (Join-Path $build "appsettings.Development.json") -ErrorAction SilentlyContinue

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
Write-Host "» einspielen (sudo fragt nach dem Passwort von srvadmin)" -ForegroundColor Cyan
$dbNeuArg = if ($DbNeu) { "--db-neu " } else { "" }
ssh @sshArgs -t $Ziel "sudo bash /tmp/profomat-kit/schritte/ausrollen.sh $dbNeuArg/tmp/profomat.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "Einspielen fehlgeschlagen – Meldung oben (eine vorhandene Version läuft unverändert weiter)." }
Write-Host ("✓ ausgerollt: https://{0}{1}/" -f $konfig["HOST"], $konfig["PFAD"]) -ForegroundColor Green
Write-Host "  Erstes Ausrollen? Sofort die Anmeldeseite öffnen und das erste Konto anlegen (Ersteinrichtung)." -ForegroundColor Yellow
