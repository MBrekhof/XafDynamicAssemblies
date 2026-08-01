# Session Handoff — XafDynamicAssemblies

## Current Status: ACT-002 (AI-chat action verbs) implemented on feature branch, full regression in progress
## Branch: feature/act-002-ai-action-verbs (7 code/docs commits on top of master @ 93d812a)

### Session 2026-07-31 (afternoon/evening) — ACT-002 via subagent-driven development

Four new AI tools in `SchemaAIToolsProvider` (10 → 14): `list_actions`, `create_action`,
`delete_action`, `set_action_active` — the AI schema assistant now manages metadata actions
(ACT-001's live DetailView buttons) through chat. Unlike entity changes, these are LIVE with
no deploy/restart.

- **Commits:** d617709 (tools) → ccc62f3 (system prompt paragraph) → dccfcc9 (mock LLM
  matchers + 2 self-tests) → 26197b8 (Phase 11 E2E Test_16–18) → 41880f3 (doc counts) →
  6e95f1f (final-review fix wave). Spec + plan in `docs/superpowers/`.
- **create_action** mirrors the XAF save rules in code (they don't fire on the non-secured
  tool ObjectSpace): hard errors for missing/duplicate/invalid steps, ≤1 OpenView; soft
  warnings for unparseable criteria / unknown target entity / 10-slot ceiling. SortOrder
  from JSON array position. `Enum.IsDefined` guard blocks numeric-string enum bypass.
- **Tests:** Phase 11 grew 15 → 18 E2E (create-via-chat with DB-row asserts incl. GCRecord
  soft-delete filter, live button-render on Customer DetailView, toggle+delete with hard
  purge in Test_99_Cleanup); mock self-tests 5 → 7. All green: 18/18 Phase 11, 7/7 mock,
  build 0 warnings. Final whole-branch review: ready to merge; its 3 findings fixed
  (6e95f1f) and re-review-confirmed.
- **Known gotcha reaffirmed:** the chat UI displays only the LLM's follow-up text, never raw
  tool results — E2E assertions must target the DB or rendered UI. The pre-existing
  create_entity mock drift (`class_name` vs `className` — that tool silently errors in
  mocked runs, tests pass on canned text) is NOT fixed on this branch; candidate follow-up.
- **Test_16–18 dependency:** `Customer` entity from Phase02 (same precedent as Phase04).

### Earlier same day (already merged to master)
- SEC-002 closed: HtmlSanitizer 9.1.974 (NU1902 gone) + System.Security.Cryptography.Xml
  10.0.10 override (NU1903, CVE-2026-50648) — zero-warning build (bc430d7).
- Phase11 Test_10/Test_02 flake root-caused: `AIChatPanel.WaitForResponseAsync` now waits
  for non-empty text in the last assistant bubble (DxAIChat renders tool_use turns as empty
  assistant messages while tools execute).

## Next Steps
- Full ~168-test regression running (`Category!=LiveAI`, server via `run-server-mock.bat`);
  merge to master + close card 1139 after it's green.
- Backburner: ACT-001 fast-follows remaining — ListView targets, expression values;
  create_entity mock drift fix.

## How to Verify
```bash
dotnet build XafDynamicAssemblies.slnx        # 0 errors, 0 warnings
run-server-mock.bat                            # mock mode — required for Phase 11 AI-chat tests
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "Category!=LiveAI"
```

## Known Issues (unchanged)
- Server MUST be started via `run-server.bat`/`run-server-mock.bat` for deploy+restart (exit 42)
- Phase04 standalone needs Phase02's `Customer` entity (full-suite order satisfies it)
- Live AI tests report "passed" (early-return) when `AI_TEST_API_KEY` unset — by design
- After failed test runs with stale state: kill server, clean bad metadata rows, restart
- EF Core 10612 warning (Employee/Department navigation split) — harmless, accepted
