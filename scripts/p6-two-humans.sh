#!/usr/bin/env bash
# P6 carry-over 4 (locked): one public match on gs1 with TWO clean-profile WebGL players (+ the phone /live sealed
# bettor via p6-public-match.sh) and a keeper-1 kill at `receipt`.   bash heavy-lock.sh p6-two -- bash scripts/p6-two-humans.sh <n>
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"; source ~/.config/fall-rush/vps.env; D="${VPS_IP//./-}"; N="${1:-3}"
OUT="docs/proof/p6/public/match-$N"; mkdir -p "$OUT"
bash scripts/p6-kill-test.sh keeper 1 receipt > "$OUT/kill.out" 2>&1 & K=$!
node tools/p6/smoke.mjs --flow player --autobet 7 --base "https://play.$D.sslip.io" --server "wss://gs1.$D.sslip.io" --arena-only 2 --out "$OUT/player2" --port 9435 --max 1300 > "$OUT/player2.out" 2>&1 & P2=$!
sleep 3
LIVE_DELAY=120 bash scripts/p6-public-match.sh "$N" gs1 943
wait $P2; pkill -f "remote-debugging-port=9435" 2>/dev/null
for _ in $(seq 1 60); do kill -0 $K 2>/dev/null || break; sleep 5; done; kill $K 2>/dev/null
echo "player2 pass=$(python3 -c "import json;print(json.load(open('$OUT/player2/result.json')).get('pass'))" 2>/dev/null)"
