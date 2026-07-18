# TODO — XafDynamicAssemblies

## P1: High

#### TEST-001: Migrate Playwright tests from Python to .NET (ID: 1048)

Port the 104 Playwright tests (12 files, 10 phases), page objects, and mock LLM harness from
Python/pytest to C# `Microsoft.Playwright` + NUnit, run via `dotnet test`.

- Plan: `docs/plans/2026-03-21-playwright-dotnet-migration.md` (21 tasks: 1-7 infrastructure,
  8-19 port test phases, 20-21 cleanup + full verification)
- Tests must handle the deploy → exit-code-42 → restart cycle (reconnect logic exists in the
  Python fixtures; port it)
- `@pytest.mark.live_ai` → NUnit category so live-AI tests stay opt-in
- After migration: drop `Dockerfile.python` and the `python-tests` service from docker-compose

#### DX-001: Upgrade DevExpress XAF 25.2.3 → 26.1.3 (ID: 1049)

Bump all DevExpress packages to 26.1.3, fix breaking changes, verify with the migrated .NET
Playwright suite. Ordered after TEST-001 so any selector/behavior breakage is fixed once in the
suite we keep. Consult dxdocs MCP + installed 26.1 sources (`C:\Program Files\DevExpress 26.1`)
for API changes — never assume from memory.
