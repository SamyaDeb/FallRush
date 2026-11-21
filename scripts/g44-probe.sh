#!/usr/bin/env bash
# G4.4 LOCAL MEASUREMENT: prediction on Star Fountain (Mac dev server on StarScramble, 3 probe clients (BotBrain on the predicted state) + server bots = 12),
# bump flag off, localhost and through the 150 ms netem relay (75 ms ± 15 per direction, 2 % loss). Same setup as g4-bump-measure.sh.
# usage: scripts/g44-probe.sh [secs=150]   → .m1-logs/g4/probe-g44.out
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; SECS="${1:-150}"
OUT="$ROOT/.m1-logs/g4/probe-g44.out"; mkdir -p "$ROOT/.m1-logs/g4"; : > "$OUT"
run() { # <label> <url>
  bash "$ROOT/scripts/m1-macserver.sh" stop >/dev/null 2>&1; : > "$ROOT/.m1-logs/macserver.log"
  bash "$ROOT/scripts/m1-macserver.sh" start -fallrushMap StarScramble -fallrushBump 0 >/dev/null
  bash "$ROOT/scripts/m3-gate.sh" "$2" "$SECS" "$1" -fallrushBump 0 >> "$OUT" 2>&1
  bash "$ROOT/scripts/m1-macserver.sh" stop >/dev/null 2>&1
}
run g44-local ws://127.0.0.1:7777
bash "$ROOT/scripts/m2-netem.sh" start 75 15 2 >> "$OUT" 2>&1
run g44-150 ws://127.0.0.1:7778
bash "$ROOT/scripts/m2-netem.sh" stop >> "$OUT" 2>&1
echo "[g44-probe] done" >> "$OUT"
