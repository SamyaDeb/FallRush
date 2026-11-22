#!/usr/bin/env bash
# P1-N smoke (locked): launches the Feel Lab Mac build on every round map with -autotest (jump / dive / run under both
# engines, overlay log, one screenshot per engine per map), waits for it to quit.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/client-unity/Builds/Mac-p1-feellab/FallRush.app"
OUT="$ROOT/docs/proof/p1/feellab"
mkdir -p "$OUT"
rm -f "$OUT/autotest.log"
"$APP/Contents/MacOS/Fall Rush" -feellab -map all -autotest -autotestOut "$OUT" -logFile "$ROOT/.m1-logs/p1-smoke-player.log" &
PID=$!
for i in $(seq 1 240); do kill -0 $PID 2>/dev/null || break; sleep 1; done
if kill -0 $PID 2>/dev/null; then echo "[p1] smoke: still running after 240 s, stopping it"; kill $PID; fi
ls -la "$OUT"; cat "$OUT/autotest.log" 2>/dev/null | tail -40
