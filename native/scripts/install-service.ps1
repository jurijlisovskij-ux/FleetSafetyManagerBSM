param(
    [string]$InstallFolder = "C:\FleetSafetyManager",
    [ValidateSet("Office","Ship")]
    [string]$Edition = "Office",
    [string]$ShipVesselId = ""
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run as Administrator."
}

$Source = $PSScriptRoot
New-Item -ItemType Directory -Force -Path $InstallFolder | Out-Null
Copy-Item "$Source\*" $InstallFolder -Recurse -Force

$configPath = Join-Path $InstallFolder "appsettings.json"
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$config.Application.Edition = $Edition
$config.Application.ShipVesselId = $ShipVesselId
$config | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8

$exe = Join-Path $InstallFolder "FSM.Api.exe"

if (Get-Service "FleetSafetyManager" -ErrorAction SilentlyContinue) {
    Stop-Service "FleetSafetyManager" -Force -ErrorAction SilentlyContinue
    sc.exe delete "FleetSafetyManager" | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create "FleetSafetyManager" `
    binPath= "`"$exe`" --urls http://0.0.0.0:8080" `
    start= auto DisplayName= "Fleet Safety Manager" | Out-Null

netsh advfirewall firewall delete rule name="Fleet Safety Manager" | Out-Null
netsh advfirewall firewall add rule name="Fleet Safety Manager" `
    dir=in action=allow protocol=TCP localport=8080 | Out-Null

Start-Service "FleetSafetyManager"
Write-Host "Installed. Open http://localhost:8080"
