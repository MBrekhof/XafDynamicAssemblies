# DONE — XafDynamicAssemblies

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
