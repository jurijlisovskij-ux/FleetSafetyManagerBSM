Fleet Safety Manager RC19 — GitHub update package

This archive contains only the files that must be replaced in the repository.

Using GitHub Desktop:
1. Open repository FleetSafetyManagerBSM.
2. Repository -> Show in Explorer.
3. Copy the folders from this archive into the repository root and confirm Replace.
4. In GitHub Desktop use summary: RC19 final Windows installer
5. Click Commit to main, then Push origin.
6. Open GitHub -> FleetSafetyManagerBSM -> Actions.
7. Wait for "Build FSM RC19 Windows Installers" to finish with a green check.
8. Open the completed run and download artifact:
   FleetSafetyManager-RC19-Windows-Installers

The artifact contains:
- FleetSafetyManager_RC19_Office_Setup.exe
- FleetSafetyManager_RC19_Ship_Setup.exe

Installer behavior:
- stops RC18/portable FSM.Api.exe automatically;
- installs RC19 as an automatic Windows service on 127.0.0.1:8088;
- creates Desktop and Start Menu shortcuts;
- starts the application without a visible server console;
- preserves Data, Documents, Backups, Logs and existing appsettings.json.
