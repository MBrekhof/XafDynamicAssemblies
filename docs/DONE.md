# DONE — XafDynamicAssemblies

#### DATA-003: Required-column ADD COLUMN fails on populated tables and the orchestrator deploys anyway (ID: 1561)

**Completed: 2026-09-10, commit 02ed2b0 (branch fix/codex-review-2026-09).** SchemaSynchronizer isolates each class (aggregated failure message), `byte[]` default is `'\x'::bytea`, and a required reference on a populated table is refused with a clear message: Codex's diff review showed that "add as NULL" (the card's own fix) would leave rows EF cannot materialize into the non-nullable Guid, breaking reads. `ExecuteHotLoadAsync` returns the error and stops after a DDL failure (no compile, no RestartNeeded, no exit-42); `SchemaChangeController` awaits it (documented XAF Blazor `async void` Execute shape, dxdocs 404738) and shows an XAF error toast. Phase07 Test_01 rewritten to assert the toast; Phase05 + Phase07 green.

#### DATA-005: Schema export drops every IsDefaultField field entirely; import loses a real column per AI-created entity (ID: 1564)

**Completed: 2026-09-10, commit abf1190 (branch fix/codex-review-2026-09).** Removed the `!IsDefaultField` export filter and the matching removal guard; `CustomFieldDto.IsDefaultField` round-trips. Package version 1.1; a legacy 1.0 import keeps existing default fields instead of deleting them (Codex review catch: those packages omitted the field deliberately).

#### DATA-002: XAF DB updater drops columns the additive SchemaSynchronizer promises to keep (ID: 1556)

**Completed: 2026-09-10, commit 8b2eec4 (branch fix/codex-review-2026-09).** `SchemaUpdateOptions.DisableAlterAndDeleteOperations = true` in `AddSecuredEFCore` (verified in DX 26.1 `EFCoreDatabaseSchemaUpdater.RemoveAlterAndDeleteOperations`); `SchemaSynchronizer` class comment records the contract.

#### PERF-001: Every new Blazor circuit bumps ModelVersion and rebuilds an identical EF model (ID: 1562)

**Completed: 2026-09-10, commit 0ad032b (branch fix/codex-review-2026-09).** `RuntimeEntityTypes` setter ignores a same-instance assignment; `BootstrapRuntimeEntities` fast path for later circuits (fill AdditionalExportedTypes, seed the orchestrator's known type names — Codex catch — skip QueryMetadata/DDL/compile). Phase08 Test_03 now opens three circuits concurrently (TEST-012).

#### DATA-004: QueryMetadata drops seven CustomField UI properties, so hidden/read-only/size/tooltip settings never compile (ID: 1563)

**Completed: 2026-09-10, commit d785811 (branch fix/codex-review-2026-09).** QueryMetadata reads the seven UI columns. Reading them exposed that the generator emitted `[DevExpress.Persistent.Base.Size]` and `[DevExpress.ExpressApp.Editors.Editable]`, which do not exist in 26.1 (Codex review; confirmed with a Roslyn compile probe) — both generators now emit `[DevExpress.ExpressApp.DC.FieldSize]` and `[ModelDefault("AllowEdit","False")]`; `GeneratedAttributeCompileTests` compiles a class with every attribute; Phase09 Test_18 assertions updated.

#### AI-002: modify_entity with a partial field payload silently flips IsRequired to false (ID: 1565)

**Completed: 2026-09-10, commit 51445a5 (branch fix/codex-review-2026-09).** `FieldDefinition.Required` is `bool?`; updateFields assigns only when present, create/add default false.

#### ACT-003: A failing step leaves earlier SetField mutations pending; next Save persists a half-applied action (ID: 1566)

**Completed: 2026-09-10, commit a91fd9f (branch fix/codex-review-2026-09).** Two-pass `CustomAction_Execute`: resolve every member/value/OpenView target (return on first error), then apply SetValues, show messages, commit. No Rollback. Type resolution extracted to `ResolveEntityType`.

#### DATA-006: Identifiers over 63 bytes make SchemaSynchronizer non-idempotent (table/column checks and FK names) (ID: 1567)

**Completed: 2026-09-10, commit f529379 (branch fix/codex-review-2026-09).** `SchemaSynchronizer.PgName` (63-byte truncation) on existence checks and FK constraint names; `ConstraintExists` scoped to the owning table (Codex: two long names can truncate to one constraint name). `MetadataValidator` + UI rules cap ClassName at 63 and FieldName at 61 (room for the `Id` companion).

#### AI-003: Polly retry never fires for the configured TimeoutSeconds (TimeoutRejectedException not in ShouldHandle) (ID: 1568)

**Completed: 2026-09-10, commit 0369a1b (branch fix/codex-review-2026-09).** `Polly.Timeout.TimeoutRejectedException` added to `ShouldHandle`.

#### AI-004: set_role_permissions reflects for an instance method that is a static extension; can never succeed (ID: 1580)

**Completed: 2026-09-10, commit 510df73 (branch fix/codex-review-2026-09).** Reflection replaced by direct calls: `PermissionPolicyRole`, `SecurityOperations`, `role.AddTypePermissionsRecursively(targetType, ops, SecurityPermissionState.Allow/Deny)` (signature verified in DX 26.1 `PermissionSettingHelper.cs:187`). `list_roles` simplified the same way.

#### TEST-004: Phase04 depends on Phase02's Customer with no class ordering; works by file-name coincidence (ID: 1569)

**Completed: 2026-09-10, commit 2dfd148 (branch fix/codex-review-2026-09).** `Phase04.Test_00_EnsureCustomerExists` inserts Customer + Name via `DatabaseHelper.InsertClassViaDb`/`InsertFieldViaDb` when the live row is missing; Test_01's Deploy compiles it.

#### TEST-005: WaitForDeployRestartAsync can pass against the old server; add an /_instance marker and wait for it to change (ID: 1570)

**Completed: 2026-09-10, commit b2599ff (branch fix/codex-review-2026-09).** `GET /_instance` (per-process GUID, anonymous) in Startup; `ClickDeploySchemaAsync` records it and `WaitForDeployRestartAsync` polls until it changes (bounded by serverTimeoutSeconds), replacing the two fixed sleeps.

#### TEST-006: AIChatPanel.WaitForResponseAsync accepts the previous turn's answer on multi-turn tests (ID: 1571)

**Completed: 2026-09-10, commit d6059df (branch fix/codex-review-2026-09).** `SendMessageAsync` captures the assistant-message count before Enter; `WaitForResponseAsync(timeout, before)` requires `count > before`; `ClickSuggestionAsync` exposes `LastSentAssistantCount`.

#### TEST-007: WaitForLoadingAsync is a 500 ms sleep on Blazor Server; FindInputByLabelAsync never auto-waits (ID: 1572)

**Completed: 2026-09-10, commit d9b862c (branch fix/codex-review-2026-09).** `FindInputByLabelAsync`/`FindContainerByLabelAsync` use one auto-waiting `:is(input, textarea)` locator; `ClickNewAsync` waits for `.dxbl-fl-ctrl`; 16 post-New 2000 ms sleeps deleted. Follow-up: Phase03's save-and-check helper polls for the validation error (bounded 8 s) instead of a fixed 1.5 s the faster flow raced (3 failures in the first full run, green on rerun). Full regression 34 min (was 37).

#### TEST-008: Phase06 graduation assertion reads an unfiltered CustomClasses row; soft-deleted GradTest from a prior run can satisfy it (ID: 1573)

**Completed: 2026-09-10, commit c85ac41 (branch fix/codex-review-2026-09).** GCRecord filter on the GradTest read plus an exactly-one-live-row assertion.

#### TEST-010: Phase04 Test_04_AddFieldViaNestedGrid cannot fail (swallowed try/catch, Assert.True(true) both branches) (ID: 1575)

**Completed: 2026-09-10, commit 743e83b (branch fix/codex-review-2026-09).** try/catch and both-branches-true removed. Live DOM check (Playwright MCP): the DetailView ribbon has no New (bar-items); the nested Fields ListView toolbar renders its New as `dxbl-toolbar-item > button[data-action-name="New"]` — the test targets that and asserts the ProductName row. Green.

#### HOT-001: Hot-load swaps live CLR types in the dying process; delete the in-process swap (ID: 1557)

**Completed: 2026-09-10, commit ae4fd56 (branch fix/codex-review-2026-09).** `ExecuteHotLoadAsync` uses `ValidateCompilation` (nothing loaded into the process), then RestartNeeded + notify; steps 4–6 and `RegisterTypesInTypesInfo` deleted, `RefreshRuntimeTypes` private. Also: a compile failure no longer restarts into degraded mode — the running process still has the previous working type set, so it keeps serving and the Deploy toast shows the errors.

#### CFG-001: EasyTest build switches EF's connection string but bootstrap/DDL still use ConnectionString (ID: 1559)

**Completed: 2026-09-10, commit 963c3ea (branch fix/codex-review-2026-09).** One `connectionString` local at the top of `ConfigureServices` (EASYTEST override applied there) feeds `RuntimeConnectionString` and `UseNpgsql`; duplicated lookup in `WithDbContext` deleted.

#### TEST-003: Regression suite runs destructive SQL against the app's only database; add a localhost guard (ID: 1558)

**Completed: 2026-09-10, commit 69862e2 (branch fix/codex-review-2026-09).** `DatabaseHelper.GetConnection` throws unless host is localhost/127.0.0.1/::1 and the database is `XafDynamicAssemblies`.

#### TEST-009: Phase11 Test_18 asserts zero CustomActionSteps table-wide after deleting one action (ID: 1574)

**Completed: 2026-09-10, commit 52ed34c (branch fix/codex-review-2026-09).** Action ID captured before the delete turn; the step poll counts `CustomActionId = @id` only.

#### TEST-011: Mock describe_entity emits class_name but the tool parameter is entityName (same drift class as TEST-002) (ID: 1576)

**Completed: 2026-09-10, commit f2033d3 (branch fix/codex-review-2026-09).** `ScriptMatcher` sends `entityName`; `MockToolContractTests` runs sample prompts through the matcher and asserts every tool_use input key is a parameter of the C# tool method (mapped by snake→Pascal name).

#### TEST-012: Phase08 Test_03_ConcurrentPageAccess does no concurrent work (ID: 1577)

**Completed: 2026-09-10, commit e67de1e (branch fix/codex-review-2026-09).** Three `BrowserFixture.NewPageAsync()` contexts navigate to PerfTest00..02 via `Task.WhenAll`; each must render `.dxbl-grid`.

#### TEST-013: Live AI tests report Passed instead of Skipped when AI_TEST_API_KEY is unset (ID: 1578)

**Completed: 2026-09-10, commit 829a489 (branch fix/codex-review-2026-09).** `Xunit.SkippableFact` 1.5.*, five `[SkippableFact]` + `Skip.If`; unfiltered run now reports 5 Skipped.

#### CTRL-001: Graduation controllers subscribe anonymous handlers in OnActivated with no OnDeactivated (ID: 1579)

**Completed: 2026-09-10, commit a7ee201 (branch fix/codex-review-2026-09).** Named handlers + `OnDeactivated` unsubscription in both graduation warning controllers (events verified as plain `EventHandler` in the 26.1 sources).

#### SEC-003: Metadata strings are interpolated raw into generated C# (code injection via ReferencedClassName/TypeName) (ID: 1554)

**Completed: 2026-09-09, commit 0be7d4c (branch fix/codex-review-2026-09).** New
`Module/Validation/MetadataValidator.cs` is the one guard every path converges on: identifier,
keyword, reserved name, supported type, reference target, duplicate / FK-companion (`XId`) /
class-name collisions. `RuntimeAssemblyBuilder.ValidateCompilation` and `Compile` run it before any
source is generated and report failures as `Errors` (not throw — `EarlyBootstrap` has no guard, so a
throw would take the process down on one bad row; a failed result already routes to DegradedMode /
validate_schema). `GenerateSource` and `GraduationService.GenerateGraduationSource` throw as a hard
backstop. String metadata goes through `SymbolDisplay.FormatLiteral`; graduation descriptions are
flattened to one comment line; `MapToClrTypeName` no longer falls through. Codex plan review added
three catches that landed: identifier regexes anchored with `\z` (trailing newline bypassed `$`),
the collision checks, and blank-name rejection. 22 unit tests in `MetadataValidatorTests`.

#### AI-001: create_entity/modify_entity commit metadata without identifier/type validation; one bad row degrades every runtime entity (ID: 1560)

**Completed: 2026-09-09, commit 64d3a46 (branch fix/codex-review-2026-09).** `CreateEntity` and
`ModifyEntity` call `MetadataValidator.Validate(cc)` right before `CommitChanges()` and return the
message instead of saving. `modify_entity` also removes a deleted field from the in-memory collection
first. Codex diff review caught that EF relationship fixup plus the explicit `Fields.Add` can hold the
same tracked instance twice, so the validator iterates `Distinct()` instances (EF `BaseObject` does
not override `Equals`, verified in the 26.1 sources).

#### SEC-004: No authentication anywhere; OData metadata CRUD and exposed entities are anonymous (ID: 1555)

**Completed: 2026-09-08, commit 71bd193.** Decision: real security (option a), "nobody should
deploy without security". Wired the DX 26.1 template shape from a fresh Template Kit app
(`C:\Projects\dxapplication2`): `AddSecuredEFCore`, `builder.Security.UseIntegratedMode` +
password auth, cookie + JWT bearer, `ApplicationUser`/`ApplicationUserLoginInfo`, Updater seeds
Administrators + Admin (empty password, non-Release), `JwtTokenProviderService` +
`POST /api/Authentication/Authenticate`. Two non-obvious findings: (1) Npgsql needs
`Persist Security Info=True` because DX's `MARSDbCommandInterceptor` clones connections from the
live connection string during permission prefetch; (2) the exit-42 restart loop never runs the
XAF updater, so new persistent types need `--updateDatabase` once. Tests: `LoginPage` page object,
fixture/helper login, Phase10 bearer token. Verified anonymous OData 401 / bearer 200, UI login,
cookie survives restart, full regression 168/168. Re-evaluation of the remaining Codex cards
against the secured app is the next step; AI-004 moved to P2 as a consequence.

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
