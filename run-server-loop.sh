#!/bin/bash
cd /c/Projects/XafDynamicAssemblies/XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server
while true; do
    dotnet run --no-build 2>&1
    EXIT_CODE=$?
    echo "[WRAPPER] Server exited with code $EXIT_CODE"
    if [ $EXIT_CODE -eq 42 ]; then
        echo "[WRAPPER] Restarting in 1 second..."
        sleep 1
    else
        echo "[WRAPPER] Not restarting (exit code != 42)"
        break
    fi
done
