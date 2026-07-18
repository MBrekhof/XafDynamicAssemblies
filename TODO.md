# TODO — XafDynamicAssemblies

## P1: High

#### DX-001: Upgrade DevExpress XAF 25.2.3 → 26.1.3 (ID: 1049)

Bump all DevExpress packages to 26.1.3, fix breaking changes, verify with the .NET
Playwright suite (full regression: server via `run-server-mock.bat`, then
`dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "Category!=LiveAI"`).
Consult dxdocs MCP + installed 26.1 sources (`C:\Program Files\DevExpress 26.1`) for API
changes — never assume from memory. Known 25.2 touchpoint: `DevExpress.Drawing.DXFontStyle`
in Appearance attributes.

## P2: Medium

#### DATA-001: SchemaSynchronizer.AddMissingColumns case-insensitive column matching (ID: 1050)

Latent bug found during the test migration: `AddMissingColumns` matches existing columns
case-insensitively, so a stale differently-cased column (e.g. `email` vs `Email`) blocks
creation of the correctly-cased column, after which every grid load for that entity throws a
Postgres error dialog. Only bites when stale columns pre-exist (e.g. leftovers from manual
experiments). Repro + analysis in `.superpowers/sdd/task-9-report.md`.

#### SEC-001: Rotate Anthropic API key exposed in appsettings.Development.json (ID: 1051)

A real-looking `sk-ant-api03-...` key sits in plaintext in
`XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server/appsettings.Development.json`
(tracked file, pre-existing). Rotate the key at Anthropic, then keep the replacement out of
git (user secrets or env var).
