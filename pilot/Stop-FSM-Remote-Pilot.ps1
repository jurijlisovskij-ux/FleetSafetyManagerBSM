$ErrorActionPreference = "Stop"
$stateRoot = Join-Path $env:LOCALAPPDATA "FleetSafetyManager\RemotePilot"
$pidPath = Join-Path $stateRoot "cloudflared.pid"

if (Test-Path $pidPath) {
    $tunnelPid = (Get-Content $pidPath -Raw -ErrorAction SilentlyContinue).Trim()
    if ($tunnelPid -match '^\d+$') {
        Stop-Process -Id ([int]$tunnelPid) -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $pidPath -Force -ErrorAction SilentlyContinue
}

Write-Host "FSM Remote Pilot tunnel is stopped."

