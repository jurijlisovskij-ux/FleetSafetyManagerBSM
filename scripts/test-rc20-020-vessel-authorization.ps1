$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# This integration test creates two vessels in a temporary existing SQLite
# database and verifies the seeded OfficeAdmin plus assigned Superintendent
# and Crew identities through both /api/vessels and /api/dashboard.
& (Join-Path $PSScriptRoot "test-role-visibility.ps1")

$access = Get-Content (Join-Path $root "backend\src\FSM.Api\Authorization\VesselAccessService.cs") -Raw
$program = Get-Content (Join-Path $root "backend\src\FSM.Api\Program.cs") -Raw
$resolver = Get-Content (Join-Path $root "backend\src\FSM.Api\DatabaseConnectionResolver.cs") -Raw
$installer = Get-Content (Join-Path $root "installer\FleetSafetyManager.iss") -Raw

if ($access.IndexOf('role, "OfficeAdmin"', [StringComparison]::Ordinal) -gt
    $access.IndexOf('Guid.TryParse(userIdText', [StringComparison]::Ordinal)) {
    throw "OfficeAdmin authorization is incorrectly dependent on assignment/user-id parsing."
}
if ($access -notmatch 'StringComparison\.OrdinalIgnoreCase') {
    throw "Role authorization must tolerate the casing used by existing tokens."
}
if ($program -notmatch 'DatabaseConnectionResolver\.Resolve') {
    throw "Startup does not resolve the configured existing database explicitly."
}
if ($resolver -notmatch 'Path\.GetFullPath' -or
    $resolver -notmatch 'EnumerateDirectories\("RC\*_\*"' -or
    $resolver -notmatch 'File\.Exists\(rootDatabase\)') {
    throw "Existing root/sibling release databases are not protected against accidental empty database creation."
}
if (-not $installer.Contains("FileExists(Candidate + '\Data\fsm.db')") -or
    -not $installer.Contains("FindFirst(ReleasesPath + '\RC*_*'")) {
    throw "Installer does not recover persistent data when current.txt is stale."
}

Write-Host "RC20-020 vessel authorization and existing-database regression tests passed."
