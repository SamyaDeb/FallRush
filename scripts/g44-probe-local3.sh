#!/usr/bin/env bash
# G4.5 carry-over: re-run the G4.4 Star Fountain prediction probe on LOCALHOST only, 3 times (3 probe clients + 9 server bots = 12, bump off).
# usage: scripts/g44-probe-local3.sh [secs=150]   → .m1-logs/g4/probe-g44-local3.out
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; SECS="${1:-150}"
OUT="$ROOT/.m1-logs/g4/probe-g44-local3.out"; mkdir -p "$ROOT/.m1-logs/g4"; : > "$OUT"
for r in 1 2 3; do
  bash "$ROOT/scripts/m1-macserver.sh" stop >/dev/null 2>&1; : > "$ROOT/.m1-logs/macserver.log"
  bash "$ROOT/scripts/m1-macserver.sh" start -fallrushMap StarScramble -fallrushBump 0 >/dev/null
  bash "$ROOT/scripts/m3-gate.sh" ws://127.0.0.1:7777 "$SECS" "g44-local-r$r" -fallrushBump 0 >> "$OUT" 2>&1
  bash "$ROOT/scripts/m1-macserver.sh" stop >/dev/null 2>&1
done
echo "[g44-probe-local3] done" >> "$OUT"
