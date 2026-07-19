# TODO — XafDynamicAssemblies

## P2: Medium

#### DATA-001: SchemaSynchronizer.AddMissingColumns case-insensitive column matching (ID: 1050)

Latent bug found during the test migration: `AddMissingColumns` matches existing columns
case-insensitively, so a stale differently-cased column (e.g. `email` vs `Email`) blocks
creation of the correctly-cased column, after which every grid load for that entity throws a
Postgres error dialog. Only bites when stale columns pre-exist (e.g. leftovers from manual
experiments). Repro + analysis in `.superpowers/sdd/task-9-report.md`.
