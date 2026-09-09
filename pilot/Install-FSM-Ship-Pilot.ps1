[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EncodedOfficeUrl,
    [Parameter(Mandatory = $true)][guid]$VesselId,
    [Parameter(Mandatory = $true)][string]$EncodedActivationCode
)

$ErrorActionPreference = "Stop"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $arguments = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", ('"{0}"' -f $PSCommandPath),
        "-EncodedOfficeUrl", $EncodedOfficeUrl,
        "-VesselId", $VesselId,
        "-EncodedActivationCode", $EncodedActivationCode
    )
    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}

$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$installer = Join-Path $packageRoot "Install FSM Ship.exe"
if (-not (Test-Path $installer)) {
    throw "Install FSM Ship.exe is missing. Copy the complete vessel package."
}

$installation = Start-Process -FilePath $installer -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"
) -Wait -PassThru
if ($installation.ExitCode -ne 0) {
    throw "Fleet Safety Manager Ship installation failed with exit code $($installation.ExitCode)."
}

$installRoot = Join-Path $env:LOCALAPPDATA "FleetSafetyManager"
$currentFile = Join-Path $installRoot "current.txt"
if (-not (Test-Path $currentFile)) {
    throw "Fleet Safety Manager Ship installation did not create current.txt."
}

$relativeRelease = (Get-Content $currentFile -Raw).Trim()
$releasePath = if ([IO.Path]::IsPathRooted($relativeRelease)) {
    $relativeRelease
} else {
    Join-Path $installRoot $relativeRelease
}
$configPath = Join-Path $releasePath "appsettings.json"
$officeUrl = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String($EncodedOfficeUrl)).TrimEnd('/')
$activationCode = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String($EncodedActivationCode))
if ($officeUrl -notmatch '^https://[a-z0-9-]+\.trycloudflare\.com$') {
    throw "The personalized Ship package contains an invalid temporary address."
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json
if ($config.Application.Edition -ne "Ship") {
    throw "The installed application is not Fleet Safety Manager Ship."
}
$config.Synchronization.OfficeBaseUrl = $officeUrl
$config.Synchronization.AllowInsecureHttp = $false
$config | ConvertTo-Json -Depth 20 | Set-Content $configPath -Encoding UTF8

Stop-Service -Name "FleetSafetyManager" -Force -ErrorAction SilentlyContinue
Start-Service -Name "FleetSafetyManager"

$deadline = (Get-Date).AddSeconds(45)
do {
    Start-Sleep -Milliseconds 500
    try {
        $state = Invoke-RestMethod -Uri "http://127.0.0.1:8080/api/synchronization/activation/state" -TimeoutSec 5
        if ($null -ne $state) { break }
    } catch { }
} while ((Get-Date) -lt $deadline)
if ($null -eq $state) {
    throw "Fleet Safety Manager Ship did not start. Restart Windows and run this installer again."
}

if ($state.requiresActivation) {
    $body = @{
        vesselId = $VesselId
        activationCode = $activationCode
    } | ConvertTo-Json
    Invoke-RestMethod -Method Post `
        -Uri "http://127.0.0.1:8080/api/synchronization/activation" `
        -ContentType "application/json" -Body $body -TimeoutSec 90 | Out-Null
}

Start-Process (Join-Path $installRoot "FleetSafetyManagerLauncher.exe")
Write-Host ""
Write-Host "Fleet Safety Manager Ship is installed, activated and synchronized." -ForegroundColor Green
Write-Host "You can close this window."
