[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://')]
    [string]$PublicUrl,

    [Parameter(Mandatory = $true)]
    [SecureString]$TunnelToken
)

$ErrorActionPreference = 'Stop'

$appRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$currentFile = Join-Path $appRoot 'current.txt'
if (-not (Test-Path $currentFile)) {
    throw 'Fleet Safety Manager current release was not found.'
}

$releaseValue = (Get-Content $currentFile -Raw).Trim()
$releasePath = if ([System.IO.Path]::IsPathRooted($releaseValue)) {
    $releaseValue
} else {
    Join-Path $appRoot $releaseValue
}
$configPath = Join-Path $releasePath 'appsettings.json'
if (-not (Test-Path $configPath)) {
    throw 'Fleet Safety Manager configuration was not found.'
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json
if ($config.Application.Edition -ne 'Office') {
    throw 'This command can only be run on an Office installation.'
}

$normalizedUrl = $PublicUrl.Trim().TrimEnd('/')
$config.Synchronization.AdvertisedBaseUrl = $normalizedUrl
$config.Synchronization.AllowInsecureHttp = $false
$config | ConvertTo-Json -Depth 20 | Set-Content $configPath -Encoding UTF8

$toolsPath = Join-Path $appRoot 'Tools'
New-Item -ItemType Directory -Path $toolsPath -Force | Out-Null
$cloudflaredPath = Join-Path $toolsPath 'cloudflared.exe'
if (-not (Test-Path $cloudflaredPath)) {
    $downloadUrl = 'https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe'
    Invoke-WebRequest -Uri $downloadUrl -OutFile $cloudflaredPath -UseBasicParsing
}

$tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($TunnelToken)
try {
    $plainToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer)
    & $cloudflaredPath service uninstall 2>$null | Out-Null
    & $cloudflaredPath service install $plainToken
    if ($LASTEXITCODE -ne 0) {
        throw 'Cloudflare Tunnel service installation failed.'
    }
} finally {
    if ($tokenPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer)
    }
    $plainToken = $null
}

Set-Service -Name 'FleetSafetyManager' -StartupType Automatic
Restart-Service -Name 'FleetSafetyManager'
Set-Service -Name 'cloudflared' -StartupType Automatic
Restart-Service -Name 'cloudflared'

$deadline = (Get-Date).AddSeconds(45)
do {
    try {
        $health = Invoke-RestMethod -Uri "$normalizedUrl/api/synchronization/health" -TimeoutSec 10
        if ($health.status -eq 'ok') {
            Write-Host "FSM production synchronization is ready: $normalizedUrl" -ForegroundColor Green
            exit 0
        }
    } catch {
        Start-Sleep -Seconds 3
    }
} while ((Get-Date) -lt $deadline)

throw "The HTTPS endpoint did not become available: $normalizedUrl"
