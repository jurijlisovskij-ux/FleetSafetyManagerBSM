[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$pilotRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$cloudflared = Join-Path $pilotRoot "cloudflared.exe"
$stateRoot = Join-Path $env:LOCALAPPDATA "FleetSafetyManager\RemotePilot"
$logPath = Join-Path $stateRoot "cloudflared.log"
$pidPath = Join-Path $stateRoot "cloudflared.pid"
$shipPackage = Join-Path ([Environment]::GetFolderPath("Desktop")) "FSM_Ship_Pilot_Connection"

function Stop-PreviousTunnel {
    if (-not (Test-Path $pidPath)) { return }
    $oldPid = (Get-Content $pidPath -Raw -ErrorAction SilentlyContinue).Trim()
    if ($oldPid -match '^\d+$') {
        Stop-Process -Id ([int]$oldPid) -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $pidPath -Force -ErrorAction SilentlyContinue
}

function Test-OfficeService {
    try {
        $catalog = Invoke-RestMethod `
            -Uri "http://127.0.0.1:8080/api/synchronization/activation/catalog" `
            -TimeoutSec 10
        return $null -ne $catalog
    }
    catch { return $false }
}

if (-not (Test-Path $cloudflared)) {
    throw "cloudflared.exe is missing. Extract the complete Remote Pilot package before starting it."
}
if (-not (Test-OfficeService)) {
    throw "Fleet Safety Manager Office is not running on this PC. Install and start Office Setup first."
}

New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
Stop-PreviousTunnel
Remove-Item $logPath -Force -ErrorAction SilentlyContinue

$process = Start-Process -FilePath $cloudflared -ArgumentList @(
    "tunnel",
    "--no-autoupdate",
    "--url", "http://127.0.0.1:8080",
    "--logfile", $logPath,
    "--loglevel", "info"
) -WindowStyle Hidden -PassThru
Set-Content -Path $pidPath -Value $process.Id -Encoding ASCII

$deadline = (Get-Date).AddSeconds(60)
$publicUrl = $null
while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $logPath) {
        $match = Select-String -Path $logPath `
            -Pattern 'https://[a-z0-9-]+\.trycloudflare\.com' `
            -AllMatches | Select-Object -Last 1
        if ($match) {
            $publicUrl = $match.Matches[0].Value.TrimEnd('/')
            break
        }
    }
}

if (-not $publicUrl) {
    Stop-PreviousTunnel
    throw "The protected temporary tunnel could not be created. Check the Internet connection and try again."
}

$encodedUrl = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($publicUrl))
New-Item -ItemType Directory -Path $shipPackage -Force | Out-Null
Copy-Item (Join-Path $pilotRoot "Connect-FSM-Ship-Pilot.ps1") $shipPackage -Force

$launcher = @"
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Connect-FSM-Ship-Pilot.ps1" -EncodedOfficeUrl "$encodedUrl"
if errorlevel 1 pause
"@
Set-Content -Path (Join-Path $shipPackage "Connect Ship to FSM Pilot.cmd") `
    -Value $launcher -Encoding ASCII
Set-Content -Path (Join-Path $shipPackage "TEMPORARY_ADDRESS.txt") `
    -Value $publicUrl -Encoding UTF8

$instructions = @"
FSM REMOTE PILOT

1. Copy this entire folder to the remote Ship PC.
2. Install FleetSafetyManager_Ship_Setup.exe on that PC.
3. Right-click "Connect Ship to FSM Pilot.cmd" and select "Run as administrator".
4. Open Fleet Safety Manager Ship and complete vessel activation.

Do not close or switch off the Office PC during the test.
The temporary address stops working when this tunnel is stopped.
"@
Set-Content -Path (Join-Path $shipPackage "HOW_TO_CONNECT_SHIP.txt") `
    -Value $instructions -Encoding UTF8

Write-Host ""
Write-Host "FSM Remote Pilot is ready." -ForegroundColor Green
Write-Host "Temporary address: $publicUrl"
Write-Host "Ship connection folder: $shipPackage"
Write-Host ""
Write-Host "Keep this Office PC switched on during the test."
Write-Host "Use Stop-FSM-Remote-Pilot.cmd when the test is finished."

