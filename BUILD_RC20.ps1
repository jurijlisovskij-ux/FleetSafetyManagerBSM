$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
Set-Location $Root

& (Join-Path $Root "native\scripts\build-native.ps1")

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
)

$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        $iscc = $command.Source
    }
}

if (-not $iscc) {
    throw "Inno Setup compiler (ISCC.exe) was not found. Install Inno Setup 6 or 7."
}

$installerScript = Join-Path $Root "installer\FleetSafetyManager.iss"
$buildId = Get-Date -Format "yyyyMMddHHmmss"

Write-Host "Building Office installer..."
& $iscc "/DMyEdition=Office" "/DMyBuild=$buildId" "/DOutputBase=FleetSafetyManager_RC20_Office_Setup" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Office installer build failed with exit code $LASTEXITCODE."
}

Write-Host "Building Ship installer..."
& $iscc "/DMyEdition=Ship" "/DMyBuild=$buildId" "/DOutputBase=FleetSafetyManager_RC20_Ship_Setup" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Ship installer build failed with exit code $LASTEXITCODE."
}

Write-Host "Building personalized Figaro Ship installer..."
& $iscc "/DMyEdition=Ship" "/DMyBuild=$buildId" `
    "/DMyShipVesselId=f1841487-0000-4000-8000-000000000001" `
    "/DMyShipVesselName=Figaro" "/DMyShipImoNumber=1841487" `
    "/DOutputBase=FSM_Ship_Figaro_Setup" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Personalized Figaro Ship installer build failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "RC20 build completed successfully."
Write-Host "Installers: $(Join-Path $Root 'installer\output')"
