#!/usr/bin/env bash
# Beta audit (2026-11-24): the "20 people at once" load test on the PUBLIC stack (https://fallrush.fun).
#   - N headless players (the Mac dev build as a scripted client: -fallrushProbeScript match plays every round with the
#     bot brain through the real client path). Each one does what the game's Quick Match does: POST /api/mm, then
#     connects to the server the matchmaker returned. They arrive STAGGER seconds apart (a beta crowd, not a burst).
#   - B headless spectator bettors on /live (tools/p7/load-bettors.ts: the chain bundle; open + sealed bets + drops).
#   - VPS sampling every 15 s (docker stats of fallrush-*, load average, the neighbour finity) → vps.txt.
# Results: docs/proof/p7/beta-audit/load/<run>/ (mm.txt = where each player was routed; p<i>.log = the probe logs).
#   bash scripts/beta-load.sh <run> [N=20] [DUR=900] [B=10] [STAGGER=3]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"; source ~/.config/fall-rush/vps.env
RUN="${1:-run1}"; N="${2:-20}"; DUR="${3:-900}"; B="${4:-10}"; STAGGER="${5:-3}"
API="${API:-https://fallrush.fun}"
OUT="docs/proof/p7/beta-audit/load/$RUN"; mkdir -p "$OUT"
APP="$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app"; BIN="$(ls "$APP/Contents/MacOS/"* | head -1)"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=8 -i "$SSH_KEY" "root@$VPS_IP")
# one SSH tunnel to the game servers' loopback ports (they are not public; the public path is wss via nginx)
"${SSH[@]}" -N -L 17777:127.0.0.1:7777 -L 17778:127.0.0.1:7778 -L 17779:127.0.0.1:7779 & TUN=$!; sleep 3
T0=$(date +%s); echo "start $(date -u +%FT%TZ) n=$N dur=$DUR bettors=$B stagger=$STAGGER" > "$OUT/run.txt"
( while [ "$(date +%s)" -lt $((T0 + DUR + 120)) ]; do
    echo "$(date -u +%T) $("${SSH[@]}" 'docker stats --no-stream --format "{{.Name}}={{.CPUPerc}}/{{.MemUsage}}" | grep fallrush- | tr "\n" " "; cat /proc/loadavg | cut -d" " -f1' 2>/dev/null) finity=$(curl -s -o /dev/null -w '%{http_code}/%{time_total}' --max-time 8 "${FINITY_URL:-https://example.invalid}/")"
    curl -s --max-time 5 "$API/api/status" | python3 -c "import json,sys; print('   status', ' | '.join(f\"{x['id']} {x['phase']} r{x.get('round')} humans={x.get('humans')} conns={x.get('conns')}\" for x in json.load(sys.stdin)['servers']))" 2>/dev/null
    sleep 15; done ) > "$OUT/vps.txt" 2>&1 &
VP=$!
if [ "$B" -gt 0 ]; then (cd tools/keeper && P2_NET=devnet N="$B" DURATION="$DUR" ARENAS=2,3 npx tsx ../p7/load-bettors.ts "$ROOT/$OUT/bettors" > "$ROOT/$OUT/bettors.out" 2>&1) & BP=$!; fi
PIDS=()
for i in $(seq 1 "$N"); do
  tier=$(python3 -c "print(['Rookie','Contender','Champion'][$i % 3])")
  R=$(curl -s --max-time 10 -X POST "$API/api/mm" -H 'content-type: application/json' -d "{\"tier\":\"$tier\"}")
  # the desktop client has no wss: the matchmaker's choice (gs id) is reached through the SSH tunnel to the same server
  GS=$(echo "$R" | python3 -c "import json,sys; r=json.load(sys.stdin); print(r.get('gs') or '')" 2>/dev/null)
  case "$GS" in gs1) URL=ws://127.0.0.1:17777 ;; gs2) URL=ws://127.0.0.1:17778 ;; gs3) URL=ws://127.0.0.1:17779 ;; *) URL= ;; esac
  echo "$(date -u +%T) p$i $tier → $(echo "$R" | python3 -c "import json,sys; r=json.load(sys.stdin); print(r.get('gs'), '|', r.get('reason') or r.get('error'), '| humans', r.get('humans'))" 2>/dev/null)" | tee -a "$OUT/mm.txt"
  if [ -n "$URL" ]; then
    "$BIN" -batchmode -nographics -fallrushProbe "$URL" -fallrushProbeSecs "$DUR" -fallrushProbeScript match -fallrushName "Beta$i" \
      -fallrushConnectTimeoutMs 8000 -logFile - > "$OUT/p$i.log" 2>&1 &
    PIDS+=($!)
  fi
  sleep "$STAGGER"
  # this Mac has 8 GB: stop adding players if free memory drops under 12 %
  FREE=$(memory_pressure | awk -F': ' '/free percentage/ {gsub("%","",$2); print $2}')
  if [ "${FREE:-100}" -lt 12 ]; then echo "$(date -u +%T) STOP adding players: Mac free memory ${FREE}%" | tee -a "$OUT/run.txt"; break; fi
done
echo "launched ${#PIDS[@]} players" | tee -a "$OUT/run.txt"
sleep 20; ps -o rss= -p "${PIDS[@]}" 2>/dev/null | awk '{s+=$1} END {printf "players RSS total %.0f MB\n", s/1024}' | tee -a "$OUT/run.txt"
wait "${PIDS[@]}" 2>/dev/null
[ -n "${BP:-}" ] && wait "$BP"
sleep 60; kill $VP $TUN 2>/dev/null
curl -s "$API/api/matches?n=40&all=1" | python3 -c "
import json,sys; t0=$T0*1000
ms=[m for m in json.load(sys.stdin)['matches'] if m.get('startedAt',0)>=t0-60000]
json.dump(ms,open('$OUT/matches.json','w'),indent=1); print(len(ms),'matches archived during the run')" | tee -a "$OUT/run.txt"
echo "end $(date -u +%FT%TZ)" >> "$OUT/run.txt"
sed -i '' "s/$VPS_IP/<VPS_IP>/g; s/${VPS_IP//./-}/<VPS_IP-dashed>/g" "$OUT"/*.txt "$OUT"/*.log "$OUT"/*.out 2>/dev/null || true
