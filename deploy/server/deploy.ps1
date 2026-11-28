<#
.SYNOPSIS
    Deploy-Kit (KIT-DATEI, in allen vier Systemen gleich): Anwendung auf einen Linux-Server ausrollen. Werte aus konfiguration.env
    (System, Server, Pfad, Port, Betriebsart). Ablauf: Tests, Linux-Build, Prüfsumme und SBOM, Kit und Archiv hochladen, auf dem Server
    mit sudo einspielen (schritte/ausrollen.sh: prüfen → sichern → Konfiguration ergänzen → umschalten → Start beobachten mit
    Countdown → bei Fehler automatisch zurück). Übertragen wird NUR das Programm – Datenbank, Schlüssel und Logs bleiben auf dem Server.
    Drei Betriebsarten (-Modus, Standard: MODUS in konfiguration.env):
      framework       nur die App; die ASP.NET-Core-Runtime 11 ist auf dem Server installiert     ~20 MB
      self-contained  App mit eigener Runtime, braucht auf dem Server nichts von .NET              ~70 MB
      docker          App-Container; das Abbild wird auf dem Server gebaut (Runtime aus mcr.microsoft.com/dotnet/aspnet)
    Die Datenbank (PostgreSQL 17) läuft in jeder Betriebsart als eigener Container <dienst>-db (Schritt postgresql.sh).
.EXAMPLE
    .\deploy\server\deploy.ps1 -Schritt einrichten   # einmalig: Grundgerüst (Benutzer, Verzeichnisse, Unit, Sicherung, nginx)
.EXAMPLE
    .\deploy\server\deploy.ps1 -Schritt postgresql   # einmalig: Datenbank-Container und Verbindung
.EXAMPLE
    .\deploy\server\deploy.ps1 -Pruefen              # bauen, hochladen und auf dem Server NUR prüfen (Trockenlauf, ändert nichts)
.EXAMPLE
    .\deploy\server\deploy.ps1                        # testen, bauen, hochladen, einspielen (fragt nach dem sudo-Passwort)
.EXAMPLE
    .\deploy\server\deploy.ps1 -SqliteUebernehmen    # einmalig: alte SQLite-Daten nach PostgreSQL übernehmen (mit Probe vorab)
.EXAMPLE
    .\deploy\server\deploy.ps1 -Modus docker         # als Container ausrollen
.EXAMPLE
    .\deploy\server\deploy.ps1 -Zurueck              # letzten Lauf zurücknehmen (Programm, Konfiguration, ggf. Datenbanken)
.EXAMPLE
    .\deploy\server\deploy.ps1 -DbNeu                # Hauptdatenbank beiseitelegen und neu aufbauen lassen (mit roter Warnung)
.EXAMPLE
    .\deploy\server\deploy.ps1 -NurBauen             # nur bauen (deploy\ausgabe\<dienst>.tar.gz), keine Verbindung
#>
param(
    # SSH-Ziel (Standard: ZIEL in konfiguration.env)
    [string] $Ziel = "",
    # privater SSH-Schlüssel (Standard: SSH_SCHLUESSEL in konfiguration.env, Datei in %USERPROFILE%\.ssh)
    [string] $Schluessel = "",
    # einen Kit-Schritt auf dem Server ausführen statt auszurollen (einrichten, postgresql, einrichten-zurueck, postgresql-zurueck)
    [string] $Schritt = "",
    [switch] $NurKit,
    [switch] $NurBauen,
    # Paket mit eigener .NET-Runtime – entspricht -Modus self-contained
    [switch] $MitRuntime,
    [ValidateSet("", "framework", "self-contained", "docker")]
    [string] $Modus = "",
    # Nur prüfen: bauen, hochladen, auf dem Server alle Voraussetzungen und den Datenbankstand prüfen – nichts ändern
    [switch] $Pruefen,
    # Letzten Lauf zurücknehmen (fragt auf dem Server nach)
    [switch] $Zurueck,
    # Laufende Version neu starten (nach Änderungen an /etc/<dienst>/<dienst>.env)
    [switch] $Neustart,
    # Sekunden bis zum automatischen Rückweg, wenn die neue Version nicht antwortet (Standard: WARTEZEIT in konfiguration.env)
    [int] $Wartezeit = 0,
    # Hauptdatenbank beiseitelegen (umbenennen) und neu aufbauen lassen. Scheitert der Start, kommen vorige Version und Datenbank zurück.
    [switch] $DbNeu,
    # Alte SQLite-Datenbank (SQLITE_ALT) einmalig nach PostgreSQL übernehmen – vorher Probe in eine Wegwerf-Datenbank
    [switch] $SqliteUebernehmen,
    # Alte SQLite-Datenbank bewusst liegen lassen und mit leerer Datenbank starten
    [switch] $SqliteIgnorieren,
    # Rückfrage bei -DbNeu überspringen
    [switch] $OhneRueckfrage,
    # Tests vor dem Bauen überspringen (nur im Notfall)
    [switch] $OhneTests
)

$ErrorActionPreference = "Stop"
$wurzel = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ausgabe = Join-Path $wurzel "deploy\ausgabe"
# Windows-eigenes tar (bsdtar) verwenden – ein GNU-tar aus Git Bash im PATH deutet „C:\…“ als entfernten Rechner
$tar = Join-Path $env:SystemRoot "System32\tar.exe"
if (-not (Test-Path $tar)) { $tar = "tar" }

# Eine Quelle für Skripte, Unit, nginx und dieses Skript
$konfig = @{}
[IO.File]::ReadAllLines((Join-Path $PSScriptRoot "konfiguration.env"), [Text.Encoding]::UTF8) |
    ForEach-Object { if ($_ -match '^\s*([A-Z_]+)="([^"]*)"') { $konfig[$Matches[1]] = $Matches[2] } }
foreach ($pflicht in "SYSTEM", "DIENST", "APP", "PROJEKT", "HOST", "PFAD") {
    if (-not $konfig[$pflicht]) { throw "$pflicht fehlt in deploy\server\konfiguration.env." }
}
$dienst = $konfig["DIENST"]
$system = $konfig["SYSTEM"]
if (-not $Ziel) { $Ziel = $konfig["ZIEL"] }
if (-not $Ziel) { throw "Kein SSH-Ziel: -Ziel angeben oder ZIEL in konfiguration.env setzen." }
if (-not $Schluessel -and $konfig["SSH_SCHLUESSEL"]) { $Schluessel = Join-Path $env:USERPROFILE ".ssh\$($konfig["SSH_SCHLUESSEL"])" }
$sshArgs = @()
if ($Schluessel -and (Test-Path $Schluessel)) { $sshArgs = @("-i", $Schluessel) }
$kitOrdner = "/tmp/$dienst-kit"

function Kit-Hochladen {
    Write-Host "» Kit nach ${Ziel}:$kitOrdner" -ForegroundColor Cyan
    # Keine Zeilenenden-Überraschungen auf dem Server: Skripte und Vorlagen müssen LF haben
    $crlf = Get-ChildItem (Join-Path $wurzel "deploy\server") -Recurse -File -Include *.sh, *.conf, *.service, *.timer, *.env, *.beispiel, *.kit, *.yml, Dockerfile |
        Where-Object { [IO.File]::ReadAllText($_.FullName).Contains("`r`n") }
    if ($crlf) { throw "CRLF in $($crlf[0].FullName) – Skripte müssen LF haben (deploy\server\.gitattributes)." }
    $kit = Join-Path $ausgabe "$dienst-kit.tar.gz"
    New-Item -ItemType Directory -Force $ausgabe | Out-Null
    # tar statt scp -r: Zielordner wird jedes Mal sauber ersetzt
    & $tar -czf $kit -C (Join-Path $wurzel "deploy\server") .
    if ($LASTEXITCODE -ne 0) { throw "Kit packen fehlgeschlagen." }
    scp @sshArgs $kit "${Ziel}:/tmp/$dienst-kit.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw "Kit hochladen fehlgeschlagen." }
    ssh @sshArgs $Ziel "rm -rf $kitOrdner && mkdir -p $kitOrdner && tar -xzf /tmp/$dienst-kit.tar.gz -C $kitOrdner && rm /tmp/$dienst-kit.tar.gz && chmod +x $kitOrdner/schritte/*.sh $kitOrdner/werkzeuge/*.sh"
    if ($LASTEXITCODE -ne 0) { throw "Kit auspacken fehlgeschlagen." }
}

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

function Auf-Server([string] $befehl, [string] $was) {
    Write-Host ("» {0} (sudo fragt nach dem Passwort auf {1})" -f $was, $Ziel) -ForegroundColor Cyan
    ssh @sshArgs -t $Ziel "sudo bash $kitOrdner/$befehl"
    return $LASTEXITCODE
}

if (-not $Modus) { $Modus = if ($MitRuntime) { "self-contained" } elseif ($konfig["MODUS"]) { $konfig["MODUS"] } else { "framework" } }
if ($MitRuntime -and $Modus -ne "self-contained") { throw "-MitRuntime passt nicht zu -Modus $Modus." }
if ($SqliteUebernehmen -and ($SqliteIgnorieren -or $DbNeu)) { throw "-SqliteUebernehmen schließt -SqliteIgnorieren und -DbNeu aus." }

if ($Schritt) {
    if ($Schritt -notmatch '^[a-z0-9-]+$' -or -not (Test-Path (Join-Path $PSScriptRoot "schritte\$Schritt.sh"))) { throw "Unbekannter Schritt '$Schritt' (Dateien in deploy\server\schritte)." }
    Kit-Hochladen
    if ((Auf-Server "schritte/$Schritt.sh" "Schritt $Schritt") -ne 0) { throw "Schritt $Schritt fehlgeschlagen – Meldung oben." }
    return
}
if ($Zurueck -or $Neustart) {
    if ($DbNeu -or $Pruefen -or $NurKit -or $NurBauen -or ($Zurueck -and $Neustart)) { throw "-Zurueck bzw. -Neustart steht allein." }
    Kit-Hochladen
    $art = if ($Zurueck) { "--zurueck" } else { "--neustart" }
    if ((Auf-Server "schritte/ausrollen.sh $art" $(if ($Zurueck) { "Rückweg" } else { "Neustart" })) -ne 0) { throw "Abgebrochen – Meldung oben." }
    return
}

if ($DbNeu -and ($NurKit -or $NurBauen)) { throw "-DbNeu wirkt nur beim Einspielen, nicht zusammen mit -NurKit oder -NurBauen." }
if ($DbNeu -and -not $Pruefen) {
    Achtung @(
        "-DbNeu: Die Datenbank wird beiseitegelegt (umbenannt, nicht gelöscht),",
        "aber NICHT in die neue Version übernommen.",
        "Nach dem Start sind in $system ALLE DATEN WEG – die Anwendung baut sie neu auf.",
        "Zurück kommen die Daten nur mit  deploy.ps1 -Zurueck  (vorige Version UND vorige Datenbank).")
    if (-not $OhneRueckfrage -and (Read-Host "  Wirklich mit neuer, leerer Datenbank ausrollen? Dann „ja“ eintippen") -ne "ja") {
        Write-Host "Abgebrochen – nichts geändert."; return
    }
}

if ($NurKit) { Kit-Hochladen; return }

if (-not $OhneTests -and $konfig["LOESUNG"]) {
    Write-Host "» Tests" -ForegroundColor Cyan
    dotnet test (Join-Path $wurzel $konfig["LOESUNG"]) --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests fehlgeschlagen – nichts ausgerollt." }
}

$eigeneRuntime = if ($Modus -eq "self-contained") { "true" } else { "false" }
Write-Host ("» dotnet publish (linux-x64, Modus {0})" -f $Modus) -ForegroundColor Cyan
$build = Join-Path $ausgabe $dienst
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
# Eigener Artefakt-Ordner: Die lokal laufende Instanz (bin\) bleibt unberührt
dotnet publish (Join-Path $wurzel $konfig["PROJEKT"]) -c Release -r linux-x64 --self-contained $eigeneRuntime -o $build -p:DebugType=None `
    --artifacts-path (Join-Path $ausgabe "artefakte")
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen." }
Remove-Item (Join-Path $build "appsettings.Development.json") -ErrorAction SilentlyContinue

# Sicherheitsnetz: niemals Daten, Datenbanken, Schlüssel oder Logs mit ausliefern
$verboten = Get-ChildItem $build -Recurse -File |
    Where-Object { $_.Name -match '\.(db(-wal|-shm)?|pem|pfx|key)$' -or $_.FullName -match '[\\/](data|logs|schluessel)[\\/]' }
if ($verboten) { throw "Build enthält Daten- oder Schlüsseldateien (z. B. $($verboten[0].FullName)) – abgebrochen." }

# Stückliste (SBOM, CycloneDX) je Version: liegt im Paket (sbom.cdx.json) und daneben – für Abhängigkeitsprüfung (z. B. Dependency-Track)
$werkzeuge = Join-Path $wurzel "dotnet-tools.json"
if ((Test-Path $werkzeuge) -and (Get-Content $werkzeuge -Raw) -match 'dotnet-CycloneDX') {
    Write-Host "» SBOM (CycloneDX)" -ForegroundColor Cyan
    $version = (Get-Item (Join-Path $build "$($konfig["APP"]).dll")).VersionInfo.ProductVersion -replace '\+.*$', ''
    dotnet tool restore --tool-manifest $werkzeuge | Out-Null
    $projektDatei = Get-ChildItem (Join-Path $wurzel $konfig["PROJEKT"]) -Filter *.csproj | Select-Object -First 1
    dotnet tool run dotnet-CycloneDX $projektDatei.FullName -o $build -fn "sbom.cdx.json" -F Json -rs -ipr -sn $system -sv $version -dpr | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $build "sbom.cdx.json"))) { throw "SBOM erzeugen fehlgeschlagen." }
    Copy-Item (Join-Path $build "sbom.cdx.json") (Join-Path $ausgabe "$dienst-sbom.cdx.json") -Force
} else {
    Write-Host "! keine SBOM (CycloneDX nicht in dotnet-tools.json)" -ForegroundColor Yellow
}

$archiv = Join-Path $ausgabe "$dienst.tar.gz"
& $tar -czf $archiv -C $ausgabe $dienst
if ($LASTEXITCODE -ne 0) { throw "Archiv packen fehlgeschlagen." }
# Prüfsumme je Paket: ausrollen.sh prüft sie vor jeder Änderung (beschädigt oder unterwegs verändert → Abbruch)
$pruefsumme = (Get-FileHash $archiv -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archiv.sha256", "$pruefsumme  $dienst.tar.gz`n")
Write-Host ("✓ gebaut: {0} ({1:N1} MB), SHA-256 {2}" -f $archiv, ((Get-Item $archiv).Length / 1MB), $pruefsumme) -ForegroundColor Green
if ($NurBauen) { return }

Kit-Hochladen
Write-Host "» Archiv hochladen" -ForegroundColor Cyan
scp @sshArgs $archiv "$archiv.sha256" "${Ziel}:/tmp/"
if ($LASTEXITCODE -ne 0) { throw "Archiv hochladen fehlgeschlagen." }
$optionen = @("--modus", $Modus)
if ($DbNeu) { $optionen += "--db-neu" }
if ($Pruefen) { $optionen += "--pruefen" }
if ($SqliteUebernehmen) { $optionen += "--sqlite-uebernehmen" }
if ($SqliteIgnorieren) { $optionen += "--sqlite-ignorieren" }
if ($Wartezeit -gt 0) { $optionen += @("--wartezeit", $Wartezeit) }
$code = Auf-Server "schritte/ausrollen.sh $($optionen -join ' ') /tmp/$dienst.tar.gz" $(if ($Pruefen) { "auf dem Server prüfen" } else { "einspielen" })
if ($code -ne 0) { throw "Abgebrochen – Meldung oben. Eine vorhandene Version läuft unverändert weiter bzw. wurde wiederhergestellt." }
if ($Pruefen) { Write-Host "✓ Prüfung bestanden – auf dem Server wurde nichts geändert." -ForegroundColor Green; return }
Write-Host ("✓ ausgerollt: https://{0}{1}/" -f $konfig["HOST"], $konfig["PFAD"]) -ForegroundColor Green
