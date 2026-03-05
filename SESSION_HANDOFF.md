# Session Handoff — XafDynamicAssemblies

## Current Status: All 10 Phases Complete — 104/104 Tests Passing
## Full regression passed on 2026-03-04

### Session 2026-03-05 — Graduation fixes, Test Compile UX, Partial class support

**Test Compile moved to ListView:**
- `TestCompileController` changed from `ObjectViewController<DetailView>` to `ObjectViewController<ListView>`
- Caption: "Test Compile All" — reflects that it always compiled all runtime classes
- `SelectionDependencyType.Independent` so the button is always enabled
- Tests in phase 3 and phase 9 updated to match

**Graduation bug fix:**
- `BlazorApplication.DatabaseVersionMismatch` now always auto-updates the database
- Previously, in non-debugger mode (run-server.bat restart loop), it threw an InvalidOperationException on schema mismatch after graduation removed a type from the EF Core model
- This was the root cause of the "deploy crashes after graduation" bug

**Visual warnings for graduation:**
- Appearance rules on `CustomClass`: Compiled entities → gray italic, Graduating → orange italic (uses `DevExpress.Drawing.DXFontStyle`)
- New `GraduationWarningController.cs` with two controllers:
  - `GraduationWarningDetailController` — shows warning banner when viewing non-Runtime entity
  - `GraduationWarningListController` — shows warning when graduated entities exist in the list
- Improved Graduate action confirmation dialog with detailed explanation of consequences

**GenerateAsPartial option:**
- New `GenerateAsPartial` bool on `CustomClass`
- When true, `GraduationService` generates `public partial class Foo : BaseObject` without class-level attributes ([DefaultClassOptions], [NavigationItem], [DefaultProperty])
- Allows developer to provide attributes on a hand-written partial class

**New file:** BACKBURNER.md — future ideas for runtime scripted ViewControllers (Monaco editor, cs-script integration)

**Files changed:**
- `Module/BusinessObjects/CustomClass.cs` — Added GenerateAsPartial, Appearance attributes
- `Module/Controllers/TestCompileController.cs` — Moved to ListView
- `Module/Controllers/GraduateController.cs` — Improved confirmation message
- `Module/Controllers/GraduationWarningController.cs` — NEW
- `Module/Services/GraduationService.cs` — Partial class support
- `Blazor.Server/BlazorApplication.cs` — Always auto-update DB on version mismatch
- `tests/tests/test_phase3_validation.py` — Updated for ListView Test Compile
- `tests/tests/test_phase9_review_fixes.py` — Updated for ListView Test Compile
- `BACKBURNER.md` — NEW

## Test Results (per-phase standalone)
- **104 tests total** across Phases 1-10
- Phase 1: 11 tests (metadata CRUD)
- Phase 2: 13 tests (runtime entity setup + compilation + CRUD)
- Phase 3: 9 tests (validation, type dropdown, test compile)
- Phase 4: 7 tests (hot-load deploy, nav, field add, data survival)
- Phase 5: 8 tests (entity relationships, FK constraints)
- Phase 6: 9 tests (graduation, source generation, data preservation)
- Phase 7: 7 tests (degraded mode, error recovery, empty metadata, restart recovery)
- Phase 8: 4 tests (bulk 10-class compilation, CRUD, concurrent access)
- Phase 9: (review fixes)
- Phase 10: 36 tests (Web API OData endpoints, Swagger, CRUD, query features, IsApiExposed toggle)

## What Was Done

### Phase 10 — Web API (OData)
- `Module/BusinessObjects/CustomClass.cs` — Added `IsApiExposed` bool property
- `Module/BusinessObjects/XafDynamicAssembliesDbContext.cs` — Added `HasDefaultValue(false)` for IsApiExposed
- `Module/Module.cs` — Added `EarlyBootstrap()`, `ApiExposedClassNames`, updated `QueryMetadata()` SQL with defensive column check
- `Blazor.Server/Startup.cs` — Added `AddXafWebApi()`, `AddControllers().AddOData()`, Swagger, `EarlyBootstrap()` call
- `Blazor.Server/XafDynamicAssemblies.Blazor.Server.csproj` — Added DevExpress.ExpressApp.WebApi + Swashbuckle NuGet packages
- `Module/Services/GraduationService.cs` — Added Web API note to graduation output when IsApiExposed=true
- `tests/tests/test_phase10_web_api.py` — 36 tests (Swagger, OData CRUD, query features, API toggle)

### Phase 6 — Graduation
- `Module/Services/GraduationService.cs` — Generates production C# source + DbContext snippet + migration note
- `Module/Controllers/GraduateController.cs` — SimpleAction on CustomClass DetailView: generates source, sets Status=Compiled
- `Module/BusinessObjects/CustomClass.cs` — Added `GraduatedSource` property for storing generated code
- `tests/tests/test_phase6_graduation.py` — 9 tests

### Phase 7 — Error Handling + Hardening
- `Module/Module.cs` — Added `DegradedMode` / `DegradedModeReason` static properties; improved `BootstrapRuntimeEntities` with separate DDL/compilation error handling
- `Module/Services/SchemaChangeOrchestrator.cs` — DDL failures non-fatal (extra columns harmless); compilation failures trigger restart; always `RestartNeeded = true`
- `tests/tests/test_phase7_error_handling.py` — 7 tests

### Phase 8 — Performance + Polish
- `tests/tests/test_phase8_performance.py` — 4 tests (bulk create 10 classes, deploy, CRUD, concurrent page loads)

## What Was Fixed (2026-03-04)
- **Graceful shutdown hang**: `RestartService.RequestRestart()` used `StopApplication()` which hung
  due to active Blazor SignalR connections. Changed to `Environment.Exit(42)` in `Startup.cs`
  for force-exit, allowing `run-server.bat` to detect exit code 42 and restart cleanly.

## How to Verify
```bash
dotnet build XafDynamicAssemblies.slnx
run-server.bat
docker exec xaf-dynamic-python bash -c "cd /workspace && python3 -m pytest tests/tests/ -v --timeout=180"
```

## Known Issues
- Non-collectible ALC — types persist in memory across hot-loads. Works with process-level restart.
- Server MUST be started via `run-server.bat` for deploy+restart to work.
- Adding new EF Core columns to CustomClass requires `--updateDatabase --forceUpdate --silent`.
- After failed test runs that leave stale state, server must be killed and restarted fresh.
