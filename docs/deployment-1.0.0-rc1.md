# Fleet Safety Manager 1.0.0-rc1 deployment

## Office and Ship installation

Run the matching installer as Administrator. Binaries install under Program
Files; writable configuration, database, documents, backups and logs remain
under `%ProgramData%\Fleet Safety Manager\Office` or `Ship`. Upgrades and
uninstall retain these directories. The service listens on TCP 8080; permit
inbound access only on trusted office networks. Ship operation is local and
offline at `http://localhost:8080`.

Before first service start, set a unique JWT key of at least 32 characters in
the ProgramData `appsettings.json`. No production password is supplied.
For a new installation, temporarily enable `DevelopmentSeed` and explicitly
provide strong unique passwords, start once, then disable it and erase those
password values. Administration cannot recreate this bootstrap after users
exist. Configure company, storage, backup, time zone and notifications in
Administration. Set `ShipVesselId` for ship scope using the supported vessel ID.

## Upgrade, rollback and uninstall

Create and verify a Full backup before upgrade. Stop the service, retain the
installer log and backup checksum, then run the newer installer. Configuration
and data are not overwritten; migrations execute once at service startup.
If upgrade fails, stop the service and preserve the failed database and logs.
Follow [database lifecycle recovery](database-lifecycle.md); never overwrite the
original with an unverified copy. Uninstall removes application binaries,
service and shortcuts but retains ProgramData. Remove data only as a separate,
explicit administrator action after another verified backup.

To move PCs, install the same or newer compatible version, copy the verified
full backup, stage restore, restart, and verify Health. For service problems,
check `services.msc`, TCP 8080, the configured Logs directory, the JWT setting,
directory permissions, and migration-lock status.

## Pilot deployment checklist

Office:

- Install and configure JWT, company, paths, time zone and thresholds.
- Create users; create/verify vessel and assign Superintendent.
- Verify dashboard, Health, Diagnostics and role restrictions.
- Generate PDF and upload/download an attachment.
- Create a Full backup and test restore on an isolated copy.
- Restart Windows and confirm service, data and migration status.

Pilot vessel:

- Install Ship edition and configure vessel scope and JWT.
- Verify Crew login, equipment/documents and finalized inspections.
- Verify permitted service-request creation and existing synchronization controls.
- Disconnect internet and confirm normal local operation and restart recovery.
- Generate PDF, test attachments, create backup, and verify Health is green.

Acceptance requires no browser console errors, current schema, successful
backup, persistent data after restart, and correct role restrictions.
