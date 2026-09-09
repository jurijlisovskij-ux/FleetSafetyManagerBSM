$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publish = Join-Path $root "publish"
$launcher = Join-Path $publish "FleetSafetyManagerLauncher.exe"
$installer = Get-Content (Join-Path $root "installer\FleetSafetyManager.iss") -Raw
$command = Get-Content (Join-Path $root "installer\Launch Fleet Safety Manager.cmd") -Raw
$project = Get-Content (Join-Path $root "backend\src\FSM.Api\FSM.Api.csproj") -Raw
$launcherSource = Get-Content (Join-Path $root "native\FleetSafetyManagerLauncher.cs") -Raw

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Launcher build is missing: $launcher"
}
if ($project -notmatch '<OutputType>Exe</OutputType>' -or
    $project -notmatch '<AssemblyName>FSM\.Api</AssemblyName>') {
    throw "FSM.Api must explicitly publish an FSM.Api.exe application host."
}

$shortcutTarget = 'Filename: "{app}\FleetSafetyManagerLauncher.exe"; WorkingDir: "{app}"'
if (([regex]::Matches($installer, [regex]::Escape($shortcutTarget))).Count -lt 2) {
    throw "Desktop and Start Menu shortcuts do not share the launcher target and working directory."
}
if ($installer -notmatch 'UsePreviousAppDir=no') {
    throw "Installer can inherit the obsolete Program Files installation directory."
}
if ($installer -notmatch 'DefaultDirName=\{localappdata\}\\FleetSafetyManager' -or
    $launcherSource -notmatch 'Path\.Combine\(installDirectory, "launcher\.log"\)') {
    throw "Writable launcher state is not confined to LocalAppData."
}
if ($launcherSource -notmatch 'Process\.Start\(fallback\)') {
    throw "Launcher cannot start without service-control administrator rights."
}
if (([regex]::Matches($installer, [regex]::Escape('Name: "{autodesktop}\Fleet Safety Manager"'))).Count -ne 1 -or
    ([regex]::Matches($installer, [regex]::Escape('Name: "{group}\Fleet Safety Manager"'))).Count -ne 1) {
    throw "Installer does not create exactly one stable Desktop and Start Menu shortcut."
}
if ($installer -notmatch '\{commondesktop\}\\Fleet Safety Manager\*\.lnk' -or
    $installer -notmatch '\{userdesktop\}\\Fleet Safety Manager\*\.lnk' -or
    $installer -notmatch '\{commonprograms\}\\Fleet Safety Manager' -or
    $installer -notmatch '\{userprograms\}\\Fleet Safety Manager') {
    throw "Installer does not remove all obsolete Desktop and Start Menu shortcuts."
}
if (-not $installer.Contains("if ServiceExists('{#ServiceName}') then") -or
    $installer.IndexOf('sc config {#ServiceName}', [StringComparison]::Ordinal) -lt 0) {
    throw "Installer does not update the existing service in place."
}
if ($installer.IndexOf('CopyPersistentData(OldReleasePath, NewReleasePath)', [StringComparison]::Ordinal) -gt
    $installer.LastIndexOf('RemoveLegacyProgramFilesBinaries', [StringComparison]::Ordinal)) {
    throw "Installer removes legacy files before migrating persistent data."
}
if ($installer -notmatch "0: FolderName := 'Data'" -or
    $installer -notmatch "1: FolderName := 'Documents'") {
    throw "Upgrade does not preserve databases and documents."
}
if ($installer.IndexOf('CopyPersistentConfiguration(OldReleasePath, NewReleasePath)', [StringComparison]::Ordinal) -lt 0) {
    throw "Upgrade does not preserve the existing connection configuration."
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
if ($installer -notmatch 'RemoveObsoleteReleases\(NewReleasePath\)' -or
    $installer -notmatch "RemoveReleasePattern\('RC19_\*'" -or
    $installer -notmatch "RemoveReleasePattern\('RC20_\*'") {
    throw "Installer does not clean superseded RC19 and RC20 releases after activation."
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

Write-Host "Upgrade checks passed: RC19 -> RC20 and RC20 -> newer RC20, Office and Ship."
