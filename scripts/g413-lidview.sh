#!/usr/bin/env bash
# G4.13 (dev tool): Lid Flip frames from the Mac PLAYER (not the Editor), for the lid-flip render check.
# Usage: scripts/g413-lidview.sh <label> <look soft|candy> <gfx high|low> [first_s] [last_s] [step_s] [extra client args…]
# One-round Lid Flip match on the Mac dev server (bots), one windowed probe client (match bot) with -fallrushProbeView,
# a screenshot every <step> s → .m1-logs/g413/<label>/shot-<s>.png
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LABEL="${1:?label}"; LOOK="${2:-soft}"; GFX="${3:-high}"; FIRST="${4:-12}"; LAST="${5:-110}"; STEP="${6:-1}"
shift $(( $# < 6 ? $# : 6 ))
BIN="$(ls "$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app/Contents/MacOS/"* | head -1)"
OUT="$ROOT/.m1-logs/g413/$LABEL"; rm -rf "$OUT"; mkdir -p "$OUT"
bash "$ROOT/scripts/m1-macserver.sh" stop >/dev/null 2>&1 || true
"$BIN" -batchmode -nographics -fallrushServer -fallrushListen 127.0.0.1 -fallrushMap CrumbleShelf -logFile "$OUT/server.log" > /dev/null 2>&1 &
SPID=$!
for _ in $(seq 1 30); do grep -q "\[gs\] listening" "$OUT/server.log" 2>/dev/null && break; sleep 1; done
AT="$(seq -s, "$FIRST" "$STEP" "$LAST")"
"$BIN" -screen-fullscreen 0 -screen-width 1280 -screen-height 800 -fallrushProbe ws://127.0.0.1:7777 -fallrushProbeView \
  -fallrushProbeSecs $(( LAST + 8 )) -fallrushProbeScript match -fallrushName QA -fallrushLook "$LOOK" -fallrushGfx "$GFX" \
  -fallrushScreenshot "$OUT/shot.png" -fallrushScreenshotAt "$AT" "$@" -logFile "$OUT/client.log" > /dev/null 2>&1
kill $SPID 2>/dev/null; wait $SPID 2>/dev/null
echo "[g413-lidview] $LABEL: $(ls "$OUT"/shot-*.png 2>/dev/null | wc -l | tr -d ' ') shots → $OUT"
