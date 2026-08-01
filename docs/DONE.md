# DONE — XafDynamicAssemblies

#### ACT-002: AI-chat action verbs — 4 tools for metadata actions (ID: 1139)

**Completed: 2026-08-01.** The AI schema assistant now manages metadata actions (ACT-001's
live DetailView buttons) through chat: `list_actions` / `create_action` / `delete_action` /
`set_action_active` in `SchemaAIToolsProvider` (10 → 14 tools), live with no deploy/restart.
create_action mirrors the XAF save rules in code (they don't fire on the non-secured tool
ObjectSpace) — hard errors + soft warnings (criteria parse, unknown target, 10-slot
ceiling), SortOrder from JSON array position, `Enum.IsDefined` guard. Built subagent-driven
(spec + plan in `docs/superpowers/`), final whole-branch review clean. Tests: Phase 11
15 → 18 E2E with DB-effect + live button-render assertions (target: compiled SchemaHistory —
full regression #1 proved Customer doesn't survive Phase07's purge in suite order); mock
self-tests 5 → 7. README usage examples added. Merged to master (0601031).

#### TEST-001: Deploy-restart navigation race in WaitForDeployRestartAsync (ID: 1140)

**Completed: 2026-08-01.** Root cause: after exit-42 restart, the reconnecting Blazor
circuit's shortcut-restore navigation aborts the helper's `GotoAsync("/")` with "interrupted
by another navigation" (bit Phase09 Test_09 in a full regression; green standalone). Fix:
shared `GotoRootToleratingRedirectAsync` in ServerHelper (used by WaitForDeployRestartAsync
AND ReloadAndWaitAsync) catches that specific interruption — which itself proves the app is
alive — waits out the competing navigation, retries (bounded). Verified: all deploy phases +
Phase09 green in the following full regression. Merged with ACT-002 (0601031).

#### TEST-002: Mock LLM create_entity drift — class_name vs className (ID: 1141)

**Completed: 2026-08-01.** The mock's canned create_entity payload used Python-era
snake_case keys (`class_name`/`fields`), which never matched the real tool's C# parameter
names — the tool errored on EVERY mocked confirm while Phase 11 tests passed on canned
follow-up text alone. Fixed TDD (self-test repointed at `className`/`fieldsJson` → RED →
mock aligned → 7/7); `Test_07_EntityExistsInMetadata` now polls for the real
`ChatTestVerify` row in CustomClasses, so mocked create-entity coverage is genuine for the
first time and future drift fails loudly. Merged with ACT-002 (0601031).

#### SEC-002: AngleSharp 0.17.1 advisory via HtmlSanitizer (NU1902) (ID: 1054)

**Completed: 2026-07-31.** HtmlSanitizer 9.0.892 → **9.1.974 stable** (the wait-for-stable
decision of 2026-07-19 paid off — no beta needed). 9.1.974 depends on AngleSharp ≥1.6.0
(mXSS fix shipped in 1.5.0) and stable AngleSharp.Css ≥1.0.0; the exact `[0.17.1]` pin is
gone. Our usage surface (`new HtmlSanitizer()`, `AllowedTags.Add`, `Sanitize`) unchanged in
9.1. En route, a second advisory batch surfaced: **NU1903 System.Security.Cryptography.Xml
8.0.3 (5× high, CVE-2026-50648 batch)** transitively via DevExpress.Printing.Core — fixed
with a direct override to 10.0.10 in Module.csproj. Build now has **zero** NU19xx warnings.
Verified: clean build + Phase 11 mocked AI-chat suite 15/15 (sanitizer's only consumer,
incl. markdown/table rendering). Bonus root-cause fix: the long-known Phase11 Test_10 flake
(also bit Test_02 on cold start) was `AIChatPanel.WaitForResponseAsync` racing DxAIChat's
empty tool_use bubble — now waits for non-empty text in the last assistant message.

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
