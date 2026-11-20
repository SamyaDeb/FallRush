#!/usr/bin/env bash
# M4 §5.4 reconnect run: a full practice match with 3 probe clients (reference-tier bots on the predicted state, so
# they tend to survive round 1). When round 2 is running, kill one alive probe and restart it after ~10 s with its
# token file (→ same slot, still alive), and kill another for 30 s (→ ELIMINATED DISCONNECT at the end of round 2).
# usage: scripts/m4-reconnect-run.sh [label=reconnect]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LABEL="${1:-reconnect}"
BIN="$(ls "$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app/Contents/MacOS/"* | head -1)"
OUT="$ROOT/.m1-logs/m4/run-$LABEL"; rm -rf "$OUT"; mkdir -p "$OUT/logs"
URL=ws://127.0.0.1:7777
"$BIN" -batchmode -nographics -fallrushServer -fallrushListen 127.0.0.1 -fallrushMatches 1 -fallrushMatchLogDir "$OUT/logs" -logFile "$OUT/server.log" > /dev/null 2>&1 &
SPID=$!
for _ in $(seq 1 30); do grep -q "\[gs\] listening" "$OUT/server.log" 2>/dev/null && break; sleep 1; done
probe() { # n suffix
  "$BIN" -batchmode -nographics -fallrushProbe "$URL" -fallrushProbeSecs 600 -fallrushProbeScript match -fallrushProbeTier reference -fallrushName "Probe$1" \
    -fallrushTokenFile "$OUT/token-$1.txt" -logFile "$OUT/probe-$1$2.log" > /dev/null 2>&1 &
  echo $! > "$OUT/probe-$1.pid"
}
for n in 1 2 3; do probe $n ""; sleep 1; done
until grep -q "round 2/4" "$OUT/server.log" 2>/dev/null || ! kill -0 $SPID 2>/dev/null; do sleep 1; done
until grep -q "ROUND_ACTIVE\|round 2/4 .*start" "$OUT/server.log"; do sleep 1; done
sleep 15 # mid-round 2
# Which probes are alive in round 2? (slot of each probe from the server log; alive = QUALIFIED in round 1)
ALIVE=$(python3 - "$OUT/server.log" <<'PY'
import re, sys
s = open(sys.argv[1]).read()
slot = {m.group(2): int(m.group(1)) for m in re.finditer(r'\[gs\] slot (\d+) → client=\d+ name="Probe(\d)"', s)}
q = set(int(x) for x in re.findall(r"mevt #\d+ QUALIFIED r1 slot=(\d+)", s))
print(" ".join(n for n, sl in sorted(slot.items()) if sl in q))
PY
)
echo "[m4-reconnect] probes alive in round 2: ${ALIVE:-none}"
set -- $ALIVE
A="${1:-1}"; B="${2:-2}"
T0=$(date +%s)
kill -9 "$(cat "$OUT/probe-$A.pid")" "$(cat "$OUT/probe-$B.pid")" 2>/dev/null || true
echo "[m4-reconnect] t=0 killed Probe$A (back in 10 s) and Probe$B (back in 30 s)"
sleep 10; probe "$A" "-b"; echo "[m4-reconnect] t=$(( $(date +%s) - T0 )) restarted Probe$A with its token"
sleep 20; probe "$B" "-b"; echo "[m4-reconnect] t=$(( $(date +%s) - T0 )) restarted Probe$B with its token"
wait $SPID || true
for n in 1 2 3; do kill "$(cat "$OUT/probe-$n.pid")" 2>/dev/null || true; done
grep -E "\[gs\] (resume|slot .* detached|client=.* watches|round [0-9]/|match [0-9]+ (COMPLETE|ABORT))|ELIMINATED r2" "$OUT/server.log" | cut -c1-200
python3 "$ROOT/scripts/m4-verify-log.py" "$OUT/logs" | tail -2
