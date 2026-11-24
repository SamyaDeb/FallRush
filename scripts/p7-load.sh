#!/usr/bin/env bash
# P7 A3.1 (locked): the load test on the PUBLIC stack, both arenas at once, back to back for DUR seconds:
#   - per arena one WebGL player (clean Chrome profile, /play?bot=match: the match probe plays rounds 1–3, the on-chain
#     Final auto-plays, ?autobet= places a sealed Champion bet), restarted after every archived match;
#   - N headless spectator bettors (tools/p7/load-bettors.ts: the chain bundle, own burners; open + sealed bets + drops);
#   - VPS sampling every 20 s (docker stats of fallrush-*, load, finity response) → vps.txt (address redacted).
# Results: docs/proof/p7/load/<run>/ (+ the archived matches' keeper summaries for CU / tick gaps / outcomes).
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p7-load -- bash scripts/p7-load.sh <run> [DUR=1800] [N=30]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"; source ~/.config/fall-rush/vps.env
RUN="${1:-run1}"; DUR="${2:-1800}"; N="${3:-30}"; D="${VPS_IP//./-}"; P="https://play.$D.sslip.io"
OUT="docs/proof/p7/load/$RUN"; mkdir -p "$OUT"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=8 -i "$SSH_KEY" "root@$VPS_IP")
T0=$(date +%s); END=$((T0 + DUR))
echo "start $(date -u +%FT%TZ) dur $DUR n $N" > "$OUT/run.txt"
( while [ "$(date +%s)" -lt $((END + 240)) ]; do echo "$(date -u +%T) $("${SSH[@]}" 'docker stats --no-stream --format "{{.Name}}={{.CPUPerc}}/{{.MemUsage}}" | grep fallrush- | tr "\n" " "; cat /proc/loadavg | cut -d" " -f1' 2>/dev/null) finity=$(curl -s -o /dev/null -w '%{http_code}/%{time_total}' --max-time 8 ${FINITY_URL}/)"; sleep 20; done ) > "$OUT/vps.txt" 2>&1 &
VP=$!
players() { # $1 = gs id, $2 = arena, $3 = port base
  local k=0
  while [ "$(date +%s)" -lt "$END" ]; do
    k=$((k + 1))
    node tools/p6/smoke.mjs --flow player --autobet 10 --base "$P" --server "wss://$1.$D.sslip.io" --arena-only "$2" --out "$OUT/player-$1-$k" --port "$3$k" --max 1100 > "$OUT/player-$1-$k.out" 2>&1
    pkill -f "remote-debugging-port=$3$k" 2>/dev/null
  done
}
players gs1 2 951 & A=$!
sleep 25
players gs2 3 952 & B=$!
(cd tools/keeper && P2_NET=devnet N="$N" DURATION="$DUR" ARENAS=2,3 npx tsx ../p7/load-bettors.ts "$ROOT/$OUT/bettors" > "$ROOT/$OUT/bettors.out" 2>&1) & C=$!
wait $A $B $C
sleep 200; kill $VP 2>/dev/null
# the matches archived during the run: keeper summaries (CU, tick gaps, outcome, sealed, drops)
curl -s "$P/api/matches?n=40&all=1" | python3 -c "
import json,sys; t0=$T0*1000
ms=[m for m in json.load(sys.stdin)['matches'] if m.get('startedAt',0)>=t0-60000]
json.dump(ms,open('$OUT/matches.json','w'),indent=1); print(len(ms),'matches')" | tee -a "$OUT/run.txt"
for m in $(python3 -c "import json;print(' '.join(x['match'] for x in json.load(open('$OUT/matches.json'))))"); do curl -s "$P/api/match/$m" > "$OUT/match-$m.json"; done
echo "end $(date -u +%FT%TZ)" >> "$OUT/run.txt"
sed -i '' "s/$VPS_IP/<VPS_IP>/g; s/$D/<VPS_IP-dashed>/g" "$OUT"/*.txt "$OUT"/*.out 2>/dev/null || true
