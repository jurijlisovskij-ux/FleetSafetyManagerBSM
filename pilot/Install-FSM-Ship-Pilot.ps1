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
    $uacMessage = @"
Fleet Safety Manager Ship requires Windows administrator permission once.

After you press OK, Windows will display a confirmation window.
Select YES to continue the automatic installation.

If this PC uses a standard Windows account, enter the administrator password.
"@
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shell.Popup($uacMessage, 0, "FSM Ship Installation", 64) | Out-Null
    }
    catch {
        Write-Host $uacMessage -ForegroundColor Yellow
    }

    $arguments = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", ('"{0}"' -f $PSCommandPath),
        "-EncodedOfficeUrl", $EncodedOfficeUrl,
        "-VesselId", $VesselId,
        "-EncodedActivationCode", $EncodedActivationCode
    )
    try {
        $elevated = Start-Process powershell.exe -Verb RunAs `
            -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -Wait -PassThru
    }
    catch {
        $wasCancelled = $_.Exception.Message -match 'canceled|cancelled'
        $exception = $_.Exception
        while ($null -ne $exception) {
            if ($exception -is [System.ComponentModel.Win32Exception] -and
                $exception.NativeErrorCode -eq 1223) {
                $wasCancelled = $true
                break
            }
            $exception = $exception.InnerException
        }
        if ($wasCancelled) {
            Write-Host ""
            Write-Host "Installation was not started because Windows administrator approval was cancelled." -ForegroundColor Yellow
            Write-Host 'Run "Install FSM Ship Pilot.cmd" again and select YES in the Windows confirmation window.'
            exit 1223
        }
        throw
    }
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
