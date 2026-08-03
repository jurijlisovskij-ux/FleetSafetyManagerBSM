$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Assert-Contains([string]$Path, [string]$Pattern, [string]$Scenario) {
    $text = Get-Content -LiteralPath $Path -Raw
    if ($text -notmatch $Pattern) { throw "$Scenario failed: $Pattern was not found in $Path" }
    Write-Host "PASS: $Scenario"
}

Write-Host "RC20 permanent pilot validation suite"
& (Join-Path $PSScriptRoot "test-role-visibility.ps1")
& (Join-Path $PSScriptRoot "test-equipment-editing.ps1")
& (Join-Path $PSScriptRoot "test-desktop-launcher.ps1")

$medicine = Join-Path $root "backend\src\FSM.Api\Controllers\MedicineController.cs"
$inspection = Join-Path $root "backend\src\FSM.Api\Controllers\InspectionListController.cs"
$serviceRequest = Join-Path $root "backend\src\FSM.Api\Controllers\ServiceRequestController.cs"
$syncFiles = Join-Path $root "backend\src\FSM.Api\Synchronization\SynchronizationFileService.cs"
$syncContracts = Join-Path $root "backend\src\FSM.Api\Synchronization\SynchronizationContracts.cs"
$syncExchange = Join-Path $root "backend\src\FSM.Api\Synchronization\SynchronizationExchangeService.cs"
$installer = Join-Path $root "installer\FleetSafetyManager.iss"

Assert-Contains $syncExchange 'authoritative for actual|Ship owns onboard inventory' "Ship-to-Office equipment/medicine authority"
Assert-Contains $syncExchange '"Office",\s*"user"' "Office-to-Ship user authority"
Assert-Contains $medicine 'HttpGet\("replenishment"\)' "Medicine replenishment generation"
Assert-Contains $medicine 'Math\.Max\(0, x\.RequiredQuantity - x\.ActualQuantity\)' "Medicine missing quantity calculation"
Assert-Contains $medicine 'preview' "Medicine preview-first template import"
Assert-Contains $inspection 'GenerateCompletedPdf' "Inspection PDF generation"
Assert-Contains $inspection 'PdfSha256' "Inspection PDF checksum and deduplication metadata"
Assert-Contains $serviceRequest 'EquipmentIds' "Selected-equipment XLSX Service Request"
Assert-Contains $syncContracts 'NextOffset' "Document transfer resume"
Assert-Contains $syncFiles 'checksum is invalid' "Document checksum validation"
Assert-Contains $installer "RemoveReleasePattern\('RC19_\*'" "RC19 to RC20 upgrade cleanup"
Assert-Contains $installer "RemoveReleasePattern\('RC20_\*'" "RC20 to newer RC20 upgrade cleanup"

Write-Host "All RC20 permanent pilot validations passed."
