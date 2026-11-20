#!/usr/bin/env bash
# LOCAL MEASUREMENT ONLY (M2a): Mac dev server with 12 occupants (11 TEST bots + 1 headless probe client).
# Measures server frame work / sim step p95 (from [gs] lines) and socket bytes of the server process (nettop).
# usage: scripts/m2-measure.sh <snapHz 15|30> <serverFps 30|60> <seconds> <label>
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HZ="${1:-15}"; FPS="${2:-30}"; SECS="${3:-60}"; LABEL="${4:-m2}"
LOG="$ROOT/.m1-logs/macserver.log"
bash "$ROOT/scripts/m1-macserver.sh" stop >/dev/null 2>&1 || true
: > "$LOG"
bash "$ROOT/scripts/m1-macserver.sh" start -fallrushBots 12 -fallrushSnapHz "$HZ" -fallrushServerFps "$FPS" >/dev/null
PID="$(cat "$ROOT/.m1-logs/macserver.pid")"
bash "$ROOT/scripts/m2-probe.sh" ws://127.0.0.1:7777 "$((SECS + 40))" run "$LABEL" > "$ROOT/.m1-logs/probe-$LABEL.summary" &
PROBE=$!
sleep 12   # connect + map load + settle
S0="$(nettop -P -x -L 1 -p "$PID" -J bytes_in,bytes_out 2>/dev/null | tail -1)"
T0=$(date +%s)
sleep "$SECS"
S1="$(nettop -P -x -L 1 -p "$PID" -J bytes_in,bytes_out 2>/dev/null | tail -1)"
T1=$(date +%s)
CPU="$(ps -o %cpu=,rss= -p "$PID")"
wait $PROBE || true
bash "$ROOT/scripts/m1-macserver.sh" stop >/dev/null
echo "== $LABEL snapHz=$HZ serverFps=$FPS window=$((T1-T0))s"
echo "nettop server (bytes_in,bytes_out cumulative): t0=[$S0] t1=[$S1]"
python3 - "$S0" "$S1" "$((T1-T0))" <<'PY'
import sys
def parse(s):
    f=[x for x in s.split(',') if x.strip()!='']
    nums=[int(x) for x in f if x.strip().isdigit()]
    return nums[-2], nums[-1]
i0,o0=parse(sys.argv[1]); i1,o1=parse(sys.argv[2]); dt=int(sys.argv[3])
print(f"server socket bytes/s: in={(i1-i0)/dt:.0f} out={(o1-o0)/dt:.0f} (1 client)")
PY
echo "server ps %cpu,rss(KB): $CPU"
grep "\[gs\] stats\|\[gs\] sim tick" "$LOG" | tail -6
grep "\[probe\]" "$ROOT/.m1-logs/probe-$LABEL.log" | tail -3
