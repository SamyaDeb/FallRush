#!/usr/bin/env bash
# P4 task 7 (locked): a full hybrid match on devnet with the economy. The Classic Mac dev server (12 slots: 11 bots +
# the Mac player as a match probe, -fallrushOnchainFinal) plays rounds 1-3; the keeper (this worktree, MODE=hybrid,
# economy on) records them, opens the markets (+ the labelled market maker), starts the Final on the rollup, settles,
# writes the receipt, draws the capsule VRF, finalizes and mints the rewards. Bets: the Mac player (auto-bet through
# the in-game economy, via the relay) + 2 bundle burners (A, B: session keys); Chaos Drops: a bundle spectator (C).
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p4-hybrid -- bash scripts/p4-devnet-hybrid.sh
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
RUN="p4-hyb-$(date +%Y%m%d-%H%M%S)"; OUT="docs/proof/p4/runs/$RUN"; mkdir -p "$OUT" .m1-logs
LOGDIR="$ROOT/.m1-logs/$RUN-matchlogs"; mkdir -p "$LOGDIR"
WORLD=HYJRSXpSGZ3HdjmZr4oMy3x6VgTWLcarkgHav7L4hSTQ
ARENA=HbinPthhN3g5BRQRL4aKQ75HyQXMofhsUbg2c28bJXHt
TSX=node_modules/.bin/tsx
bal() { echo "$(date -u +%T) admin $(solana balance -u devnet -k ~/.config/solana/id.json) authority $(solana balance -u devnet -k ~/.config/fall-rush/devnet-game-authority.json)"; }
bal | tee "$OUT/balances.txt"
bash scripts/m1-macserver.sh stop >/dev/null 2>&1; sleep 1
: > .m1-logs/macserver.log
P=()
for n in A B; do node tools/p4/bundle-player.mjs --net devnet --name $n --role bettor --run "$RUN" > ".m1-logs/$RUN-$n.log" 2>&1 & P+=($!); done
node tools/p4/bundle-player.mjs --net devnet --name C --role spectator --drops "${DROPS:-1,3,4}" --run "$RUN" > ".m1-logs/$RUN-C.log" 2>&1 & P+=($!)
node tools/p3/chain-relay.mjs --net devnet --fund-from ~/.config/solana/id.json --fund-lamports 20000000 > ".m1-logs/$RUN-relay.log" 2>&1 &
RELAY=$!
cleanup() { kill $RELAY 2>/dev/null; [[ -n "${KEEPER:-}" ]] && kill $KEEPER 2>/dev/null; for p in "${P[@]}"; do kill $p 2>/dev/null; done; bash scripts/m1-macserver.sh stop >/dev/null 2>&1; }
trap cleanup EXIT
sleep 35   # players + relay: fund, session, chips
W="$(cat .m1-logs/$RUN-[ABC].log | grep -o "burner [1-9A-HJ-NP-Za-km-z]*" | awk '{print $2}' | sort -u | tr '\n' ' ') $(grep -o "burner [1-9A-HJ-NP-Za-km-z]*" .m1-logs/$RUN-relay.log | head -1 | awk '{print $2}') $(solana-keygen pubkey tools/keeper/.keys/devnet-mm.json 2>/dev/null)"
P2_NET=devnet ARENA=2 $TSX tools/p4/econ-check.ts snapshot "$RUN" $W
bash scripts/m1-macserver.sh start -fallrushBots 12 -fallrushMatchSize 12 -fallrushMatchLogDir "$LOGDIR" \
  -fallrushOnchainFinal -fallrushOnchainFinalNet devnet -fallrushOnchainArena "$ARENA" -fallrushOnchainWorld "$WORLD"
APP="client-unity/Builds/Mac-p1-feellab/FallRush.app/Contents/MacOS/Fall Rush"
"$ROOT/$APP" -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logFile "$ROOT/.m1-logs/$RUN-client.log" \
  -fallrushProbe ws://127.0.0.1:7777 -fallrushProbeScript match -fallrushProbeView -fallrushProbeSecs 1100 \
  -fallrushOnchainAuto 1100 -fallrushOnchainShot "$ROOT/$OUT/final.png" -fallrushName P4Hybrid -fallrushHud &
CLIENT=$!
for _ in $(seq 1 240); do J=$(ls "$LOGDIR"/match-*.jsonl 2>/dev/null | head -1); [[ -n "$J" ]] && break; sleep 1; done
echo "jsonl: $J"
env P2_NET=devnet ARENA=2 MATCHES=1 MODE=hybrid INTERVAL_MS=11 RUN="$RUN" CLASSIC_JSONL="$J" $TSX tools/keeper/run.ts > ".m1-logs/$RUN-keeper.log" 2>&1 &
KEEPER=$!
for _ in $(seq 1 1500); do kill -0 $KEEPER 2>/dev/null || break; sleep 1; done
echo "keeper done"
for p in "${P[@]}"; do for _ in $(seq 1 60); do kill -0 $p 2>/dev/null || break; sleep 1; done; done
kill $CLIENT 2>/dev/null
MATCH=$(python3 -c "import json;print(json.load(open('docs/proof/p2/runs/$RUN-m1/summary.json'))['matchPda'])" 2>/dev/null)
P2_NET=devnet ARENA=2 $TSX tools/p4/econ-check.ts verify "$RUN" "$MATCH"
cp "$LOGDIR"/match-*.jsonl "$OUT/" 2>/dev/null
cp docs/proof/p2/runs/$RUN-m1/{summary,econ}.json "$OUT/" 2>/dev/null; cp docs/proof/p2/keeper-$RUN.jsonl "$OUT/keeper.jsonl" 2>/dev/null
grep -E "^\[onchain\]|\[net\] fonc" ".m1-logs/$RUN-client.log" | cut -c1-300 > "$OUT/client.txt"
grep -vE "secret|Secret" ".m1-logs/$RUN-relay.log" | tail -40 > "$OUT/relay.txt"
grep -E "\[gs\] (round |on-chain|fonc|mevt .*(COMPLETE|START_ROUND))" .m1-logs/macserver.log | cut -c1-300 > "$OUT/server.txt"
tail -2 ".m1-logs/$RUN-keeper.log" | cut -c1-600
grep -E "auto-bet" "$OUT/client.txt" | tail -6
bal | tee -a "$OUT/balances.txt"
