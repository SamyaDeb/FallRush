#!/usr/bin/env bash
# P6 (copy of p5-public-match.sh) task 6.2 (locked): one public hybrid match with sealed bets (in-game ?autobet= + phone /live) through the public URLs only: a headless player (/play?bot=match, the
# match probe plays rounds 1-3, the on-chain Final auto-plays), a phone-viewport /live spectator that bets and buys a
# Chaos Drop, then /replay and /receipt of the archived match; VPS load + finity response sampled every 15 s.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p5-pub -- bash scripts/p5-public-match.sh <n> [gs1|gs2]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"; source ~/.config/fall-rush/vps.env
N="${1:-1}"; G="${2:-gs1}"; PB="${3:-936}"; LIVE="${4:-1}"; D="${VPS_IP//./-}"; P="https://play.$D.sslip.io"; ARENA=$([ "$G" = gs2 ] && echo 3 || echo 2)
OUT="docs/proof/p6/public/match-$N"; mkdir -p "$OUT"
( while true; do echo "$(date -u +%T) $(ssh -o IdentitiesOnly=yes -o ConnectTimeout=8 -i "$SSH_KEY" "root@$VPS_IP" 'docker stats --no-stream --format "{{.Name}}={{.CPUPerc}}/{{.MemUsage}}" | tr "\n" " "; cat /proc/loadavg | cut -d" " -f1' 2>/dev/null) finity=$(curl -s -o /dev/null -w '%{http_code}/%{time_total}' --max-time 8 ${FINITY_URL}/)"; sleep 15; done ) > "$OUT/load.txt" 2>&1 &
LP=$!
trap 'kill $LP 2>/dev/null' EXIT
node tools/p6/smoke.mjs --flow player --autobet ${AUTOBET:-10} --base "$P" --server "wss://$G.$D.sslip.io" --arena-only "$ARENA" --out "$OUT/player" --port ${PB}1 --max 1300 > "$OUT/player.out" 2>&1 &
PP=$!
sleep ${LIVE_DELAY:-45}
[ "$LIVE" = 1 ] && node tools/p6/smoke.mjs --flow sealed --base "$P" --arena "$ARENA" --out "$OUT/live" --port ${PB}2 --max 600 > "$OUT/live.out" 2>&1 &
SP=$!
wait $PP; [ "$LIVE" = 1 ] && { sleep 30; kill $SP 2>/dev/null; wait $SP 2>/dev/null; }
M=$(python3 -c "import json;print(json.load(open('$OUT/player/result.json'))['match']['match'])" 2>/dev/null)
echo "match $M"
if [ -n "$M" ]; then
  sleep 20
  node tools/p6/smoke.mjs --flow replay --base "$P" --match "$M" --out "$OUT/replay" --port ${PB}3 --max 240 > "$OUT/replay.out" 2>&1
  node tools/p6/smoke.mjs --flow receipt --base "$P" --match "$M" --out "$OUT/receipt" --port ${PB}4 --max 120 > "$OUT/receipt.out" 2>&1
  curl -s "$P/api/match/$M" > "$OUT/match.json"
fi
for f in player live replay receipt; do [ -f "$OUT/$f/result.json" ] && echo "$f pass=$(python3 -c "import json;print(json.load(open('$OUT/$f/result.json')).get('pass'))")"; done
pkill -f "remote-debugging-port=${PB}[0-9]" 2>/dev/null || true  # P6: never leave a smoke Chrome playing
