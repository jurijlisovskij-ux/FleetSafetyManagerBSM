[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EncodedOfficeUrl
)

$ErrorActionPreference = "Stop"
$installRoot = Join-Path $env:LOCALAPPDATA "FleetSafetyManager"
$currentFile = Join-Path $installRoot "current.txt"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Right-click "Connect Ship to FSM Pilot.cmd" and select "Run as administrator".'
    }
}

Assert-Administrator
if (-not (Test-Path $currentFile)) {
    throw "Fleet Safety Manager Ship is not installed for this Windows user. Install Ship Setup first."
}

$relativeRelease = (Get-Content $currentFile -Raw).Trim()
$releasePath = if ([IO.Path]::IsPathRooted($relativeRelease)) {
    $relativeRelease
} else {
    Join-Path $installRoot $relativeRelease
}
$configPath = Join-Path $releasePath "appsettings.json"
if (-not (Test-Path $configPath)) {
    throw "The active Fleet Safety Manager release is incomplete: appsettings.json is missing."
}

$officeUrl = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String($EncodedOfficeUrl)).TrimEnd('/')
if ($officeUrl -notmatch '^https://[a-z0-9-]+\.trycloudflare\.com$') {
    throw "The pilot connection package contains an invalid temporary address."
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json
if ($config.Application.Edition -ne "Ship") {
    throw "This connection package can only be applied to Fleet Safety Manager Ship."
}

$backupPath = "$configPath.before-remote-pilot.bak"
Copy-Item $configPath $backupPath -Force
$config.Synchronization.OfficeBaseUrl = $officeUrl
$config.Synchronization.AllowInsecureHttp = $false
$config | ConvertTo-Json -Depth 20 | Set-Content $configPath -Encoding UTF8

Stop-Service -Name "FleetSafetyManager" -Force -ErrorAction SilentlyContinue
Start-Service -Name "FleetSafetyManager"

$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Milliseconds 500
    try {
        $state = Invoke-RestMethod `
            -Uri "http://127.0.0.1:8080/api/synchronization/activation/vessels" `
            -TimeoutSec 5
        if ($null -ne $state) {
            Write-Host ""
            Write-Host "Ship is connected to the FSM Remote Pilot." -ForegroundColor Green
            Write-Host "Open Fleet Safety Manager Ship and complete vessel activation."
            exit 0
        }
    }
    catch { }
} while ((Get-Date) -lt $deadline)

throw "Ship configuration was saved, but the local FSM service did not become ready. Restart Windows and try again."

