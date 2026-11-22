#!/usr/bin/env bash
# P3-F (locked): a full Classic match with the switch at the Final, all local:
#   chain emulator (shaping on) + the Classic Mac dev server with -fallrushOnchainFinal + bots + the Mac dev player as a
#   match probe with views (plays rounds 1-3 on Classic, then the Final on the emulator), logs + screenshots.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p3-switch -- bash scripts/p3-switch.sh [seconds] [fail]
# "fail" = no emulator: the server's handoff fails and it runs the Final itself (Classic).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
SECS="${1:-480}"; MODE="${2:-ok}"
STAMP=$(date +%Y%m%d-%H%M%S)
mkdir -p .m1-logs docs/proof/p3
EMU=""
if [[ "$MODE" != fail ]]; then
  node tools/chain-emulator/src/main.mjs --log ".m1-logs/p3-switch-emu-$STAMP.jsonl" > ".m1-logs/p3-switch-emu-$STAMP.out" 2>&1 &
  EMU=$!
fi
cleanup() { [[ -n "$EMU" ]] && kill $EMU 2>/dev/null; bash scripts/m1-macserver.sh stop >/dev/null 2>&1 || true; }
trap cleanup EXIT
bash scripts/m1-macserver.sh stop >/dev/null 2>&1; sleep 1
: > .m1-logs/macserver.log
bash scripts/m1-macserver.sh start -fallrushBots 12 -fallrushMatchSize 12 -fallrushOnchainFinal
APP="client-unity/Builds/Mac-p1-feellab/FallRush.app/Contents/MacOS/Fall Rush"
LOG="$ROOT/.m1-logs/p3-switch-client-$STAMP.log"
"$ROOT/$APP" -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logFile "$LOG" \
  -fallrushProbe ws://127.0.0.1:7777 -fallrushProbeScript match -fallrushProbeView -fallrushProbeSecs "$SECS" \
  -fallrushOnchainAuto "$SECS" -fallrushOnchainShot "$ROOT/docs/proof/p3/switch-$MODE-$STAMP.png" -fallrushName P3Probe &
PID=$!
( sleep $((SECS + 90)); kill $PID 2>/dev/null ) &
# one match: stop 25 s after the server completes it (the champion / receipt screens)
( until grep -q "COMPLETE_MATCH" .m1-logs/macserver.log; do sleep 2; done; sleep 25; kill $PID 2>/dev/null ) &
wait $PID || true
echo "--- server"; grep -E "\[gs\] (round |on-chain|fonc|mevt .*(COMPLETE|CHAMPION|START_ROUND))|handoff" .m1-logs/macserver.log | cut -c1-260 | tail -25
echo "--- client"; grep -E "^\[onchain\]|\[net\] fonc|\[probe\]" "$LOG" | cut -c1-260 | tail -30
[[ -n "$EMU" ]] && { echo "--- emulator"; grep -E "round_end|arena_start|join" ".m1-logs/p3-switch-emu-$STAMP.out" | tail -8; }
echo "client log $LOG"
