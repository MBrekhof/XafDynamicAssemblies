# Session Handoff — XafDynamicAssemblies

## Current Status: dependency advisories all cleared — zero NU19xx warnings
## Verified 2026-07-31: clean build (0 warnings) + Phase 11 mocked suite 15/15

### Session 2026-07-31 — SEC-002 closed (HtmlSanitizer 9.1.974) + Phase11 flake root-caused

- **HtmlSanitizer 9.0.892 → 9.1.974 stable** (Module + Blazor.Server). Depends on
  AngleSharp ≥1.6.0 (patched; advisory GHSA-pgww-w46g-26qg fixed in 1.5.0) — NU1902 gone.
  Usage surface unchanged (`AllowedTags.Add`, `Sanitize`); markdown/table rendering verified
  via Phase 11.
- **New NU1903 batch fixed same session**: System.Security.Cryptography.Xml 8.0.3 (5× high,
  CVE-2026-50648 batch, advisories published after 07-19) transitive via
  DevExpress.Printing.Core → direct override to **10.0.10** in Module.csproj.
- **Phase11 Test_10 known flake root-caused and fixed** (also hit Test_02 on cold start):
  `AIChatPanel.WaitForResponseAsync` treated "first assistant bubble visible + 500 ms" as
  "response ready", but DxAIChat renders a tool_use turn as an EMPTY assistant message while
  the server-side tool runs (validate_schema = Roslyn compile, seconds) — the read raced the
  content. Now `WaitForFunctionAsync` polls for non-empty text in the LAST assistant message.
  Evidence: failing payloads were plain text while markdown-heavy responses passed
  (sanitizer exonerated); Test_02 passed warm, Test_10 failed deterministically until the
  wait fix; DATA-001's notes already recorded Test_10 as a known flake on 07-19.
- Not run this session: the full ~32-min regression (Phase 11 covers the sanitizer's only
  consumer; the Crypto.Xml override is audit-level). Run it before the next release-ish
  milestone if you want belt-and-braces.

## Open Items (TODO.md)
- None. Backburner ideas in `BACKBURNER.md` (Runtime Scripted ViewControllers; ACT-001
  fast-follows: AI-chat action verbs, ListView targets, expression values).

## How to Verify
```bash
dotnet build XafDynamicAssemblies.slnx        # 0 errors, 0 warnings
run-server-mock.bat                            # mock mode — required for Phase 11 AI-chat tests
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "Category!=LiveAI"
```

## Known Warnings (accepted)
- EF Core 10612 (Employee/Department navigations split into two relationships) — runtime-entity
  metadata from old test runs; harmless, DDL comes from SchemaSynchronizer not EF migrations.

## Known Issues (unchanged)
- Server MUST be started via `run-server.bat`/`run-server-mock.bat` for deploy+restart (exit 42)
- Phase04 standalone needs Phase02's `Customer` entity (full-suite order satisfies it)
- Live AI tests report "passed" (early-return) when `AI_TEST_API_KEY` unset — by design
- After failed test runs with stale state: kill server, clean bad metadata rows, restart
