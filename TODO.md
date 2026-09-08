# TODO — XafDynamicAssemblies

Source: Codex full-repo review 2026-09-08 (27 findings), each verified against the source and DX 26.1 installed sources before carding. Severity in the body is mine, Codex's original is quoted.

## P0: Critical

#### SEC-003: Metadata strings are interpolated raw into generated C# (code injection via ReferencedClassName/TypeName) (ID: 1554)

**Codex (Critical):** `RuntimeAssemblyBuilder.cs:181` interpolates `field.ReferencedClassName` straight into source; the only rule is non-empty. Crafted metadata compiles to executable members that run under the server identity after the next Deploy.

**Assessment: CONFIRMED, Critical (with SEC-004 this is unauthenticated RCE after one Deploy; High on its own).**
- `RuntimeAssemblyBuilder.cs:176-181`: `public virtual {refTypeName} {field.FieldName}` with `refTypeName = field.ReferencedClassName`; `CustomField.cs:56-60` only requires non-empty. Payload `Company X {get;set;} static Emp(){...} public virtual Company` is valid C#.
- Second vector: line 263 `_ => typeName` emits an unsupported `TypeName` verbatim at line 197. UI blocks it via `IsTypeNameValid`, the AI tool path never validates (AI-001).
- `ClassName`/`FieldName` are regex-checked only by XAF Save rules, which do not fire on the AI tool's non-secured ObjectSpace. `NavigationGroup`/`ToolTip`/`DisplayName` go through `EscapeString` and are not vectors; `Description` is never emitted.

**Fix:** Validate at the one point every write path converges on: in `RuntimeAssemblyBuilder.GenerateSource` throw if `ClassName`, any `FieldName` or `ReferencedClassName` fails `CustomClassValidation.IsValidIdentifier`, or a non-reference `TypeName` fails `SupportedTypes.IsSupported`; replace the `_ => typeName` fallthrough with a throw. The orchestrator already treats a throw as a failed compile. Add a `RuleFromBoolProperty` on `ReferencedClassName` so the UI reports it early, and swap `EscapeString` for Roslyn's `SymbolDisplay.FormatLiteral(s, quote: true)`.

## P1: High

#### AI-001: create_entity/modify_entity commit metadata without identifier/type validation; one bad row degrades every runtime entity (ID: 1560)

**Codex (High):** `SchemaAIToolsProvider.cs:459`. Create/modify commits skip the business-object validation rules; class name `class` or field name `public` persist, then break compilation of every runtime entity in the shared assembly.

**Assessment: CONFIRMED, High.**
- `CreateEntity` (`:417-459`) and `ModifyEntity` (`:480-597`) only check `IsNullOrWhiteSpace`; `FieldName = fd.Name`, `TypeName = fd.Type ?? "System.String"` stored as received, then `CommitChanges()` on the `INonSecuredObjectSpaceFactory` space. `RuleFromBoolProperty` rules on `CustomClass.cs:64-80` / `CustomField.cs:38-60` run in the view-level validation controller only (same lesson as ACT-002).
- `RuntimeAssemblyBuilder.cs:160,197` emit names verbatim, no `@` prefix. `class` → CS1001 → single compilation unit fails → `SchemaChangeOrchestrator.cs:87-96` restart → `Module.cs:258-265` DegradedMode, every runtime entity unavailable until the row is fixed by hand. An unsupported `TypeName` also throws in `SupportedTypes.GetPostgresType`, aborting DDL sync for all remaining classes (DATA-003).
- The tool description's "call validate_schema afterwards" is advisory, not enforced.

**Fix:** One shared helper, e.g. `MetadataValidator.Validate(CustomClass cc)` in `Module/Validation/`, applying the existing predicates (`CustomClassValidation.IsValidIdentifier/IsCSharpKeyword/IsReservedTypeName`, `CustomFieldValidation.IsValidIdentifier/IsReservedFieldName`, `SupportedTypes.IsSupported`, identifier regex on `ReferencedClassName`), returning the first message. Call it in `CreateEntity` and `ModifyEntity` right before `CommitChanges()` and return the message instead of committing. Pair with the `GenerateSource` guard from SEC-003 so a bad row can never reach Roslyn from any path.

#### DATA-003: Required-column ADD COLUMN fails on populated tables and the orchestrator deploys anyway (ID: 1561)

**Codex (High):** `SchemaSynchronizer.cs:99`. A required reference on a populated table gets `uuid NOT NULL` without backfill; required `byte[]` gets `DEFAULT NULL`. The additions fail, the orchestrator suppresses the error and continues publishing/restarting.

**Assessment: CONFIRMED, High.**
- `SchemaSynchronizer.cs:98-99`: `ADD COLUMN "XId" uuid NOT NULL` with no DEFAULT → 23502 on any populated table. Line 110 + `SupportedTypes.cs:44` (`_ => "NULL"`) → required `System.Byte[]` becomes `bytea NOT NULL DEFAULT NULL`, same failure.
- `ExecuteNonQuery` is not caught inside `AddMissingColumns`/`SynchronizeAll`, so the first failure also skips every remaining column and class in the loop (line 30-33).
- `SchemaChangeOrchestrator.cs:63-72` catches it as "DDL sync failed (non-fatal)" and proceeds to compile and restart; `Module.cs:195-204, 230-244` swallow it identically at startup. After restart the EF model has the property, the table lacks the column, every ListView/OData query on the entity fails with 42703, and DATA-002 shows the XAF updater will not add it on the restart path.

**Fix:** (a) `AddMissingColumns`: add reference columns as `NULL` and let the compiled non-nullable FK enforce going forward; make `GetPostgresDefault` return `'\x'::bytea` for `System.Byte[]`; wrap each class in its own try/catch so one bad class cannot block the rest. (b) `ExecuteHotLoadAsync` lines 63-72: stop swallowing. On DDL failure log, do not set `RestartNeeded`, do not compile, and surface the error (a `SchemaHistory` row or the existing `SchemaChanged` payload) so the UI shows why Deploy did nothing.

#### DATA-005: Schema export drops every IsDefaultField field entirely; import loses a real column per AI-created entity (ID: 1564)

**Codex (Medium):** `SchemaExportImportService.cs:35`. `IsDefaultField` marks the display property, but export excludes that field; importing elsewhere loses it and may break relationships or actions referencing it.

**Assessment: CONFIRMED and worse than stated, High for round-trip (Medium if export is only a backup).** The whole field is dropped, not just the flag.
- `IsDefaultField` means "display property": `RuntimeAssemblyBuilder.cs:207-211` (`[DefaultProperty]`), `GraduationService.cs:88`, and `SchemaAIToolsProvider.cs:452-453` sets it on the first field of every AI-created entity. There is no "system field" concept in the codebase; the export filter misreads the flag.
- `SchemaExportImportService.cs:34-35`: `Fields = c.Fields.Where(f => !f.IsDefaultField)`; `CustomFieldDto` (`:235-250`) has no `IsDefaultField` member so it could not round-trip anyway; `:143-144` also protects those rows from removal on update.
- Net effect: export → import on a fresh DB recreates every AI-created entity without its first field, and no entity keeps its `[DefaultProperty]`.

**Fix:** Remove the `.Where(f => !f.IsDefaultField)` at line 35 and the `!f.IsDefaultField &&` at line 144; add `public bool IsDefaultField { get; init; }` to `CustomFieldDto`, set it in the `Select` (line 38-53) and copy it in `ApplyFieldValues` (line 179-193).

## P2: Medium

#### DATA-002: XAF DB updater drops columns the additive SchemaSynchronizer promises to keep (ID: 1556)

**Codex (High):** `BlazorApplication.cs:39`. Delete a populated runtime field and deploy: the synchronizer leaves the column, but the XAF updater later executes `DropColumnOperation` and destroys the data.

**Assessment: PARTIALLY CONFIRMED, Medium.** The updater does drop columns, but it never runs on the exit-42 restart loop; only under a debugger or `--updateDatabase`.
- DX `EFCoreDatabaseSchemaUpdater.cs:280-313`: with default `DropUnusedObjects=false` only schema/table/sequence drops are removed; `DropColumnOperation` is kept unless `DisableAlterAndDeleteOperations` (default false), and Npgsql supports the reverse-engineering that generates it.
- `BlazorApplication.cs:24-28` sets `UpdateDatabaseAlways` only under `#if DEBUG && Debugger.IsAttached`; `Program.cs:37-43` is the `--updateDatabase` path. CLAUDE.md documents F5-with-debugger as the normal dev workflow, so it will bite: the next F5 after a field delete silently drops the column.

**Fix:** One line in `Startup.cs:78-81`: `.AddEFCore(options => { options.PreFetchReferenceProperties(); options.SchemaUpdateOptions.DisableAlterAndDeleteOperations = true; })`. That makes the XAF updater add-only, matching the synchronizer's contract. Note it in the `SchemaSynchronizer` class comment.

#### PERF-001: Every new Blazor circuit bumps ModelVersion and rebuilds an identical EF model (ID: 1562)

**Codex (Medium):** `Module.cs:269`. Every Setup reassigns the existing runtime-type array; the setter unconditionally increments `ModelVersion`, so additional circuits generate new cache keys and rebuild equivalent models.

**Assessment: CONFIRMED, Medium.** Per-circuit startup latency and CPU; EF's model cache is size-bounded so not an unbounded leak.
- `XafDynamicAssembliesDbContext.cs:38-46`: setter is `_runtimeEntityTypes = value; Interlocked.Increment(ref _modelVersion);` with no equality check. `DynamicModelCacheKeyFactory.cs:17` keys on `ModelVersion`.
- `Module.cs:252-269`: when an assembly is already loaded, `runtimeTypes = AssemblyManager.RuntimeTypes` (the same array instance) and the assignment still bumps the version. `Module.cs:163-172` calls `BootstrapRuntimeEntities` from every `Setup(XafApplication)`, and XAF instantiates modules per application, which in Blazor Server is per circuit. So every new tab/login runs QueryMetadata + DDL sync + a model rebuild (`OnModelCreating` with change-tracking + lazy-loading proxies).

**Fix:** In the `RuntimeEntityTypes` setter: `if (ReferenceEquals(_runtimeEntityTypes, value)) return;` before the assignment and increment. Separately gate `BootstrapRuntimeEntities` at `Module.cs:170` on `!AssemblyManager.HasLoadedAssembly` so a new circuit does not re-run QueryMetadata and DDL sync either.

#### DATA-004: QueryMetadata drops seven CustomField UI properties, so hidden/read-only/size/tooltip settings never compile (ID: 1563)

**Codex (Medium):** `Module.cs:376`. The metadata query omits visibility/editability flags, `StringMaxLength`, `IsImmediatePostData`, `ToolTip`, `DisplayName`; a saved hidden or non-editable field compiles with defaults.

**Assessment: CONFIRMED, Medium.** Silent loss of admin-configured UI behaviour on every deploy/restart; nothing errors.
- `Module.cs:376-398` selects only `CustomClassId, FieldName, TypeName, IsRequired, IsDefaultField, Description, ReferencedClassName, SortOrder`.
- `CustomField.cs:30-36` persists `IsImmediatePostData`, `StringMaxLength`, `IsVisibleInListView`, `IsVisibleInDetailView`, `IsEditable`, `ToolTip`, `DisplayName`, and `RuntimeAssemblyBuilder.cs:194-195, 227-241` (`EmitFieldAttributes`) emits `[ImmediatePostData]`, `[VisibleInListView(false)]`, `[VisibleInDetailView(false)]`, `[Editable(false)]`, `[ToolTip]`, `[DisplayName]`, `[Size]` from exactly those. All three compile paths (`EarlyBootstrap`, `BootstrapRuntimeEntities`, hot-load) use this one query.
- Tests' `DatabaseHelper.cs:62-70` inserts these columns, so any test asserting hidden/read-only rendering can only pass by DB leftovers.

**Fix:** Extend the SELECT at `Module.cs:376-377` with the seven columns and map them in the `new CustomField { ... }` at `:389-398` (`IsImmediatePostData = reader.GetBoolean(8)`, `StringMaxLength = reader.IsDBNull(9) ? null : reader.GetInt32(9)`, etc.). Guard like the existing `hasApiExposedCol` check only if pre-upgrade databases must still boot.

#### AI-002: modify_entity with a partial field payload silently flips IsRequired to false (ID: 1565)

**Codex (Medium):** `SchemaAIToolsProvider.cs:590`. Updating only a field's description assigns `IsRequired = false` because the omitted `FieldDefinition.Required` deserializes to the non-nullable bool default.

**Assessment: CONFIRMED, Medium.**
- `SchemaAIToolsProvider.cs:1104-1111`: `public bool Required { get; set; }`.
- `:582-592`: the `updateFields` loop guards `Type`, `ReferencedClass`, `Description` with `!= null`, but line 590 is unconditional: `field.IsRequired = fd.Required;`. A payload `{"name":"Email","description":"..."}` flips a required field to optional; after Deploy the generated property becomes nullable (`RuntimeAssemblyBuilder.cs:186-190`) while the existing PostgreSQL column stays `NOT NULL` (the synchronizer never alters existing columns), so EF inserts of null then fail at the DB.

**Fix:** Change `FieldDefinition.Required` to `bool?`; at line 590 `if (fd.Required.HasValue) field.IsRequired = fd.Required.Value;`; at the two create/add sites (`:448`, `:561`) use `fd.Required ?? false`.

#### ACT-003: A failing step leaves earlier SetField mutations pending; next Save persists a half-applied action (ID: 1566)

**Codex (Medium):** `MetadataActionDispatcherController.cs:195`. If step 1 sets `Status = Approved` and step 2 fails conversion, the controller returns an error without restoring Status; a later ordinary Save persists part of the failed action.

**Assessment: CONFIRMED, Medium.** Silent partial writes; ShowMessage steps have also already fired by then.
- `:176-243`: steps run sequentially; a SetField calls `member.SetValue(obj, converted)` at line 195 immediately.
- Later steps bail with `return` after earlier SetFields ran: unknown member (`:183-187`), `FormatException` from `StepValueConverter.Convert` (`:189-194`), unresolved OpenView target (`:234-238`). Nothing undoes prior `SetValue`; `CommitChanges()` at `:245-246` is skipped, so the view's ObjectSpace is left `IsModified` with a half-applied action that the user's next Save (or the close prompt) persists.

**Fix:** Two-pass in `CustomAction_Execute`: first loop resolves every step (member lookup, `StepValueConverter.Convert`, OpenView type resolution) into a `List<(IMemberInfo member, object value)>` plus the OpenView target, returning on any error before touching `obj`; second loop applies the SetValues and shows messages, then commits. Do not use `ObjectSpace.Rollback()`, which would also discard the user's own unsaved edits.

#### DATA-006: Identifiers over 63 bytes make SchemaSynchronizer non-idempotent (table/column checks and FK names) (ID: 1567)

**Codex (Medium):** `SchemaSynchronizer.cs:179`. Metadata permits 128-char names; PostgreSQL truncates identifiers to 63 bytes; existence checks with the full name miss the truncated object and re-create; generated FK names likewise.

**Assessment: CONFIRMED, Medium.** The realistic trigger is the FK constraint name, not a 64-char class name.
- `XafDynamicAssembliesDbContext.cs:76, 93`: `ClassName`/`FieldName` `HasMaxLength(128)`; the validation classes check regex and keywords only, no length cap.
- `SchemaSynchronizer.cs:176-183` `TableExists` and `:185-202` `GetExistingColumns` query `information_schema` with the full metadata name (Ordinal). A >63-char name never matches, the second sync re-issues `CREATE TABLE`/`ADD COLUMN`, PostgreSQL throws "already exists", and since `SynchronizeAll` (`:25-34`) has no per-class try/catch that aborts DDL sync for every later class on every startup.
- `:138` `constraintName = $"FK_{cc.ClassName}_{field.FieldName}"` exceeds 63 with ordinary names (30 + 30 chars); `ConstraintExists` (`:161-168`) never finds it, so every sync retries `ADD CONSTRAINT` and logs the "already exists" warning. Noisy, not fatal.

**Fix:** In `SchemaSynchronizer` add `static string PgName(string s) => s.Length > 63 ? s[..63] : s;` (names are ASCII by the validation regex) and pass `PgName(...)` for the `@name` parameter in `TableExists`/`GetExistingColumns`, the `existingColumns.Contains` comparisons at `:96/:106`, and `constraintName` at `:138`. Add a `RuleFromBoolProperty` on `ClassName`/`FieldName` requiring `Length <= 63` so the truncation case is refused at save time.

#### AI-003: Polly retry never fires for the configured TimeoutSeconds (TimeoutRejectedException not in ShouldHandle) (ID: 1568)

**Codex (Medium):** `AIChatService.cs:266`. The timeout strategy throws `TimeoutRejectedException`, but `ShouldHandle` accepts cancellation and selected HTTP failures only, so a request exceeding the timeout gets no retry.

**Assessment: CONFIRMED, Medium.** A slow provider response fails the chat turn on the first attempt.
- `AIChatService.cs:252-284`: `AddRetry` first (outermost), `.AddTimeout(TimeSpan.FromSeconds(_options.TimeoutSeconds))` second, so timeout wraps each attempt and surfaces to retry as `Polly.Timeout.TimeoutRejectedException`, which is not an `OperationCanceledException` subclass.
- `ShouldHandle` (`:259-274`) returns true only for `TaskCanceledException`/`OperationCanceledException` (line 266) and `HttpRequestException` with 429/5xx; the comment "Retry timeouts" at `:265` only covers HttpClient's own timeout.

**Fix:** Change line 266 to `if (ex is TaskCanceledException or OperationCanceledException or Polly.Timeout.TimeoutRejectedException) return true;`. The retry-outer/timeout-inner ordering is already correct for per-attempt timeouts.

#### AI-004: set_role_permissions reflects for an instance method that is a static extension; can never succeed (ID: 1580)

**Codex (Low):** `SchemaAIToolsProvider.cs:812`. The tool searches instance methods for a two-parameter method although DX exposes a static extension, and invokes with three arguments. Latent because security is not enabled.

**Assessment: CONFIRMED, Medium now that SEC-004 (71bd193) wired security: the LLM is offered a `set_role_permissions` tool that always fails.**
- `:812-813` looks for `roleType.GetMethods()` named `AddTypePermissionsRecursively` with 2 parameters, then `:832/:842` invoke it with three arguments (`targetType, operationsStr, allowState`): self-contradictory before checking DX.
- DX 26.1 `PermissionSettingHelper.cs:52-53, 184-195`: `AddTypePermissionsRecursively` is a static extension on `public static class PermissionSettingHelper` in `DevExpress.ExpressApp.Security`, signature `(this IPermissionPolicyRole role, Type targetType, string operations, SecurityPermissionState? state, ITypesInfo typesInfo = null)`. `GetMethods()` on the role type never returns it.
- Neither csproj references `DevExpress.ExpressApp.Security`, so today `roleType` resolves to null (`:747-753`) and the tool reports "Security module is not configured" one step earlier.

**Fix:** Reference `DevExpress.ExpressApp.Security` 26.1.3 in `Module.csproj` and replace the reflection block at `:810-848` with a direct call: cast `role` to `IPermissionPolicyRole` and call `role.AddTypePermissionsRecursively(targetType, operationsStr, SecurityPermissionState.Allow)` / `Deny`. SEC-004 chose option (a), so fix the tool (or drop it if role management via chat is not wanted).

#### TEST-004: Phase04 depends on Phase02's Customer with no class ordering; works by file-name coincidence (ID: 1569)

**Codex (Medium):** `TestOrder.cs:18`. The orderer sorts methods within each class; Phase04 needs `Customer` from Phase02 and Phase07 deletes it; neither class execution order nor standalone prerequisites are established.

**Assessment: CONFIRMED, Medium.** Works today by accident; the failure mode is a confusing cross-phase break, not a false pass.
- `TestOrder.cs:7-18` registers only an `ITestCaseOrderer` (method order within a class). No `ITestCollectionOrderer` exists and the whole suite is one `[Collection("Sequential")]`. xUnit 2.9 runs classes within a collection in discovery order, which is csproj compile-glob order, i.e. NTFS directory order of `Tests/*.cs`. Phase02 before Phase04 holds by file name only (`Phase02.Test_00a_CreateCustomerClass` → `Phase04.Test_05_ExistingCustomerStillWorks`, `Phase04:199`).
- A `--filter` run, a different runner, or a renamed file silently reorders.

**Fix:** Remove the dependency rather than fight xUnit 2: add `Test_00_EnsureCustomerExists` to `Phase04_HotLoadTests` that checks the Customer row via `DatabaseHelper` and, if absent, creates class + fields via `DatabaseHelper.InsertFieldViaDb` and deploys (same helper flow as Phase02 `Test_00a-c`). Alternative if hard ordering is wanted: per-phase collections plus an `ITestCollectionOrderer` sorting by DisplayName, at the cost of one Chromium launch per phase.

#### TEST-005: WaitForDeployRestartAsync can pass against the old server; add an /_instance marker and wait for it to change (ID: 1570)

**Codex (Medium):** `ServerHelper.cs:42`. After fixed sleeps, readiness accepts any HTTP < 500 without observing a new server generation; slow compilation leaves the old server answering, which then exits during the next test.

**Assessment: CONFIRMED, Medium.** This is the mechanism behind the "cold-start-window artifacts" recorded in memory.
- `ServerHelper.cs:40-48`: `WaitForTimeoutAsync(5000)` + `Task.Delay(5000)` + `WaitForServerAsync`, which returns on the first `< 500` status (line 25-26). Nothing distinguishes old process from new.
- Server side, exit happens only after DDL + Roslyn compile: `SchemaChangeController.cs:33-37` runs the hot-load on `Task.Run`, `SchemaChangeOrchestrator.cs:85-118` compiles first, then `Startup.cs:213-221` waits another 3 s before `Environment.Exit(42)`. When compile exceeds ~7 s (Phase08's ten-class bulk compile, cold Roslyn) the 10 s wait expires while the old server is alive, `.xaf-nav-link` is found on the old process, and the kill lands in the next test.
- No instance/version marker exists: `Startup.cs:185-191` maps only `/schemaUpdateHub`, controllers and `_Host`.

**Fix:** In `Startup.cs` inside `UseEndpoints`, before `MapFallbackToPage`: `endpoints.MapGet("/_instance", () => InstanceId);` with `private static readonly string InstanceId = Guid.NewGuid().ToString("N");`. In `ServerHelper`, `ClickDeploySchemaAsync` records `before = GET /_instance` (static), and `WaitForDeployRestartAsync` replaces the two fixed sleeps with a poll until `/_instance` differs from `before` (bounded by `serverTimeoutSeconds`), then continues with the existing `GotoRootToleratingRedirectAsync` + `.xaf-nav-link` wait.

#### TEST-006: AIChatPanel.WaitForResponseAsync accepts the previous turn's answer on multi-turn tests (ID: 1571)

**Codex (Medium):** `AIChatPanel.cs:91`. On a slow second turn the existing assistant message already satisfies "last message contains text"; confirmation tests can inspect the earlier proposal and later prompts race the outstanding response.

**Assessment: CONFIRMED, Medium.**
- `AIChatPanel.cs:84-98`: polls `m[m.length - 1].innerText.trim().length > 0` over `.dxbl-chatui-message-assistant`, never records a pre-send count. `SendMessageAsync` (`:70-81`) presses Enter, waits a fixed 500 ms, then this wait; on any second turn the previous bubble satisfies the predicate unless the new empty bubble is in the DOM within 500 ms.
- Tests that pass on the stale answer: `Phase11.Test_06` (`:196-204`) sends "create a ChatTestEntity" then "yes" and asserts `Contains("creat")`, which the turn-1 text already satisfies; `Test_14_MultiTurn` (`:380-382`) only asserts `Length > 0`. Test_07/16/18 are protected by their DB polls.

**Fix:** In `SendMessageAsync` (and `ClickSuggestionAsync`) capture `var before = await Page.Locator(AssistantMessages).CountAsync();` before Enter and call `WaitForResponseAsync(timeout, before)`; change the JS predicate to `m.length > args.before && m[m.length-1].innerText.trim().length > 0` with `new { sel = AssistantMessages, before }`. Keep a no-arg overload defaulting `before = 0`.

#### TEST-007: WaitForLoadingAsync is a 500 ms sleep on Blazor Server; FindInputByLabelAsync never auto-waits (ID: 1572)

**Codex (Medium):** `BasePage.cs:18`. `NetworkIdle` is already reached after New/Save, so only a 500 ms sleep remains; slow rendering makes one-shot field discovery inspect the previous or incomplete view.

**Assessment: CONFIRMED, Medium.**
- `BasePage.cs:16-20`: `WaitForLoadStateAsync(NetworkIdle)` + `WaitForTimeoutAsync(500)`. Blazor Server drives UI over the SignalR websocket, so NetworkIdle is already satisfied and returns immediately; net effect is a 500 ms sleep after `ClickNewAsync`/`ClickSaveAsync`/`ClickActionAsync` (`:44-80`).
- `DetailViewPage.cs:76-91` `FindInputByLabelAsync` probes with `CountAsync()` (no auto-wait) and throws immediately, so a DetailView rendering after 500 ms is "not found" or, if the previous view had a same-named field, the previous view's input is filled. Tests compensate with scattered `WaitForTimeoutAsync(2000)` after every `ClickNewAsync` (`Phase04:216-218`, `Phase08:137-140`, `Phase06:114-117`).

**Fix:** Make the page objects wait for the thing they need. In `FindInputByLabelAsync` replace the two `CountAsync` probes with one locator `$".dxbl-fl-ctrl:has([data-item-name='{label}']) :is(input:not([type='hidden']):not([type='checkbox']), textarea)"` and `await field.First.WaitForAsync(new() { State = Visible, Timeout = 10_000 })`; same one-line change in `FindContainerByLabelAsync`. Optionally `ClickNewAsync` waits for `.dxbl-fl-ctrl` visible instead of `WaitForLoadingAsync`. Then delete the scattered 2000 ms sleeps.

#### TEST-008: Phase06 graduation assertion reads an unfiltered CustomClasses row; soft-deleted GradTest from a prior run can satisfy it (ID: 1573)

**Codex (Medium):** `Phase06_GraduationTests.cs:202`. The query selects `GradTest` without filtering `GCRecord` and reads the first row; a rerun can inspect an old record's stale `GraduatedSource`.

**Assessment: CONFIRMED, Medium.** A broken graduation can pass every assertion on a rerun.
- `:201-205`: `SELECT "GraduatedSource", "Status" FROM "CustomClasses" WHERE "ClassName" = @name`, no GCRecord filter, single `reader.Read()`, unordered.
- Deferred deletion is on (`XafDynamicAssembliesDbContext.cs:65`) and the ClassName unique index is filtered `"GCRecord" = 0` (line 75), so multiple GradTest rows coexist. `Test_99_Cleanup` (`:253-262`) soft-deletes GradTest via the UI and only drops the table; the old row with `Status='Compiled'` and full `GraduatedSource` survives.
- `DatabaseHelper.cs:43-47` already uses the correct `("GCRecord" IS NULL OR "GCRecord" = 0)` filter.

**Fix:** Append `AND ("GCRecord" IS NULL OR "GCRecord" = 0)` to the query at line 202 and add `Assert.False(reader.Read(), "exactly one live GradTest row expected")` after the assertions. Optionally have `Test_99_Cleanup` hard-purge GradTest rows the way `Phase11.Test_99_Cleanup` (`:536-545`) purges the Approve action.

#### TEST-010: Phase04 Test_04_AddFieldViaNestedGrid cannot fail (swallowed try/catch, Assert.True(true) both branches) (ID: 1575)

**Codex (Medium):** `Phase04_HotLoadTests.cs:184`. Nested-grid exceptions are swallowed and both field-present and field-absent branches assert true; broken field creation stays green and the added column is never deployed or exercised.

**Assessment: CONFIRMED, Medium.** A test that cannot fail is worse than no test; it reports nested-grid field addition as covered when it is not. The `ponytail:` comment records it as a deliberate port of the Python soft assertion, so it is known, not accidental.
- `:153-170`: the whole nested-grid interaction sits in `try { ... } catch { /* best-effort */ }`.
- `:184-187`: `if (hasField) Assert.True(true) else Assert.True(true)`. The only way the test fails is `ReloadAndWaitAsync`/navigation throwing.

**Fix:** Make it real or delete it. Real: drop the try/catch, replace lines 184-187 with `Assert.True(hasField, "ProductName field should appear in Custom Field list")`, and if the multi-New-button heuristic (line 145) is the flaky part, target the nested grid explicitly (`.dxbl-grid` inside the Fields tab, then its `button[data-action-name="New"]`). If the nested-grid flow is not worth stabilising, remove the test; Phase10 (`:269`) and Phase06 already cover field addition via `DatabaseHelper.InsertFieldViaDb`.

## P3: Low

#### HOT-001: Hot-load swaps live CLR types in the dying process; delete the in-process swap (ID: 1557)

**Codex (High):** `SchemaChangeOrchestrator.cs:100` publishes new type identities while open views and OData registrations hold old ones; requests keep running ~3 s before the forced exit and a fresh DbContext can reject an old runtime type.

**Assessment: PARTIALLY CONFIRMED, Low.** Real but transient and self-healing via the restart.
- `SchemaChangeOrchestrator.cs:100-106`: `RuntimeEntityTypes = result.RuntimeTypes` (bumps ModelVersion), then `RegisterTypesInTypesInfo`, then `RefreshRuntimeTypes`; `Startup.cs:213-221` exits 3 s later. A DbContext built in that window uses the new `Type` objects while views hold the old ones: `dbContext.Set(oldType)` throws "not part of the model".
- The load context is non-collectible (`RuntimeAssemblyBuilder.cs:341-343`), so old types stay valid: failed requests, no torn types.
- Steps 4-6 are dead work: `RestartNeeded` is unconditionally true (line 112) and the new process recompiles from metadata in `EarlyBootstrap`.

**Fix:** Remove the in-process swap: delete lines 99-106 (and `RuntimeEntityTypes = Array.Empty` at line 78) from `ExecuteHotLoadAsync`, keeping DDL → compile (validates metadata) → `RestartNeeded = true` → `SchemaChanged`. `RegisterTypesInTypesInfo` and the hot-load caller of `Module.RefreshRuntimeTypes` become dead code; remove them too.

#### CFG-001: EasyTest build switches EF's connection string but bootstrap/DDL still use ConnectionString (ID: 1559)

**Codex (High):** `Startup.cs:43` and `~95`. With `EasyTestConnectionString` configured, the `EASYTEST` build switches EF at line 95, but `EarlyBootstrap`/`SchemaSynchronizer` still read `ConnectionString`, so DDL lands in the ordinary DB.

**Assessment: CONFIRMED but latent, Low.** No `EasyTestConnectionString` exists in `appsettings.json` today, so an EasyTest build uses one DB by accident rather than design. Becomes Medium the day one is added.
- `Startup.cs:43-44` sets `RuntimeConnectionString = Configuration.GetConnectionString("ConnectionString")` unconditionally; `EarlyBootstrap()` at line 49, `SchemaChangeOrchestrator.cs:55-66` and `Module.cs:191-199, 229-241` all use it.
- The `#if EASYTEST` override at `Startup.cs:93-97` only changes the string passed to `options.UseNpgsql`.

**Fix:** Resolve the connection string once at the top of `ConfigureServices` (`var connectionString = ...; #if EASYTEST override #endif`), assign it to `RuntimeConnectionString`, and use the same local in `WithDbContext`, deleting the duplicated lookup at lines 88-97.

#### TEST-003: Regression suite runs destructive SQL against the app's only database; add a localhost guard (ID: 1558)

**Codex (High):** `TestSettings.cs:27` defaults to the application's normal database; Phase07 deletes `Customer` metadata and cleanup phases run `DELETE`/`DROP TABLE` against it.

**Assessment: CONFIRMED but accepted for this repo, Low.** Would be High if the DB were ever anything but the throwaway docker volume.
- `TestSettings.cs:20-33` (`localhost:5434`, `XafDynamicAssemblies`, `xafdynamic`) is byte-identical to `appsettings.json` and `docker-compose.yml`, the only DB this repo has. Destructive SQL at `Phase07:60,123,291`, `Phase06:265`, `Phase08:65,221`, `Phase09:562`, `Phase12:74,430`.
- The tests are E2E against the running server, so they must share its DB by construction; the suite is designed around it (Phase07 deploys an empty runtime set).

**Fix:** Cheapest guard: in `DatabaseHelper` (line 19-21) refuse to run unless the resolved host is `localhost`/`127.0.0.1` and the DB name is the docker default, throwing with a clear message. Larger option if ever needed: a second `XafDynamicAssemblies_Test` database in `docker-compose.yml`, `run-server-mock.bat` overriding the connection string to it, `TestSettings.DbName` defaulting to the same.

#### TEST-009: Phase11 Test_18 asserts zero CustomActionSteps table-wide after deleting one action (ID: 1574)

**Codex (Medium):** `Phase11_AIChatMockedTests.cs:520`. After deleting one action the test requires zero live steps in the whole database; any unrelated or leftover Phase12 action fails it.

**Assessment: CONFIRMED, Low.** False failure, not false pass.
- `:516-523` polls `SELECT COUNT(*) FROM "CustomActionSteps" WHERE "GCRecord" IS NULL OR "GCRecord" = 0` with no join to the deleted action and asserts `0L`.
- Nothing seeds actions and Phase12 runs after Phase11 alphabetically, so a fresh DB passes. A Phase12 run that failed before its `Test_99_Cleanup` (`Phase12:416`), or a hand-made action in the shared dev DB, fails Test_18 for reasons unrelated to chat deletion. Test_16 (`:453-463`) already has the correctly scoped join.

**Fix:** Capture the action `"ID"` before the delete turn (`SELECT "ID" FROM "CustomActions" WHERE "Caption"='Approve' AND "TargetEntity"='SchemaHistory' AND live`), then poll `SELECT COUNT(*) FROM "CustomActionSteps" WHERE "CustomActionId" = @id AND live` for `0L`.

#### TEST-011: Mock describe_entity emits class_name but the tool parameter is entityName (same drift class as TEST-002) (ID: 1576)

**Codex (Low):** `ScriptMatcher.cs:90`. The mock supplies `class_name` while `DescribeEntity` requires `entityName`; binding fails, but the canned follow-up ignores the tool result and masks it.

**Assessment: CONFIRMED, Low.** No test currently depends on it, so it is latent.
- `ScriptMatcher.cs:89-90` emits `ToolUse("describe_entity", { ["class_name"] = ... })`; the real signature is `SchemaAIToolsProvider.DescribeEntity(string entityName)` (`:150-151`), registered at line 40.
- `AIChatService.ExecuteToolAsync` (`:233-237`) builds `AIFunctionArguments` from the JSON dict; Microsoft.Extensions.AI throws "Missing required parameter 'entityName'", the catch at `:240-244` turns it into a tool-result string, and the mock's "Here are the fields for the entity." is what the UI shows. Identical failure mode to the create_entity drift TEST-002 fixed (`ScriptMatcher.cs:118-122`).
- No Phase11 or MockLlmServerTests prompt sends a describe/show-fields message.

**Fix:** `ScriptMatcher.cs:90`: key `["entityName"]`. To end this class of drift, add one self-test in `MockLlmServerTests` that reads each `SchemaAIToolsProvider` AIFunction's `JsonSchema` properties and asserts every `ToolUse` input key `ScriptMatcher` emits for a sample prompt is in that set.

#### TEST-012: Phase08 Test_03_ConcurrentPageAccess does no concurrent work (ID: 1577)

**Codex (Low):** `Phase08_PerformanceTests.cs:155`. `ConcurrentPageAccess` opens one page and navigates sequentially; cross-circuit model races and deployment overlapping requests cannot fail it.

**Assessment: CONFIRMED, Low.**
- `:155-169` navigates the single `_page` to `PerfTest00_ListView`, waits for the grid, asserts `.dxbl-grid` visible. No second context, no `Task.WhenAll`. Line 151's `ponytail:` comment admits it was ported as-is from the Python test, which never opened a second context either. It duplicates Test_02's render check for a different entity.
- Relevant now because PERF-001 shows every new circuit re-bootstraps and bumps ModelVersion; a real concurrency test would exercise exactly that.

**Fix:** `BrowserFixture.NewPageAsync()` already gives independent contexts: `var pages = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _fixture.NewPageAsync()));` then `Task.WhenAll(pages.Select((p, i) => p.GotoAsync($"{BaseUrl}/PerfTest0{i}_ListView")))`, assert each has a visible `.dxbl-grid`, dispose the contexts. If not wanted, rename to `Test_03_RuntimeListViewRenders` so the name stops claiming concurrency.

#### TEST-013: Live AI tests report Passed instead of Skipped when AI_TEST_API_KEY is unset (ID: 1578)

**Codex (Low):** `Phase11_AIChatLiveTests.cs:53`. Without an API key the Facts print "SKIPPED" and return normally; xUnit reports success though nothing was exercised.

**Assessment: CONFIRMED, Low.** Judgment call rather than a defect: the header comment (`:13-16`) records the early-return as a deliberate decision to avoid the `Xunit.SkippableFact` package, and the documented regression command filters `Category!=LiveAI`, so the misleading green only appears on an unfiltered run of the full assembly.
- `:51-56` `SkipIfNoApiKey()` writes to the output helper and returns true; five tests do `if (SkipIfNoApiKey()) return;`.
- xUnit 2.9 (pinned in the csproj) has no `Assert.Skip`; dynamic skip needs `Xunit.SkippableFact` (`[SkippableFact]` + `Skip.If`), which reports Skipped through the VSTest adapter.

**Fix:** Add `Xunit.SkippableFact` 1.5.* to the test csproj, change the five `[Fact]` to `[SkippableFact]`, replace the early return with `Skip.If(TestSettings.AiTestApiKey == null, "AI_TEST_API_KEY not set")`, delete `SkipIfNoApiKey`. Or close as won't-fix and leave the existing comment.

#### CTRL-001: Graduation controllers subscribe anonymous handlers in OnActivated with no OnDeactivated (ID: 1579)

**Codex (Low):** `GraduationWarningController.cs:18` and `~54`. Activation attaches anonymous handlers without cleanup; reactivation against a retained view duplicates warnings and queries.

**Assessment: CONFIRMED, Low.** Pattern violation with bounded impact.
- `:18` `View.CurrentObjectChanged += (_, _) => ShowWarningIfGraduated();` and `:54` `View.CollectionSource.CollectionChanged += (_, _) => CheckForGraduatedEntities();`, both lambdas in `OnActivated`, neither class overrides `OnDeactivated`, so they can never be removed.
- Every re-activation (Active BoolList toggle, frame re-setting the view) stacks another handler and the toast fires N times. The handlers hang off the View/CollectionSource so they die with the view: duplicate firing plus controller-to-view retention, not a process-wide leak.

**Fix:** In both classes replace the lambdas with named methods and add `protected override void OnDeactivated() { View.CurrentObjectChanged -= View_CurrentObjectChanged; base.OnDeactivated(); }` (respectively `View.CollectionSource.CollectionChanged -= ...`). Standard `xaf-viewcontroller-patterns` shape.

(Completed work: see `docs/DONE.md`. Future ideas: `BACKBURNER.md`.)
