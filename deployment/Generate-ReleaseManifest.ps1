param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$version = '1.0.0-rc1'
$schema = '20260726080125_AddSystemAdministration'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$installers = Get-ChildItem -LiteralPath $OutputDirectory -Filter "FleetSafetyManager-*-$version.exe" |
  Sort-Object Name | ForEach-Object {
    [ordered]@{ fileName=$_.Name; size=$_.Length;
      sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
  }
$manifest = [ordered]@{
  productName='Fleet Safety Manager'; releaseVersion=$version
  buildDateUtc=(Get-Date).ToUniversalTime().ToString('O')
  commitHash=(git -C $root rev-parse HEAD)
  supportedOperatingSystems=@('Windows 10 x64','Windows 11 x64','Windows Server 2019+')
  databaseSchemaVersion=$schema
  minimumSupportedSchema='202607240000_InitialCoreSchema'
  installers=@($installers)
  includedMigrations=@(
    '202607240000_InitialCoreSchema',
    '202607250001_AddEquipmentDocumentRepository',
    '20260725195239_AddInspectionReports',
    '20260725202932_AddManagedServiceRequests',
    $schema)
  dataLocations=@{
    office='%ProgramData%\Fleet Safety Manager\Office'
    ship='%ProgramData%\Fleet Safety Manager\Ship'
  }
  upgradeNotes='Retains configuration, database, documents, backups and logs. Migration runs at first service startup.'
  knownLimitations=@(
    'Pilot release; validate restore on an isolated machine before production use.',
    'Synchronization remains limited to the existing implementation.')
}
$jsonPath=Join-Path $OutputDirectory 'FleetSafetyManager-1.0.0-rc1-manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
$md=@("# Fleet Safety Manager 1.0.0-rc1 release manifest","",
  "Build UTC: $($manifest.buildDateUtc)","Commit: $($manifest.commitHash)",
  "Schema: $schema","","## Installers","")
foreach($item in $installers){$md += "- $($item.fileName) — $($item.size) bytes — SHA-256 ``$($item.sha256)``"}
$md += "","## Upgrade","",$manifest.upgradeNotes,"","## Known limitations",""
foreach($item in $manifest.knownLimitations){$md += "- $item"}
$md | Set-Content -LiteralPath (Join-Path $OutputDirectory 'FleetSafetyManager-1.0.0-rc1-manifest.md') -Encoding UTF8
