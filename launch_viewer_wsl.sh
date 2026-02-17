#!/bin/sh
set -eu

APP_DIR="$1"
LOG_FILE="/tmp/codex-sessions-viewer.log"
PID_FILE="/tmp/codex-sessions-viewer.pid"

cd "$APP_DIR"

PY_CMD=""
if command -v python3 >/dev/null 2>&1; then
  PY_CMD="python3"
elif command -v python >/dev/null 2>&1; then
  PY_CMD="python"
else
  exit 11
fi

if command -v nohup >/dev/null 2>&1; then
  nohup env HOST=0.0.0.0 "$PY_CMD" viewer.py >"$LOG_FILE" 2>&1 </dev/null &
else
  env HOST=0.0.0.0 "$PY_CMD" viewer.py >"$LOG_FILE" 2>&1 </dev/null &
fi
echo "$!" >"$PID_FILE"
