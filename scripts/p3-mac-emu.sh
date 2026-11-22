#!/usr/bin/env bash
# P3-D (locked): chain emulator (default shaping) + the Mac dev player in on-chain auto-play for N seconds.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p3-mac-emu -- bash scripts/p3-mac-emu.sh [seconds] [extra emulator args…]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
SECS="${1:-90}"; shift || true
STAMP=$(date +%Y%m%d-%H%M%S)
node tools/chain-emulator/src/main.mjs --log ".m1-logs/p3-emu-$STAMP.jsonl" "$@" > ".m1-logs/p3-emu-$STAMP.out" 2>&1 &
EMU=$!
trap 'kill $EMU 2>/dev/null || true' EXIT
sleep 1
APP="client-unity/Builds/Mac-p1-feellab/FallRush.app/Contents/MacOS/Fall Rush"
LOG="$ROOT/.m1-logs/p3-mac-emu-$STAMP.log"
"$ROOT/$APP" -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logFile "$LOG" -fallrushOnchain emu -fallrushOnchainAuto "$SECS" -fallrushOnchainShot "$ROOT/docs/proof/p3/mac-emu-$STAMP.png" -fallrushHud ${P3_ARGS:-} &
PID=$!
( sleep $((SECS + 60)); kill $PID 2>/dev/null ) &
wait $PID || true
grep -E "^\[onchain\]" "$LOG" | tail -40
echo "--- emulator"; tail -5 ".m1-logs/p3-emu-$STAMP.out"
echo "log $LOG"
