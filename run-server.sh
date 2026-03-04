#!/bin/bash
# Wrapper script to run the Blazor server with auto-restart.
# When Deploy Schema changes the type set, the server exits with code 42.
# This script detects that exit code and restarts the process.

PROJECT_DIR="$(dirname "$0")/XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server"

while true; do
    dotnet run --project "$PROJECT_DIR" --no-build "$@"
    EXIT_CODE=$?

    if [ $EXIT_CODE -eq 42 ]; then
        echo "[WRAPPER] Server requested restart, restarting in 1 second..."
        sleep 1
    else
        echo "[WRAPPER] Server exited with code $EXIT_CODE"
        exit $EXIT_CODE
    fi
done
