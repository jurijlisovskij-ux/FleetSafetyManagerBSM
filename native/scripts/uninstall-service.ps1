if (Get-Service "FleetSafetyManager" -ErrorAction SilentlyContinue) {
    Stop-Service "FleetSafetyManager" -Force -ErrorAction SilentlyContinue
    sc.exe delete "FleetSafetyManager" | Out-Null
}
netsh advfirewall firewall delete rule name="Fleet Safety Manager" | Out-Null
Write-Host "Service removed. Data and Documents were preserved."
