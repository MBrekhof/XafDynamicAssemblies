# DONE — XafDynamicAssemblies

#### NET-001: Upgrade to .NET 10 + EF Core 10 (ID: 1053)

**Completed: 2026-07-19.** net8.0 → net10.0 (Module, Blazor.Server, Tests). EF Core
8.0.18 → 10.0.10, Npgsql.EntityFrameworkCore.PostgreSQL 8.0.11 → 10.0.3, Npgsql (Tests)
8.0.6 → 10.0.3, Roslyn 4.10.0 → 5.0.0 (forced: DevExpress.ExpressApp.EFCore 26.1.3 pins
Microsoft.CodeAnalysis.Workspaces.Common = 5.0.0 on net10.0 — NU1107 otherwise). XAF 26.1
officially supports .NET 10 + EF Core 10 (v26.1 release notes; XAF0026). One code fix:
XAF0035 in SchemaExportImportController — SecuritySystem.CurrentUser →
ISecurityStrategyBase via Application.ServiceProvider (documented DX pattern).
RuntimeAssemblyBuilder needed no changes (TRUSTED_PLATFORM_ASSEMBLIES is version-agnostic).
Full regression on net10.0: 163/0/1 in ~32 min. New: NU1902 AngleSharp advisory surfaced by
.NET 10 transitive audit — tracked open as SEC-002 (ID: 1054). Motivation: .NET 8 EOL
Nov 2026; .NET 10 LTS to Nov 2028.

#### ACT-001: Metadata-driven action builder for runtime entities (ID: 1052)

**Completed: 2026-07-19.** Admins define buttons on entity DetailViews as pure metadata —
live, no compilation, no restart. `CustomAction` + aggregated `CustomActionStep` entities
(SetField/ShowMessage/OpenView steps, criteria enablement, XAF validation rules);
`MetadataActionDispatcherController` with a source-verified slot-pool design (10
constructor-declared slots — dynamic OnActivated actions never render in XAF Blazor;
deterministic assignment, ceiling logged); `StepValueConverter` (10 unit tests); 9 Phase 12
E2E tests. Two product bugs caught by review/tests en route (FullName-keyed OpenView
resolution → simple names; stale slot map on deactivation). Full regression 163/0/1.
Merged to master (d550fd7). Fast-follows parked in BACKBURNER: AI-chat verbs, ListView
targets, expression values.

#### DATA-001: SchemaSynchronizer.AddMissingColumns case-insensitive column matching (ID: 1050)

**Completed: 2026-07-19.** Root cause: `GetExistingColumns` used `OrdinalIgnoreCase`, so a
stale differently-cased column (`email`) satisfied the existence check for `Email` and the
correct quoted column was never created, wedging every query for that entity. Fix: ordinal
(case-sensitive) comparison — everything else in the DDL pipeline was already exact-case, and
Postgres allows both casings to coexist (stale extras stay harmless). TDD: failing E2E repro
first (`SchemaSyncCaseSensitivityTests`, stale-table fixture mimicking CreateTable's shape,
exact-case `information_schema` assertion), then the one-comparer fix. Full regression
143 passed / 1 known-flake (Phase11 Test_10, green in isolated re-run) / 1 skipped.
Merged to master (97211a8).

#### DX-001: Upgrade DevExpress XAF 25.2.3 → 26.1.3 (ID: 1049)

**Completed: 2026-07-19.** All 28 DevExpress packages bumped to 26.1.3. Product fixes:
`AIChat.razor` `.Content`→`.Text` (obsolete API); `WebApiOptions.UseResourceDelta = false`
in Startup.cs (26.1 defaults it true under `Latest` compatibility mode, which broke OData
writes for runtime entities — `ResourceDelta<T>` needs deserializer wiring EF Core apps
don't get). Test-side: 26.1 Ribbon renders actions as `<dxbl-bar-item>`; selectors moved to
`button[data-action-name="<Caption>"]` (note: attribute carries the Caption, NOT the Action
Id — verified in 26.1 sources). Full regression green 143/0/1 (26m46s). Merged to master
(9b73c3b).

#### TEST-001: Migrate Playwright tests from Python to .NET (ID: 1048)

**Completed: 2026-07-19.** All E2E tests ported from Python/pytest to C#/xUnit/
`Microsoft.Playwright`: 143 phase tests + 5 mock-server self-tests + 1 manual smoke test.
Full regression green (143 passed / 0 failed / 1 skipped, 27m10s) against the live server in
mock mode. Python stack removed (`tests/`, `Dockerfile.python`, compose `python` service);
docs updated; `run-server-mock.bat` added (AI-chat mock mode for Phase 11). Mock LLM server
ported Flask → in-process ASP.NET Core minimal API. Merged to master (984f5cc).
