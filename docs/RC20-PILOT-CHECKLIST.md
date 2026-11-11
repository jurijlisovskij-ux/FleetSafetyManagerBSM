# RC20 Pilot Checklist

Use this checklist for every Office and Ship pilot deployment. Do not import a medicine list unless the source is approved by the operator’s responsible medical/regulatory authority.

## Before installation

- [ ] Record the installed release shown in `%LOCALAPPDATA%\FleetSafetyManager\current.txt`.
- [ ] Back up the active release’s `Data`, `Documents`, and `Backups` folders. The SQLite database is under `Data`; uploaded documents, inspection reports, and service requests are under `Documents`.
- [ ] Confirm a restore from the backup on a separate test location before upgrading a production pilot.
- [ ] Confirm the synchronization endpoint uses HTTPS with a certificate trusted by both computers. Plain HTTP is for loopback development only.

## Office installation

- [ ] Run `FleetSafetyManager_RC20_Office_Setup.exe` as an administrator.
- [ ] Confirm exactly one Fleet Safety Manager service, desktop shortcut, and Start Menu entry exist.
- [ ] Launch once from Desktop, Start Menu, and `Launch Fleet Safety Manager.cmd`; all must open the release named by `current.txt`.
- [ ] Open the Windows Start menu and test `Reset FSM Administrator Password` with Windows administrator approval.

## Ship installation

- [ ] Run `FleetSafetyManager_RC20_Ship_Setup.exe` as an administrator on the vessel computer.
- [ ] Confirm `edition.txt` contains `Ship` and `current.txt` names the installed `RC20_<build>` folder.
- [ ] Confirm the configured local vessel is the only vessel visible to Crew.
- [ ] Confirm starting the application after installation does not request administrator rights.

## Synchronization provisioning

- [ ] On Office, configure the vessel synchronization endpoint and automatic interval.
- [ ] Provision the synchronization token through the authorized local workflow; do not send it in ordinary email or logs.
- [ ] Install/trust the HTTPS certificate chain on Office and Ship and verify hostname matching and validity dates.
- [ ] Run the first synchronization manually. Confirm vessel, assigned users, equipment, and approved Medicine configuration arrive without duplicates.
- [ ] Change one Ship equipment expiry and Medicine actual quantity, synchronize, and confirm Office receives both.

## Pilot setup and workflows

- [ ] Create users with temporary passwords and confirm forced password change.
- [ ] Assign each Superintendent and Crew user only to the required vessels.
- [ ] Import an approved Category A/B or Dangerous Goods medicine template, review the preview, and preserve actual quantities unless overwrite is explicitly authorized.
- [ ] Upload an equipment report/certificate and verify its checksum after synchronization.
- [ ] Select only intended LSA/FFE items, record OK/Defect/Not Applicable results, and generate the completed inspection PDF.
- [ ] Open the report from vessel inspection history and close a test defect with closure details.
- [ ] Select only defective items requiring shore support and generate the XLSX Service Request; test the optional attachment ZIP.

## Restore test

- [ ] Stop the Fleet Safety Manager service.
- [ ] Restore `Data`, `Documents`, and `Backups` into an isolated RC20 test release.
- [ ] Start the service and verify users, vessel assignments, equipment, Medicine inventory, inspection history, and documents.
- [ ] Record the restore date, operator, backup identifier, and result.

## Rollback

- [ ] Stop the Fleet Safety Manager service and preserve a copy of the failed RC20 release plus installer log.
- [ ] Restore the pre-upgrade `Data`, `Documents`, and `Backups` backup.
- [ ] Reinstall the previously approved installer or restore its release folder, then update `current.txt` only to that verified release.
- [ ] Update the single Windows service binary path to the restored executable and restart it.
- [ ] Verify Desktop, Start Menu, and CMD launch the same restored release. Do not modify the `release/rc19-stable` branch, `v0.19.0` tag, or published RC19 assets.
