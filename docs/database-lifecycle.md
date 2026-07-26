# Fleet Safety Manager database lifecycle

## Supported startup sequence

1. A staged, validated restore is applied before any database context is opened.
2. The configured database directory and write permissions are checked.
3. An exclusive `<database>.migration.lock` file is acquired.
4. The database is classified as `New`, `Migrated`, or `LegacyBaselined`.
5. A valid legacy core schema is transactionally baselined at
   `202607240000_InitialCoreSchema`.
6. EF Core applies all pending migrations and verifies none remain.
7. Seed data runs only when the explicit development-seed setting is enabled.
8. The lock is released. Only then does the host start background workers.

`EnsureCreated` is not part of normal startup. It must not be used on an FSM
installation because it bypasses migration history.

## First installation and normal startup

A missing or empty database is created entirely by migrations. On later starts,
FSM checks migration history, refuses unknown future migration identifiers, and
applies supported pending migrations once.

## Legacy installations

A database without migration history is accepted only when the `users`,
`vessels`, `user_vessels`, and `equipment` tables contain the known required
columns. No tables or user rows are recreated. The initial core migration is
recorded transactionally, and later migrations then run normally. Missing or
unknown signatures stop startup without automatic repair.

## Upgrade

Stop all FSM instances, make a full backup, install the new application, and
start one instance. Do not start a second instance until migration completes.
An unsupported newer database or malformed migration history is rejected.

## Backup and restore

Backups contain `manifest.json` with application/schema versions, backup type,
creation time, included database/documents, and SHA-256 checksums. Database
backups are SQLite-consistent snapshots.

Restore is staged from Administration. The archive is checked for traversal,
manifest compatibility, checksums, required files, future schema versions, and
SQLite integrity. It is applied on the next startup before migration. Keep the
original backup until the restored installation has been verified.

## Failure recovery

If migration fails, the application does not start and notification processing
does not run. Preserve the database, logs, backup archive, and migration error.
Restore a known-good backup into an isolated copy before replacing an
installation. Never attempt to repair an unknown schema automatically.

For a damaged database, stop FSM, copy the database and Documents directory,
run SQLite integrity checks on the copy, then restore the last verified full
backup. A migration lock left after a crash may be removed only after confirming
that no FSM process is running.

## Moving to another PC

Create a full backup, install the same or newer compatible FSM version on the
new PC, copy the backup into its configured Backups directory, stage restore,
and restart. Verify Health before allowing users to connect.

## Files never to edit manually

- the SQLite database and `-wal`/`-shm` companions;
- `__EFMigrationsHistory`;
- backup ZIP files or `manifest.json`;
- `restore.pending`;
- stored document filenames;
- the migration lock while any FSM instance is running.
