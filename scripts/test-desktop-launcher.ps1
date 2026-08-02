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
if ($installer -notmatch 'DestDir: "\{app\}\\Releases\\\{#ReleaseName\}"') {
    throw "Installer payload is not directed to a build-specific release directory."
}
if (-not $installer.Contains("'Releases\{#ReleaseName}', False")) {
    throw "Installer does not switch current.txt to the installed RC20 release."
}
if ($installer -notmatch 'RemoveObsoleteRC19Releases\(NewReleasePath\)') {
    throw "Installer does not clean RC19 only after activating RC20."
}
if ($installer.IndexOf('VerifyRelease(NewReleasePath)', [StringComparison]::Ordinal) -gt
    $installer.IndexOf('InstallOrUpdateService(NewReleasePath)', [StringComparison]::Ordinal)) {
    throw "Installer activates the service before verifying the release copy."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("FSM-launcher-test-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    Copy-Item -LiteralPath $launcher -Destination $testRoot

    foreach ($edition in @("Office", "Ship")) {
        $releaseName = "RC20_2026080201010" + ($(if ($edition -eq "Office") { "1" } else { "2" }))
        $releasePath = Join-Path $testRoot ("Releases\" + $releaseName)
        New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $releasePath "FSM.Api.exe") -Force | Out-Null
        Remove-Item (Join-Path $testRoot "current.txt") -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $testRoot "edition.txt") -ErrorAction SilentlyContinue
        Set-Content (Join-Path $releasePath "appsettings.json") `
            -Value ('{ "Edition": "' + $edition + '" }')

        $process = Start-Process `
            -FilePath (Join-Path $testRoot "FleetSafetyManagerLauncher.exe") `
            -ArgumentList "--validate-only" `
            -WorkingDirectory $testRoot `
            -Wait `
            -PassThru
        if ($process.ExitCode -ne 0) { throw "$edition launcher validation failed." }
        if ((Get-Content (Join-Path $testRoot "current.txt") -Raw).Trim() -ne ("Releases\" + $releaseName)) {
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
