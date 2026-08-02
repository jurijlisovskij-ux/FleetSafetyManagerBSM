$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publish = Join-Path $root "publish"
$launcher = Join-Path $publish "FleetSafetyManagerLauncher.exe"
$installer = Get-Content (Join-Path $root "installer\FleetSafetyManager.iss") -Raw
$command = Get-Content (Join-Path $root "installer\Launch Fleet Safety Manager.cmd") -Raw

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Launcher build is missing: $launcher"
}

$shortcutTarget = 'Filename: "{app}\FleetSafetyManagerLauncher.exe"; WorkingDir: "{app}"'
if (($installer.Split($shortcutTarget).Count - 1) -lt 2) {
    throw "Desktop and Start Menu shortcuts do not share the launcher target and working directory."
}
if ($command -notmatch 'FleetSafetyManagerLauncher\.exe') {
    throw "Launch Fleet Safety Manager.cmd does not call the common launcher."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("FSM-launcher-test-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    Copy-Item -LiteralPath $launcher -Destination $testRoot
    New-Item -ItemType File -Path (Join-Path $testRoot "FSM.Api.exe") | Out-Null

    foreach ($edition in @("Office", "Ship")) {
        Remove-Item (Join-Path $testRoot "current.txt") -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $testRoot "edition.txt") -ErrorAction SilentlyContinue
        Set-Content (Join-Path $testRoot "appsettings.json") `
            -Value ('{ "Edition": "' + $edition + '" }')

        $process = Start-Process `
            -FilePath (Join-Path $testRoot "FleetSafetyManagerLauncher.exe") `
            -ArgumentList "--validate-only" `
            -WorkingDirectory $testRoot `
            -Wait `
            -PassThru
        if ($process.ExitCode -ne 0) { throw "$edition launcher validation failed." }
        if ((Get-Content (Join-Path $testRoot "current.txt") -Raw).Trim() -ne ".") {
            throw "$edition current.txt was not recreated correctly."
        }
        if ((Get-Content (Join-Path $testRoot "edition.txt") -Raw).Trim() -ne $edition) {
            throw "$edition edition.txt was not recreated correctly."
        }
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}

Write-Host "Desktop launcher checks passed for Office and Ship."
