<#
.SYNOPSIS
    Deploy-Kit (KIT-DATEI): Kit-Dateien dieses Repositorys mit einem anderen vergleichen bzw. von dort übernehmen. Die Kit-Dateien sind
    in ProfOMat, FraktionPerfekt, VibeVHS und SchulPerfekt byte-gleich; eigene Dateien je System (konfiguration.env, umgebung.kit,
    umgebung.beispiel, nginx/locations.conf und weitere Schritte) bleiben unberührt.
.EXAMPLE
    .\deploy\server\kit-abgleich.ps1                                   # Vergleich mit allen Geschwister-Repositories
.EXAMPLE
    .\deploy\server\kit-abgleich.ps1 -Quelle C:\repos\profomat -Uebernehmen   # Kit aus ProfOMat in dieses Repository übernehmen
#>
param(
    # Repository, mit dem verglichen bzw. aus dem übernommen wird (Standard: alle vier unter C:\repos, soweit vorhanden)
    [string[]] $Quelle = @(),
    # abweichende Kit-Dateien aus der (einzigen) Quelle hierher kopieren
    [switch] $Uebernehmen
)
$ErrorActionPreference = "Stop"

# Pfade relativ zu deploy\ – die einzige Liste der Kit-Dateien
$kit = @(
    "server\README.md", "server\deploy.ps1", "server\kit-abgleich.ps1", "server\.gitattributes",
    "server\schritte\ausrollen.sh", "server\schritte\beobachten.sh", "server\schritte\datenbank.sh", "server\schritte\einrichten.sh",
    "server\schritte\einrichten-zurueck.sh", "server\schritte\gemeinsam.sh", "server\schritte\konfig-abgleich.sh",
    "server\schritte\liebler-dotnet.sh", "server\schritte\liebler-nginx.sh", "server\schritte\postgresql.sh",
    "server\schritte\postgresql-zurueck.sh", "server\schritte\pruefen.sh",
    "server\systemd\app.service", "server\systemd\sicherung.service", "server\systemd\sicherung.timer",
    "server\nginx\proxy.conf", "server\docker\Dockerfile", "server\docker\compose.yml",
    "server\werkzeuge\sicherung.sh", "server\werkzeuge\pruefer-konto.sh",
    "test\simulation.sh", "test\fake-app.py", "test\.gitattributes",
    "test\stubs\docker", "test\stubs\journalctl", "test\stubs\nginx", "test\stubs\systemctl", "test\stubs\systemd-analyze",
    "test\stubs\systemd-run", "test\stubs\timedatectl"
)
$hier = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$eigenesRepo = Split-Path $hier
if (-not $Quelle) {
    $Quelle = "profomat", "fraktionperfekt", "vibevhs", "schulperfekt" |
        ForEach-Object { Join-Path (Split-Path $eigenesRepo) $_ } |
        Where-Object { (Test-Path (Join-Path $_ "deploy\server\konfiguration.env")) -and ((Resolve-Path $_).Path -ne $eigenesRepo) }
}
if ($Uebernehmen -and $Quelle.Count -ne 1) { throw "-Uebernehmen braucht genau eine -Quelle." }

function Inhalt([string] $pfad) { if (Test-Path $pfad) { (Get-FileHash $pfad -Algorithm SHA256).Hash } else { "" } }

$abweichungen = 0
foreach ($q in $Quelle) {
    $qDeploy = Join-Path $q "deploy"
    Write-Host ("== {0}" -f $q) -ForegroundColor Cyan
    foreach ($datei in $kit) {
        $a = Inhalt (Join-Path $hier $datei); $b = Inhalt (Join-Path $qDeploy $datei)
        if ($a -eq $b) { continue }
        $abweichungen++
        $zustand = if (-not $b) { "fehlt dort" } elseif (-not $a) { "fehlt hier" } else { "abweichend" }
        Write-Host ("   {0,-12} {1}" -f $zustand, $datei) -ForegroundColor Yellow
        if ($Uebernehmen -and $b) {
            $ziel = Join-Path $hier $datei
            New-Item -ItemType Directory -Force (Split-Path $ziel) | Out-Null
            Copy-Item (Join-Path $qDeploy $datei) $ziel -Force
            Write-Host "      übernommen" -ForegroundColor Green
        }
    }
}
if ($abweichungen -eq 0) { Write-Host "✓ Kit-Dateien gleich" -ForegroundColor Green }
elseif (-not $Uebernehmen) { Write-Host ("{0} Abweichung(en) – übernehmen mit -Quelle <repo> -Uebernehmen" -f $abweichungen) -ForegroundColor Yellow }
