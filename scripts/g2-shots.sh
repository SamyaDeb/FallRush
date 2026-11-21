#!/usr/bin/env bash
# G2/G3 proof screenshots: the Mac dev server (1 match) + one WINDOWED probe client (BotBrain on its predicted state,
# the normal client view) that saves a PNG of every match screen and map (SimProbe -fallrushShotDir).
# usage: scripts/g2-shots.sh <outdir> [secs=520] [width=1280] [height=720] [extra probe args...]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:?outdir}"; SECS="${2:-520}"; W="${3:-1280}"; H="${4:-720}"
shift $(( $# < 4 ? $# : 4 ))
BIN="$(ls "$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app/Contents/MacOS/"* | head -1)"
mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"
LOGS="$ROOT/.m1-logs/g2"; mkdir -p "$LOGS"
"$BIN" -batchmode -nographics -fallrushServer -fallrushListen 127.0.0.1 -fallrushMatches 1 -logFile "$LOGS/shots-server.log" > /dev/null 2>&1 &
SPID=$!
for _ in $(seq 1 30); do grep -q "\[gs\] listening" "$LOGS/shots-server.log" 2>/dev/null && break; sleep 1; done
"$BIN" -fallrushProbe ws://127.0.0.1:7777 -fallrushProbeSecs "$SECS" -fallrushProbeScript match -fallrushProbeView -fallrushName You \
  -screen-fullscreen 0 -screen-width "$W" -screen-height "$H" -fallrushShotDir "$OUT" "$@" -logFile "$LOGS/shots-probe.log" > /dev/null 2>&1 &
PPID2=$!
wait $PPID2 || true
kill $SPID 2>/dev/null || true
echo "[g2-shots] $(ls "$OUT" | wc -l | tr -d ' ') files in $OUT"
