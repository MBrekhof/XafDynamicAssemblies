@echo off
REM Wrapper: runs the server with AI calls routed to the in-process mock LLM (port 5555).
REM Required for the Phase 11 mocked AI-chat tests — without this, the app calls the real
REM LLM provider and Phase 11 fails with generic chat errors. See run-server.bat for the
REM exit-code-42 restart-loop semantics, which this preserves via `call`.
set AI_MOCK_LLM_BASE_URL=http://localhost:5555
call "%~dp0run-server.bat"
