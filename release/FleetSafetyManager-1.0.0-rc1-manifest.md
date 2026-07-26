# Fleet Safety Manager 1.0.0-rc1 release manifest

Build UTC: 2026-07-26T08:53:35.2228605Z
Commit: f97019cc64844063be921efb5091ebee450e0718
Schema: 20260726080125_AddSystemAdministration

## Installers

- FleetSafetyManager-Office-1.0.0-rc1.exe — 48098013 bytes — SHA-256 `69F41B9885C1A514A8D20EF315ABED43AC4384001650A34D9884D58DEE35038A`
- FleetSafetyManager-Ship-1.0.0-rc1.exe — 48098015 bytes — SHA-256 `14E1A2BAE987EA77FD1D64ACCCC9C61691E64F9E3806F70231610A6055B2C4DE`

## Upgrade

Retains configuration, database, documents, backups and logs. Migration runs at first service startup.

## Known limitations

- Pilot release; validate restore on an isolated machine before production use.
- Synchronization remains limited to the existing implementation.
