#!/usr/bin/env bash
# G4.1c LOCAL MEASUREMENT: the M3 crowd gate (Mac dev server on BlockPartyDash, 3 probe race clients + server bots = 12)
# with the bump stagger flag off and on, localhost and through the 150 ms netem relay (75 ms ± 15 per direction, 2 % loss).
# usage: scripts/g4-bump-measure.sh [secs=150]   → .m1-logs/probe-g4bump-<cond>-<n>.log + summaries in .m1-logs/g4/bump.out
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; SECS="${1:-150}"
OUT="$ROOT/.m1-logs/g4/bump.out"; mkdir -p "$ROOT/.m1-logs/g4"; : > "$OUT"
run() { # <label> <url> <bump 0|1>
  bash "$ROOT/scripts/m1-macserver.sh" stop >/dev/null 2>&1; : > "$ROOT/.m1-logs/macserver.log"
  bash "$ROOT/scripts/m1-macserver.sh" start -fallrushMap BlockPartyDash -fallrushBump "$3" >/dev/null
  bash "$ROOT/scripts/m3-gate.sh" "$2" "$SECS" "$1" -fallrushBump "$3" >> "$OUT" 2>&1
  echo "[g4-bump] $1 server: $(grep -o 'pushesTotal=[0-9]* bumpsTotal=[0-9]* bumpFlag=[0-9]' "$ROOT/.m1-logs/macserver.log" | tail -1)" >> "$OUT"
  bash "$ROOT/scripts/m1-macserver.sh" stop >/dev/null 2>&1
}
run g4bump-off-local ws://127.0.0.1:7777 0
run g4bump-on-local ws://127.0.0.1:7777 1
bash "$ROOT/scripts/m2-netem.sh" start 75 15 2 >> "$OUT" 2>&1
run g4bump-off-150 ws://127.0.0.1:7778 0
run g4bump-on-150 ws://127.0.0.1:7778 1
bash "$ROOT/scripts/m2-netem.sh" stop >> "$OUT" 2>&1
echo "[g4-bump] done" >> "$OUT"
