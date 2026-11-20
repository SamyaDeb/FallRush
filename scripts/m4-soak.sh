#!/usr/bin/env bash
# M4 unattended match soak (M4 §5.3): N complete 12-bot matches back to back on the Mac dev server, headless, random
# seeds, sim speed S (ticks per network tick while nobody is connected). Samples the server RSS every 5 s, then
# re-verifies every jsonl event log independently (scripts/m4-verify-log.py) and prints the per-match table.
# usage: scripts/m4-soak.sh [matches=20] [simSpeed=8]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BIN="$(ls "$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app/Contents/MacOS/"* | head -1)"
N="${1:-20}"; SPEED="${2:-8}"
OUT="$ROOT/.m1-logs/m4/soak"; rm -rf "$OUT"; mkdir -p "$OUT/logs"
start=$(date +%s)
"$BIN" -batchmode -nographics -fallrushServer -fallrushListen 127.0.0.1 -port 7795 -fallrushMatches "$N" -fallrushSimSpeed "$SPEED" \
  -fallrushMatchLogDir "$OUT/logs" -logFile "$OUT/server.log" > /dev/null 2>&1 &
PID=$!
echo "t_s,rss_kb,cpu_pct" > "$OUT/rss.csv"
while kill -0 $PID 2>/dev/null; do
  ps -o rss=,%cpu= -p $PID | awk -v t=$(( $(date +%s) - start )) '{print t","$1","$2}' >> "$OUT/rss.csv" || true
  sleep 5
done
wait $PID; CODE=$?
echo "[m4-soak] server exit=$CODE wall=$(( $(date +%s) - start )) s"
grep -E "\[gs\] soak|Exception" "$OUT/server.log" | head -60
python3 "$ROOT/scripts/m4-verify-log.py" "$OUT/logs" | tail -$(( N + 2 ))
echo "[m4-soak] RSS (first / max / last sample, KB): $(awk -F, 'NR==2{f=$2} NR>1{if($2>m)m=$2; l=$2} END{print f" / "m" / "l}' "$OUT/rss.csv")"
