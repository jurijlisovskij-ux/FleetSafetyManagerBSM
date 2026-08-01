$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $Root

Write-Host "[1/4] Building frontend..."
Push-Location (Join-Path $Root "frontend")
try {
    npm ci --registry=https://registry.npmjs.org/
    npm run build
}
finally {
    Pop-Location
}

Write-Host "[2/4] Copying frontend to backend wwwroot..."
$wwwroot = Join-Path $Root "backend\src\FSM.Api\wwwroot"
if (Test-Path $wwwroot) {
    Remove-Item $wwwroot -Recurse -Force
}
New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
Copy-Item (Join-Path $Root "frontend\dist\*") $wwwroot -Recurse -Force

Write-Host "[3/4] Publishing Windows application..."
# The Inno Setup script reads from ..\publish relative to installer,
# therefore the publish directory must be in the repository root.
$publish = Join-Path $Root "publish"
if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}
New-Item -ItemType Directory -Path $publish -Force | Out-Null

dotnet publish (Join-Path $Root "backend\src\FSM.Api\FSM.Api.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $publish

foreach ($folder in @(
    "Data",
    "Documents\reports",
    "Documents\certificates",
    "Backups",
    "Logs"
)) {
    New-Item -ItemType Directory -Force -Path (Join-Path $publish $folder) | Out-Null
}

Copy-Item (Join-Path $Root "native\scripts\install-service.ps1") $publish -Force
Copy-Item (Join-Path $Root "native\scripts\uninstall-service.ps1") $publish -Force
Copy-Item (Join-Path $Root "native\scripts\backup-native.ps1") $publish -Force

Write-Host "[4/4] Application publish completed."
Write-Host "Publish directory: $publish"
