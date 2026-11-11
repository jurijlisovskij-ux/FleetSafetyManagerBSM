[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$pilotRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = Split-Path -Parent $pilotRoot
$cloudflared = Join-Path $pilotRoot "cloudflared.exe"
$stateRoot = Join-Path $env:LOCALAPPDATA "FleetSafetyManager\RemotePilot"
$logPath = Join-Path $stateRoot "cloudflared.log"
$pidPath = Join-Path $stateRoot "cloudflared.pid"
$testConnectionUrlPath = Join-Path $env:LOCALAPPDATA `
    "FleetSafetyManager\FSM-Test-Connection-URL.txt"

function Stop-PreviousTunnel {
    if (-not (Test-Path $pidPath)) { return }
    $oldPid = (Get-Content $pidPath -Raw -ErrorAction SilentlyContinue).Trim()
    if ($oldPid -match '^\d+$') {
        Stop-Process -Id ([int]$oldPid) -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $pidPath -Force -ErrorAction SilentlyContinue
}

function Get-ActiveTunnelUrl {
    if (-not (Test-Path $pidPath) -or -not (Test-Path $logPath)) {
        return $null
    }
    $existingPid = (Get-Content $pidPath -Raw -ErrorAction SilentlyContinue).Trim()
    if ($existingPid -notmatch '^\d+$' -or
        -not (Get-Process -Id ([int]$existingPid) -ErrorAction SilentlyContinue)) {
        return $null
    }
    $match = Select-String -Path $logPath `
        -Pattern 'https://[a-z0-9-]+\.trycloudflare\.com' `
        -AllMatches | Select-Object -Last 1
    if ($match) {
        return $match.Matches[0].Value.TrimEnd('/')
    }
    return $null
}

function Get-OfficeCatalog {
    try {
        return Invoke-RestMethod `
            -Uri "http://127.0.0.1:8080/api/synchronization/activation/catalog" `
            -TimeoutSec 10
    }
    catch { return $null }
}

function Get-ConfiguredTestAddress {
    if (-not (Test-Path $testConnectionUrlPath)) { return $null }
    $candidate = (Get-Content $testConnectionUrlPath -Raw `
        -ErrorAction SilentlyContinue).Trim().TrimEnd('/')
    if ($candidate -notmatch '^https://') { return $null }
    try {
        $health = Invoke-RestMethod `
            -Uri "$candidate/api/synchronization/health" `
            -TimeoutSec 15
        if ($health.databaseAvailable -and $health.synchronizationAvailable) {
            return $candidate
        }
    }
    catch { return $null }
    return $null
}

if (-not (Test-Path $cloudflared)) {
    throw "cloudflared.exe is missing. Extract the complete Remote Pilot package before starting it."
}
$catalog = Get-OfficeCatalog
if ($null -eq $catalog) {
    throw "Fleet Safety Manager Office is not running on this PC. Install and start Office Setup first."
}

$vessels = @($catalog.vessels)
if ($vessels.Count -eq 0) {
    throw "No vessels are available in Fleet Safety Manager Office."
}

Write-Host ""
Write-Host "Select the vessel for this Ship installer:" -ForegroundColor Cyan
for ($index = 0; $index -lt $vessels.Count; $index++) {
    Write-Host ("{0}. {1} (IMO {2})" -f ($index + 1), $vessels[$index].vesselName, $vessels[$index].imoNumber)
}

if ($vessels.Count -eq 1) {
    $selectedVessel = $vessels[0]
} else {
    $selectedNumber = 0
    do {
        $selection = Read-Host "Enter vessel number"
        $validSelection = [int]::TryParse($selection, [ref]$selectedNumber) -and
            $selectedNumber -ge 1 -and $selectedNumber -le $vessels.Count
    } while (-not $validSelection)
    $selectedVessel = $vessels[$selectedNumber - 1]
}

New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$publicUrl = Get-ConfiguredTestAddress
if ($publicUrl) {
    Write-Host ""
    Write-Host "Using the configured secure test address." -ForegroundColor Green
}
if (-not $publicUrl) {
    $publicUrl = Get-ActiveTunnelUrl
}
if ($publicUrl) {
    Write-Host ""
    Write-Host "Reusing the active secure Internet connection." -ForegroundColor Green
}
else {
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

    Write-Host ""
    Write-Host "Creating secure Internet connection. Please wait..." -ForegroundColor Yellow
    $deadline = (Get-Date).AddSeconds(60)
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
}

$encodedUrl = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($publicUrl))
$activation = Invoke-RestMethod -Method Post `
    -Uri ("http://127.0.0.1:8080/api/synchronization/activation/pilot-code/{0}" -f $selectedVessel.vesselId) `
    -TimeoutSec 10
$encodedActivationCode = [Convert]::ToBase64String(
    [Text.Encoding]::UTF8.GetBytes($activation.activationCode))
$safeVesselName = ($selectedVessel.vesselName -replace '[^A-Za-z0-9_-]', '_').Trim('_')
if ([string]::IsNullOrWhiteSpace($safeVesselName)) { $safeVesselName = "Vessel" }
$shipPackage = Join-Path $packageRoot ("FSM_Ship_{0}_Pilot" -f $safeVesselName)
New-Item -ItemType Directory -Path $shipPackage -Force | Out-Null
Copy-Item (Join-Path $pilotRoot "Install-FSM-Ship-Pilot.ps1") $shipPackage -Force
$shipInstaller = Get-ChildItem -Path $packageRoot -Filter "FleetSafetyManager_Ship_Setup*.exe" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $shipInstaller) {
    throw "The Ship installer is missing from the Remote Pilot package."
}
Copy-Item $shipInstaller.FullName (Join-Path $shipPackage "Install FSM Ship.exe") -Force

$launcher = @"
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-FSM-Ship-Pilot.ps1" -EncodedOfficeUrl "$encodedUrl" -VesselId "$($selectedVessel.vesselId)" -EncodedActivationCode "$encodedActivationCode"
if errorlevel 1 pause
"@
Set-Content -Path (Join-Path $shipPackage "Install FSM Ship Pilot.cmd") `
    -Value $launcher -Encoding ASCII
Set-Content -Path (Join-Path $shipPackage "TEMPORARY_ADDRESS.txt") `
    -Value $publicUrl -Encoding UTF8

$instructions = @"
FSM REMOTE PILOT

Vessel: $($selectedVessel.vesselName)
IMO: $($selectedVessel.imoNumber)

1. Copy this entire folder to the remote Ship PC.
2. Double-click "Install FSM Ship Pilot.cmd".
3. Approve the Windows administrator prompt.

Installation, activation and first synchronization are automatic.

Do not close or switch off the Office PC during the test.
The temporary address stops working when this tunnel is stopped.
"@
Set-Content -Path (Join-Path $shipPackage "HOW_TO_CONNECT_SHIP.txt") `
    -Value $instructions -Encoding UTF8

Write-Host ""
Write-Host "FSM Remote Pilot is ready." -ForegroundColor Green
Write-Host "Temporary address: $publicUrl"
Write-Host "Vessel: $($selectedVessel.vesselName) (IMO $($selectedVessel.imoNumber))"
Write-Host "Ship connection folder: $shipPackage"
Write-Host ""
Write-Host "Keep this Office PC switched on during the test."
Write-Host "Run this file again to create another vessel package using the same address."
Write-Host "Use Stop-FSM-Remote-Pilot.cmd when the test is finished."
Start-Process explorer.exe -ArgumentList $shipPackage
Write-Host ""
Read-Host "The Ship package folder is open. Press Enter to close this window (the tunnel will continue running)"
