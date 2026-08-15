param(
    [string]$OutputDirectory = "publish"
)

$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$sourcePath = Join-Path $root "native\FleetSafetyManagerLauncher.cs"
$iconPath = Join-Path $root "installer\FleetSafetyManager.ico"

if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $resolvedOutputDirectory =
        [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    $resolvedOutputDirectory =
        [System.IO.Path]::GetFullPath(
            (Join-Path $root $OutputDirectory))
}

if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Desktop launcher source is missing: $sourcePath"
}

if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "Application icon is missing: $iconPath"
}

New-Item `
    -ItemType Directory `
    -Path $resolvedOutputDirectory `
    -Force | Out-Null

$launcherPath =
    Join-Path $resolvedOutputDirectory "FleetSafetyManagerLauncher.exe"

if (Test-Path -LiteralPath $launcherPath) {
    Remove-Item -LiteralPath $launcherPath -Force
}

$compilerParameters =
    New-Object System.CodeDom.Compiler.CompilerParameters
$compilerParameters.OutputAssembly = $launcherPath
$compilerParameters.GenerateExecutable = $true
$compilerParameters.GenerateInMemory = $false
$compilerParameters.IncludeDebugInformation = $false
$compilerParameters.CompilerOptions =
    "/target:winexe /win32icon:`"$iconPath`""
$compilerParameters.ReferencedAssemblies.Add("System.dll") | Out-Null

$compiler = New-Object Microsoft.CSharp.CSharpCodeProvider
$compilation =
    $compiler.CompileAssemblyFromFile(
        $compilerParameters,
        $sourcePath)

if ($compilation.Errors.HasErrors) {
    $messages = $compilation.Errors |
        ForEach-Object { $_.ToString() }
    throw "Desktop launcher compilation failed:`n$($messages -join "`n")"
}

if (-not (Test-Path -LiteralPath $launcherPath)) {
    throw "FleetSafetyManagerLauncher.exe was not created."
}

Write-Host "Desktop launcher created: $launcherPath"
