# Session Handoff — XafDynamicAssemblies

## Current Status: SEC-004 done (71bd193, 8b3bec5), NOT pushed. 26 Codex-review cards open in TODO.md.
## Next: re-evaluate the open cards against the now-secured app before any fixing spree (user's call).

### Session 2026-09-08 — Codex review carded, security wired

**Codex full-repo review** (27 findings) via the codex plugin. First launch from Git Bash died
silently (spawn from PowerShell instead). Every finding verified by three parallel agents
against source + installed DX 26.1 sources; all 27 hold, 8 re-graded. Cards 1554–1580 on the
board, bodies in TODO.md (file-synced repo). Each card carries Codex claim, verdict with
file:line evidence, and a concrete fix.

**SEC-004 — XAF integrated security, shipped.** Decision: real security ("nobody should
deploy without security"), template shape lifted from a fresh 26.1.4 Template Kit app at
`C:\Projects\dxapplication2`; repo stays on 26.1.3. Admin / empty password seeded in
non-Release builds; OData needs a JWT from `POST /api/Authentication/Authenticate`.
Regression 168/168 (37 min). Two non-obvious findings, in CLAUDE.md + memory:
`Persist Security Info=True` on the Npgsql string (DX MARS interceptor clones connections),
and `--updateDatabase` once for new persistent types (restart loop never runs the updater).

**Where the open cards stand after SEC-004:** SEC-003 (generator trusts metadata strings),
AI-001 (AI tools skip validation on the non-secured ObjectSpace) are unchanged; the AI chat
path still bypasses security by construction; AI-004 moved to P2 because the role tool is now
reachable and always fails. Priorities: 1 P0, 3 P1, 13 P2, 9 P3.

**Test-suite note:** BrowserFixture/ServerHelper log in per fresh context; `LoginPage` uses
`data-item-name='User Name'` (caption, not property name). Running `dotnet test --no-build`
against a stale test DLL produced 55 identical nav-link timeouts once this session; rebuild.

---

### Previous status: ACT-002 + TEST-001 + TEST-002 merged to master (0601031) and pushed

### Session 2026-07-31/08-01 — three deliverables, all merged in 0601031

**ACT-002 — AI-chat action verbs.** Four new AI tools (`list_actions`, `create_action`,
`delete_action`, `set_action_active`) in `SchemaAIToolsProvider` (10 → 14): the assistant
manages metadata actions (ACT-001's live DetailView buttons) via chat — live, no
deploy/restart. Validation mirrored in tool code (XAF rules don't fire on the non-secured
ObjectSpace). Phase 11 grew to 18 E2E; mock self-tests to 7. README has usage examples.

**TEST-001 — deploy-restart nav race fixed.** Shared `GotoRootToleratingRedirectAsync` in
`ServerHelper` tolerates the post-restart shortcut-restore navigation aborting the helper's
root navigation (bounded retry). Covers WaitForDeployRestartAsync + ReloadAndWaitAsync.

**TEST-002 — mock create_entity drift fixed.** Mock now sends the real parameter names
(`className`/`navigationGroup`/`description`/`fieldsJson`); Test_07 asserts the real
CustomClasses row. Mocked create-entity coverage is genuine for the first time.

### Verification story (important context for future full runs)
- Regression #2 (pre-fixes): 167/1/1 — the 1 was the TEST-001 race, then fixed.
- Regression #3 (post-fixes): 163/5/1 — **all 5 failures are cold-start-window artifacts**
  (first form interactions against a seconds-old server process: P01 T02–05, P03 T01 toast
  race). Warm standalone reruns: Phase01 11/11, Phase03 9/9, Phase09 19/19. Everything the
  branch touches (deploy phases, Phase09, Phase11 incl. the new real-DB asserts, Phase12)
  was green in #3.
- **Cold-start gotcha:** starting `dotnet test` the instant the server answers HTTP 200
  invites first-render timeouts in the first test class; the suite also ENDS with a
  deploy-restart, so a rerun chained immediately after a full run hits a newborn process
  again. Leave a warm-up gap (or prime a real view) before starting the suite.
- **Suite-order gotcha (learned via regression #1):** Phase07's cleanup deletes `Customer`
  and deploys an empty runtime set — runtime entities from early phases do NOT survive to
  Phase 11. Phase 11's action tests target compiled `SchemaHistory` for exactly this reason.

### Lifecycle
- Board: cards 1139 (ACT-002), 1140 (TEST-001), 1141 (TEST-002) completed → Review;
  DONE.md entries written; TODO.md empty.
- Earlier same session, already on master: SEC-002 closed (HtmlSanitizer 9.1.974 +
  Crypto.Xml 10.0.10 override, zero-warning build); Phase11 wait-race fix (bc430d7).

## How to Verify
```bash
dotnet build XafDynamicAssemblies.slnx        # 0 errors, 0 warnings
run-server-mock.bat                            # then wait ~30s before starting tests
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "Category!=LiveAI"
```

## Known Issues
- Server MUST be started via `run-server.bat`/`run-server-mock.bat` for deploy+restart (exit 42)
- Cold-start sensitivity: see Verification story above
- Live AI tests report "passed" (early-return) when `AI_TEST_API_KEY` unset — by design
- EF Core 10612 warning (Employee/Department navigation split) — harmless, accepted
