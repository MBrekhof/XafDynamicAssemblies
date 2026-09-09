# Session Handoff — XafDynamicAssemblies

## Current Status: all 27 Codex-review cards + DATA-007 fixed on branch `fix/codex-review-2026-09` (pushed, NOT merged). TODO.md is empty.
## Next: review the branch and merge to master (user's call). Master itself still has 4 unpushed commits (SEC-004, 71bd193..5b52174) underneath the branch.

### Session 2026-09-09/10 — the fixing spree (loop session, Codex consulted per card)

Branch `fix/codex-review-2026-09`, 29 commits on top of master, one per card (shared files were
split by hunk), plus docs. Every card: plan or diff reviewed by Codex via the codex plugin
(`codex-companion.mjs task` for plans, `review --scope working-tree` for diffs); every catch
that changed the code is recorded in the DONE.md entry and the commit message. Board: all
cards in Review (SEC-003, AI-001 already confirmed Done by the user).

**Behaviour changes worth knowing before review (details in `docs/DONE.md`):**
- **Deploy never restarts into a broken state.** DDL failure (DATA-003) or compile failure
  (HOT-001) returns an error string from `ExecuteHotLoadAsync`; `SchemaChangeController`
  awaits it (`async void` Execute, the documented XAF Blazor shape) and shows an XAF error toast
  "Deploy aborted, …". The process keeps the previous type set. Degraded mode is now only
  reachable at process start.
- **Hot-load no longer loads the new assembly in the dying process** — `ValidateCompilation`
  only. `RegisterTypesInTypesInfo` is gone.
- **One validator.** `Module/Validation/MetadataValidator.cs` runs in `ValidateCompilation`,
  `Compile` (errors, not throw), `GenerateSource`/graduation (throw), and in the AI create/modify
  tools before commit. Identifier regexes anchor with `\z`. 63/61-char name caps (DATA-006).
- **Generated attributes were wrong for 26.1** (found by Codex while reviewing DATA-004): now
  `[DevExpress.ExpressApp.DC.FieldSize]` and `[ModelDefault("AllowEdit","False")]`;
  `GeneratedAttributeCompileTests` guards it.
- **A required reference on a populated table is refused** (DATA-003; Codex: "add as NULL"
  would break reads against the non-nullable Guid).
- **SchemaGuard (DATA-007)** skips fields whose column disagrees with the metadata; reasons in
  `validate_schema`. Three agreed deviations from the card (dangling ids only; FK retarget only
  for runtime targets; unknown PG types pass).
- **Tests:** `/_instance` per-process marker replaces the fixed restart sleeps; page objects
  auto-wait; 16 sleeps deleted; live AI tests report Skipped; DatabaseHelper refuses non-local
  DBs; Phase04 makes its own Customer; three real concurrent circuits in Phase08.

**Verification.** Full regression (non-live) on the branch: first run 191/4/1 → the 4 were
the Phase03 validation-toast race (fixed: bounded poll) and Phase04 Test_04 (fixed after a live
DOM check: the DetailView ribbon has no New, the nested grid's is a `dxbl-toolbar-item`); rerun
of Phase03+04 17/17, Phase07 8/8 incl. the new guard test. **Final full run with everything on
the branch (2026-09-10): 211 passed / 0 failed / 1 skipped (manual smoke), 35 min**; the 5 live
AI tests are excluded by the filter and report Skipped without a key.

**Suite counts:** see README's test table (Phase04 8, Phase07 8); unit tests: MetadataValidator
24, SchemaGuard 15, GeneratedAttributeCompile 1, StepValueConverter 10, MockLlm 7 + contract 1.

**Gotchas learned this session:**
- `dotnet build` fails while `run-server*.bat` runs (Module.dll locked by the server). Stop the
  server first; a scratch `server.ps1 start|stop` wrapper did that this session.
- A standalone rerun of a test that inserts bad metadata (Phase07 Test_01) leaves the row in
  the shared DB and the NEXT phase's deploy is (correctly) aborted by DATA-003. Clean up or run
  the phase to its cleanup.
- Playwright: `tr:has-text("Organization")` matched the Department row (NavigationGroup column).
- Git Bash heredocs with certain content fail to parse here; write scripts to a file and run.

---

### Session 2026-09-08 — Codex review carded, security wired

**Codex full-repo review** (27 findings) via the codex plugin. Every finding verified by three
parallel agents against source + installed DX 26.1 sources; all 27 hold, 8 re-graded. Cards
1554–1580 on the board.

**SEC-004 — XAF integrated security, shipped.** Template shape lifted from a fresh 26.1.4
Template Kit app at `C:\Projects\dxapplication2`; repo stays on 26.1.3. Admin / empty password
seeded in non-Release builds; OData needs a JWT from `POST /api/Authentication/Authenticate`.
Two non-obvious findings, in CLAUDE.md + memory: `Persist Security Info=True` on the Npgsql
string (DX MARS interceptor clones connections), and `--updateDatabase` once for new persistent
types (restart loop never runs the updater).

## How to Verify
```bash
dotnet build XafDynamicAssemblies.slnx        # 0 errors, 0 warnings
run-server-mock.bat                            # then wait ~30s before starting tests
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "Category!=LiveAI"
```

## Known Issues
- Server MUST be started via `run-server.bat`/`run-server-mock.bat` for deploy+restart (exit 42)
- A skipped required reference (SchemaGuard) leaves its NOT NULL column without a default:
  reads recover, inserts still fail until the metadata is fixed
- EF Core 10612 warning (Employee/Department navigation split) — harmless, accepted
