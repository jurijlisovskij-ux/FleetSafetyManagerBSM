$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $Root

Push-Location "$Root\frontend"
npm install --registry=https://registry.npmjs.org/
npm run build
Pop-Location

$wwwroot = "$Root\backend\src\FSM.Api\wwwroot"
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
New-Item -ItemType Directory -Path $wwwroot | Out-Null
Copy-Item "$Root\frontend\dist\*" $wwwroot -Recurse -Force

$publish = "$Root\native\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Path $publish | Out-Null

dotnet publish "$Root\backend\src\FSM.Api\FSM.Api.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $publish

foreach ($folder in @(
    "Data", "Documents\reports", "Documents\certificates",
    "Backups", "Logs"
)) {
    New-Item -ItemType Directory -Force -Path (Join-Path $publish $folder) | Out-Null
}

Copy-Item "$Root\native\scripts\install-service.ps1" $publish
Copy-Item "$Root\native\scripts\uninstall-service.ps1" $publish
Copy-Item "$Root\native\scripts\backup-native.ps1" $publish

Write-Host "Build completed: $publish"
