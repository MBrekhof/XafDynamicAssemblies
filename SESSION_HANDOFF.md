# Session Handoff — XafDynamicAssemblies

## Current Status: Test suite on .NET, DevExpress on 26.1.3 — full regression green
## Full regression passed 2026-07-19: 143 passed / 0 failed / 1 skipped (~27 min)

### Session 2026-07-18/19 — Two migrations completed and merged to master

**1. Playwright test migration Python → .NET (TEST-001, merged 984f5cc)**
- New project `XafDynamicAssemblies/XafDynamicAssemblies.Tests` (xUnit + Microsoft.Playwright + Npgsql):
  143 phase tests (12 files, Phases 1–11) + 5 mock-LLM self-tests + 1 manual smoke test
- Page objects, DatabaseHelper/ServerHelper, BrowserFixture, in-process ASP.NET Core mock LLM
  server (Anthropic + OpenAI wire formats) — all Python-parity ports, per-task reviewed
- Python stack deleted: `tests/`, `Dockerfile.python`, compose `python` service
- `run-server-mock.bat` added: sets `AI_MOCK_LLM_BASE_URL=http://localhost:5555` so the app
  routes LLM calls to the in-process mock — REQUIRED for Phase 11 mocked tests / full regression
- Live AI tests opt-in: `AI_TEST_API_KEY` env var + `--filter "Category=LiveAI"`

**2. DevExpress XAF 25.2.3 → 26.1.3 (DX-001, merged 9b73c3b)**
- 28 DevExpress packages bumped; build 0 warnings
- Product fixes: `AIChat.razor` `.Content`→`.Text`; `WebApiOptions.UseResourceDelta = false`
  in Startup.cs (26.1 defaults true under `Latest` compat mode → OData writes on runtime
  entities 500'd; ResourceDelta<T> needs deserializer wiring EF Core apps don't get)
- Test fixes for 26.1 Ribbon DOM: selectors now `dxbl-toolbar-item > button[data-action-name=...],
  dxbl-bar-item > button[data-action-name=...]` — NOTE: `data-action-name` = action CAPTION,
  not Id (verified in 26.1 sources)

**Not pushed** — master is ahead of origin/master; push when ready.

## How to Verify
```bash
dotnet build XafDynamicAssemblies.slnx
run-server-mock.bat   # mock mode — required for Phase 11 AI-chat tests
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "Category!=LiveAI"
```

### Follow-up session 2026-07-19 (same day)
- DATA-001 FIXED (merged 97211a8): SchemaSynchronizer column-existence check now
  case-sensitive (Ordinal); E2E repro test `SchemaSyncCaseSensitivityTests` added.
- SEC-001 dropped — exposed key confirmed old/invalid by owner.
- Known flake (1st occurrence): Phase11 Test_10_ValidateSchema returned empty chat response
  once in a full-suite run; green in isolated re-run and in all prior regressions.

### ACT-001 shipped (2026-07-19, merged d550fd7)
- Metadata-driven action builder: live SetField/ShowMessage/OpenView buttons on DetailViews,
  no restart. Slot-pool dispatcher (10 slots — XAF Blazor never renders dynamically-created
  OnActivated actions; verified in DX sources). Spec/plan in `docs/superpowers/`.
  Suite now 152 E2E + 10 converter unit tests + mock/self tests; regression 163/0/1.

## Open Items (TODO.md)
- None. Fast-follow ideas (AI-chat action verbs, ListView targets, expression values) are
  parked in BACKBURNER.md.

## Known Issues
- Server MUST be started via `run-server.bat`/`run-server-mock.bat` for deploy+restart (exit 42)
- Phase04 standalone needs Phase02's `Customer` entity (full-suite order satisfies it)
- Live AI tests report "passed" (early-return) when `AI_TEST_API_KEY` unset — by design
- After failed test runs with stale state: kill server, clean bad metadata rows, restart
