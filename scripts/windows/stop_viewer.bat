@echo off
setlocal EnableExtensions EnableDelayedExpansion

wsl.exe -d Ubuntu -- sh -lc "if [ -f /tmp/codex-sessions-viewer.pid ]; then kill \"$(cat /tmp/codex-sessions-viewer.pid)\" 2>/dev/null || true; rm -f /tmp/codex-sessions-viewer.pid; elif command -v pkill >/dev/null 2>&1; then pkill -f \"python3 viewer.py\" || true; pkill -f \"python viewer.py\" || true; fi"

endlocal
