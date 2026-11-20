#!/usr/bin/env bash
# M4 §5.4 real-client run: the Mac dev server (real time) + 3 headless probe clients playing a full practice match with
# BotBrain on their predicted state (-fallrushProbeScript match) + 9 server bots. Logs .m1-logs/m4/run-<label>/.
# usage: scripts/m4-match-run.sh <label> [url=ws://127.0.0.1:7777] [secs=540] [extra probe args...]
#   localhost: scripts/m4-match-run.sh local
#   150 ms:    scripts/m2-netem.sh start 75 15 2 && scripts/m4-match-run.sh netem ws://127.0.0.1:7778
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LABEL="${1:?label}"; URL="${2:-ws://127.0.0.1:7777}"; SECS="${3:-540}"
shift $(( $# < 3 ? $# : 3 ))
BIN="$(ls "$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app/Contents/MacOS/"* | head -1)"
OUT="$ROOT/.m1-logs/m4/run-$LABEL"; rm -rf "$OUT"; mkdir -p "$OUT/logs"
bash "$ROOT/scripts/m1-macserver.sh" stop >/dev/null 2>&1 || true
"$BIN" -batchmode -nographics -fallrushServer -fallrushListen 127.0.0.1 -fallrushMatches 1 -fallrushMatchLogDir "$OUT/logs" -logFile "$OUT/server.log" > /dev/null 2>&1 &
SPID=$!
for _ in $(seq 1 30); do grep -q "\[gs\] listening" "$OUT/server.log" 2>/dev/null && break; sleep 1; done
for n in 1 2 3; do
  "$BIN" -batchmode -nographics -fallrushProbe "$URL" -fallrushProbeSecs "$SECS" -fallrushProbeScript match -fallrushName "Probe$n" \
    -fallrushTokenFile "$OUT/token-$n.txt" "$@" -logFile "$OUT/probe-$n.log" > /dev/null 2>&1 &
  echo $! > "$OUT/probe-$n.pid"
  sleep 1
done
echo "[m4-run] server pid $SPID, probes started; waiting for the match to end (server quits after 1 match)…"
wait $SPID || true
for n in 1 2 3; do kill "$(cat "$OUT/probe-$n.pid")" 2>/dev/null || true; done
sleep 1
grep -E "\[gs\] (round|soak|match [0-9]+ (COMPLETE|ABORT))" "$OUT/server.log" | cut -c1-200
python3 "$ROOT/scripts/m4-verify-log.py" "$OUT/logs" | tail -2
python3 "$ROOT/scripts/m4-summarize.py" "$OUT"
