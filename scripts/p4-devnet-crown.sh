#!/usr/bin/env bash
# P4 task 7 (locked): an on-chain Final on devnet whose 3 finalists are human wallets (3 bundle "stand" players that
# join the arena lobby and never move) → the champion is a human: Crown Capsule + 3 VRF capsules minted. Plus a bundle
# bettor and a spectator buying Chaos Drops; the follow-up call's first attempt is forced to fail (FAIL_FIRST=1) →
# the keeper retries → done once. Payouts verified against the hand calculation.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p4-crown -- bash scripts/p4-devnet-crown.sh
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
RUN="p4-crown-$(date +%Y%m%d-%H%M%S)"; OUT="docs/proof/p4/runs/$RUN"; mkdir -p "$OUT" .m1-logs
bal() { echo "$(date -u +%T) admin $(solana balance -u devnet -k ~/.config/solana/id.json) authority $(solana balance -u devnet -k ~/.config/fall-rush/devnet-game-authority.json)"; }
bal | tee "$OUT/balances.txt"
TSX=node_modules/.bin/tsx
P=()
for n in S1 S2 S3; do node tools/p4/bundle-player.mjs --net devnet --name $n --role stand --run "$RUN" > ".m1-logs/$RUN-$n.log" 2>&1 & P+=($!); sleep 9; done
sleep 9; node tools/p4/bundle-player.mjs --net devnet --name A --role bettor --run "$RUN" > ".m1-logs/$RUN-A.log" 2>&1 & P+=($!); sleep 9
node tools/p4/bundle-player.mjs --net devnet --name C --role spectator --drops "${DROPS:-1,2,3}" --run "$RUN" > ".m1-logs/$RUN-C.log" 2>&1 & P+=($!)
sleep 60   # players: fund, session, chips (staggered: devnet RPC rate limits)
W=$(cat .m1-logs/$RUN-*.log | grep -o "burner [1-9A-HJ-NP-Za-km-z]*" | awk '{print $2}' | sort -u | tr '\n' ' ')
MM=$(solana-keygen pubkey tools/keeper/.keys/devnet-mm.json 2>/dev/null)
P2_NET=devnet ARENA=2 $TSX tools/p4/econ-check.ts snapshot "$RUN" $W $MM
env P2_NET=devnet ARENA=2 MATCHES=1 MODE=final INTERVAL_MS=11 HUMANS=3 WAIT_HUMANS_S=60 ROUND_GAP_MS=5000 FAIL_FIRST=1 RUN="$RUN" $TSX tools/keeper/run.ts > ".m1-logs/$RUN-keeper.log" 2>&1
echo "keeper exit $?"
for p in "${P[@]}"; do for _ in $(seq 1 90); do kill -0 $p 2>/dev/null || break; sleep 1; done; kill $p 2>/dev/null; done
MATCH=$(python3 -c "import json;print(json.load(open('docs/proof/p2/runs/$RUN-m1/summary.json'))['matchPda'])" 2>/dev/null)
P2_NET=devnet ARENA=2 $TSX tools/p4/econ-check.ts verify "$RUN" "$MATCH"
cp docs/proof/p2/runs/$RUN-m1/{summary,econ}.json "$OUT/" 2>/dev/null; cp docs/proof/p2/keeper-$RUN.jsonl "$OUT/keeper.jsonl" 2>/dev/null
tail -2 ".m1-logs/$RUN-keeper.log" | cut -c1-600
bal | tee -a "$OUT/balances.txt"
