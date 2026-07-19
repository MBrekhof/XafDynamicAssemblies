# Session Handoff — XafDynamicAssemblies

## Current Status: On .NET 10 / EF Core 10 — full regression green
## Full regression passed 2026-07-19 (net10.0): 163 passed / 0 failed / 1 skipped (~32 min)

### Session 2026-07-19 (afternoon) — NET-001 .NET 10 upgrade (merged to master)

- TFM net8.0 → net10.0 in all three projects; EF Core 8.0.18 → 10.0.10;
  Npgsql.EntityFrameworkCore.PostgreSQL 8.0.11 → 10.0.3; Npgsql (Tests) 8.0.6 → 10.0.3.
- **Roslyn 4.10.0 → 5.0.0 was forced**: on net10.0, DevExpress.ExpressApp.EFCore 26.1.3
  depends on Microsoft.CodeAnalysis.Workspaces.MSBuild 5.0.0 which pins Workspaces.Common
  = 5.0.0 exactly → NU1107 against our 4.10 pin. All three Microsoft.CodeAnalysis.* refs
  now 5.0.0 (exact — don't float to 5.3/5.6 or the pin conflicts return).
- Code fix: XAF0035 (new analyzer warning) in SchemaExportImportController.GetCurrentUserName —
  SecuritySystem.CurrentUser static → Application.ServiceProvider.GetService<ISecurityStrategyBase>()
  (documented DX pattern, docs.devexpress.com/eXpressAppFramework/405775).
- RuntimeAssemblyBuilder unchanged — TRUSTED_PLATFORM_ASSEMBLIES + loaded-assembly refs are
  version-agnostic; runtime compilation, deploy/restart, hot-load all green on .NET 10.
- Earlier same session: README refreshed (26.1, metadata-actions usage section, test
  inventory incl. SchemaSyncCaseSensitivityTests; counts verified: 153 E2E + 10 unit +
  5 mock = 163-run).

## Known Warnings (accepted)
- **NU1902 AngleSharp 0.17.1** (moderate, GHSA-pgww-w46g-26qg) via HtmlSanitizer 9.0.892 —
  no patched stable upstream; tracked as SEC-002 (ID: 1054) in TODO.md. Do NOT suppress.
- EF Core 10612 (Employee/Department navigations split into two relationships) — runtime-entity
  metadata from old test runs; harmless, DDL comes from SchemaSynchronizer not EF migrations.

## How to Verify
```bash
dotnet build XafDynamicAssemblies.slnx        # 0 errors, only NU1902 warnings
run-server-mock.bat                            # mock mode — required for Phase 11 AI-chat tests
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "Category!=LiveAI"
```

## Open Items (TODO.md)
- SEC-002 (ID: 1054): AngleSharp advisory — waiting on an HtmlSanitizer release built on
  AngleSharp 1.x. Backburner ideas unchanged in BACKBURNER.md.

## Known Issues (unchanged)
- Server MUST be started via `run-server.bat`/`run-server-mock.bat` for deploy+restart (exit 42)
- Phase04 standalone needs Phase02's `Customer` entity (full-suite order satisfies it)
- Live AI tests report "passed" (early-return) when `AI_TEST_API_KEY` unset — by design
- After failed test runs with stale state: kill server, clean bad metadata rows, restart
