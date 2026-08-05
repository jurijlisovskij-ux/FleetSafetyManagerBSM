# RC20 Office ↔ Ship synchronization

## Production setup

1. Install Office Edition. Configure a trusted TLS certificate for the ASP.NET Core/Kestrel endpoint and bind the service to the required network interface and HTTPS port. The application accepts standard `Kestrel` endpoint/certificate configuration.
2. Permit that single HTTPS port through the site's firewall. Do not expose the launcher/UI port broadly unless it is the configured synchronization endpoint.
3. In Office, open **Synchronization**, select the vessel, enable synchronization, enter the externally reachable Office HTTPS URL, choose the interval, and save.
4. Generate or rotate the vessel token. Use a unique token for every vessel.
5. Enter a passphrase of at least 12 characters and export `FSM-Ship-Sync-<IMO>.json`. Send the protected file and its passphrase by separate approved channels.
6. Install Ship Edition with the same immutable vessel ID and IMO. Open **Synchronization**, enter the passphrase, import the file, and select **Test Connection**.
7. Run the first manual synchronization and confirm the vessel, counts, documents, and status before enabling the automatic interval.

The token is encrypted in the exported file with AES-GCM and a PBKDF2-derived key. It is protected by ASP.NET Core Data Protection after import. Never paste it into normal logs or support tickets.

## Explicit local-network HTTP test mode

HTTPS remains required by default. For an administrator-approved isolated LAN test only:

1. During Office installation select **Enable Office synchronization from other computers on the private network (TCP 8080)**. This changes the Office service binding to `0.0.0.0:8080` and creates only the private-profile inbound rule `Fleet Safety Manager Office Synchronization` for TCP 8080.
2. In the Office vessel synchronization settings, enter the effective URL such as `http://192.168.1.10:8080`, enable **Allow HTTP for explicitly approved local-network testing**, and export a new protected Ship configuration.
3. Import it on Ship and use **Test Connection** before synchronizing.

Ship Edition never creates an inbound firewall rule. Clearing the installer task or uninstalling removes the Office synchronization rule. Changing the synchronization port requires updating the service binding and replacing the firewall rule with one restricted to the new port.

## Health and diagnostics

- `GET /api/synchronization/health` returns edition, application version, UTC time, database availability, and synchronization availability without secrets.
- Office **Export Support Diagnostics** creates `FSM-Sync-Diagnostics-<date>.zip` with sanitized settings, versions, vessel identity, recent safe synchronization logs, endpoint health, SQLite integrity status, and firewall-rule status.
- The export excludes passwords, password hashes, recovery keys, synchronization tokens, and document contents.

## Upgrade persistence

The installer upgrade path preserves `Data`, `Documents`, `Backups`, and `Logs` before activating the new RC20 release. Synchronization settings, protected credentials, cursors, transfer-resume metadata, and Data Protection keys live under these persistent application-data folders.
