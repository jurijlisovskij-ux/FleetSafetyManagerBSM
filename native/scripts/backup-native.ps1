param(
    [string]$InstallFolder = "C:\FleetSafetyManager",
    [int]$RetentionDays = 30
)

$backupFolder = Join-Path $InstallFolder "Backups"
New-Item -ItemType Directory -Force -Path $backupFolder | Out-Null

$stamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
$temp = Join-Path $env:TEMP "fsm_$stamp"
$zip = Join-Path $backupFolder "FSM_Backup_$stamp.zip"

New-Item -ItemType Directory -Force -Path $temp | Out-Null
try {
    Copy-Item (Join-Path $InstallFolder "Data") $temp -Recurse -Force
    Copy-Item (Join-Path $InstallFolder "Documents") $temp -Recurse -Force
    Copy-Item (Join-Path $InstallFolder "appsettings.json") $temp -Force
    Compress-Archive "$temp\*" $zip -CompressionLevel Optimal

    $cutoff = (Get-Date).AddDays(-$RetentionDays)
    Get-ChildItem $backupFolder -Filter "FSM_Backup_*.zip" |
        Where-Object { $_.LastWriteTime -lt $cutoff } |
        Remove-Item -Force
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Backup created: $zip"
