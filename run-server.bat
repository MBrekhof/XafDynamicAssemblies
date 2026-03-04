@echo off
REM Wrapper script to run the Blazor server with auto-restart.
REM When Deploy Schema changes the type set, the server exits with code 42.
REM This script detects that exit code and restarts the process.

:loop
dotnet run --project "%~dp0XafDynamicAssemblies\XafDynamicAssemblies.Blazor.Server" --no-build
if %ERRORLEVEL% EQU 42 (
    echo [WRAPPER] Server requested restart, restarting in 1 second...
    timeout /t 1 /nobreak >nul
    goto loop
)
echo [WRAPPER] Server exited with code %ERRORLEVEL%
