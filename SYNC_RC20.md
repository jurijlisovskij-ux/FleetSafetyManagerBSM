# RC20 Office ↔ Ship synchronization

## Normal operation

RC20-026 synchronization is zero-configuration for Office Administrators,
Technical Superintendents, and Ship Crew.

1. The Office Administrator creates a vessel. Fleet Safety Manager creates its
   synchronization identity, credentials, 24-hour schedule, initial state, and
   a one-time activation code automatically.
2. The administrator copies the displayed `FSM-XXXX-XXXX-XXXX` activation code
   to the vessel. The code is scoped to that vessel, expires after seven days,
   and becomes invalid as soon as it is used. A replacement code can be
   generated from the vessel profile.
3. After Ship installation, the crew selects the discovered vessel, enters the
   activation code, and selects **Connect**. Fleet Safety Manager obtains and
   stores the Office endpoint and protected credential internally, then
   downloads the vessel configuration and assigned operational data.
4. Synchronization runs at application startup, after important local changes,
   on the 24-hour schedule, and after connectivity returns. Failed network
   attempts use exponential backoff. The crew continues working while offline.

The normal UI never requests or displays endpoints, tokens, HTTP modes,
provisioning phases, upload/download stages, API responses, or technical logs.
**Settings → Synchronization** contains the simplified state, last/next time,
pending-change count, and the Ship **Synchronize Now** action.

## Network discovery

The Office installer binds the service to private-network TCP 8080 and creates
private-profile firewall rules for that endpoint and UDP 50820 discovery.
Ship discovers Office on the local network through the internal RC20 discovery
service. A centrally managed endpoint may also be supplied through deployment
configuration; it is never entered by crew.

Production deployments can configure a trusted Kestrel HTTPS endpoint and the
internal advertised endpoint through deployment configuration. Plain HTTP is
enabled by the packaged installer only because the bundled service endpoint is
private-network HTTP. No HTTP/HTTPS choice appears in the application UI.

## Advanced Diagnostics

Only an authenticated Office Administrator can open
**Settings → Advanced Diagnostics**. It contains endpoint state, a non-secret
credential fingerprint, upload/download queue counts, retry state, sanitized
logs, HTTP response codes, request timing, and health checks. Diagnostic exports
exclude plaintext credentials, passwords, password hashes, recovery keys, and
document content.

`GET /api/synchronization/health` remains an anonymous, secret-free availability
probe. Other technical synchronization endpoints remain internal compatibility
surfaces for existing installations and automated verification.

## Upgrade persistence

The installer preserves `Data`, `Documents`, `Backups`, and `Logs` before
activating a newer RC20 release. Existing vessel identity, credentials, cursors,
transfer-resume metadata, Data Protection keys, databases, and documents are
retained. RC19 databases receive the synchronization tables and automatic Office
provisioning during startup; a Ship database that already contains its local
vessel opens without reactivation.
