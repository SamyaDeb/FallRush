#!/usr/bin/env bash
# P3-M (locked): a full match with the on-chain Final on devnet-as.
#   Classic Mac dev server (this worktree, -fallrushOnchainFinal, rollup mode, 11 bots + the Mac player as a match
#   probe with views) plays rounds 1-3; P2's keeper (unmodified, ../FallRush-p2, MODE=hybrid) tails the server's jsonl,
#   records rounds 1-3 on the Match and calls start_final; the client pre-joined the arena lobby (fonc final_prejoin)
#   through the relay and plays the Final on the rollup; the server takes the champion from the arena World.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p3-hybrid -- bash scripts/p3-hybrid.sh [fail]
# "fail": the server points at an arena that never runs the Final -> it plays the Final itself (Classic).
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
P2="$ROOT/../FallRush-p2"
MODE="${1:-ok}"
cd "$ROOT"
STAMP=$(date +%Y%m%d-%H%M%S); RUN="p3-m-$STAMP"
OUT="docs/proof/p3/hybrid/$RUN"; mkdir -p "$OUT" .m1-logs
LOGDIR="$ROOT/.m1-logs/$RUN-matchlogs"; mkdir -p "$LOGDIR"
WORLD=HYJRSXpSGZ3HdjmZr4oMy3x6VgTWLcarkgHav7L4hSTQ
ARENA=HbinPthhN3g5BRQRL4aKQ75HyQXMofhsUbg2c28bJXHt
[[ "$MODE" == fail ]] && WORLD=11111111111111111111111111111111
echo "$(date -u +%T) balances: admin $(solana balance -u devnet -k ~/.config/solana/id.json) authority $(solana balance -u devnet -k ~/.config/fall-rush/devnet-game-authority.json)" | tee "$OUT/balances.txt"
bash scripts/m1-macserver.sh stop >/dev/null 2>&1; sleep 1
: > .m1-logs/macserver.log
bash scripts/m1-macserver.sh start -fallrushBots 12 -fallrushMatchSize 12 -fallrushMatchLogDir "$LOGDIR" \
  -fallrushOnchainFinal -fallrushOnchainFinalNet devnet -fallrushOnchainArena "$ARENA" -fallrushOnchainWorld "$WORLD"
RELAY=""; KEEPER=""
if [[ "$MODE" != fail ]]; then
  node tools/p3/chain-relay.mjs --net devnet --fund-from ~/.config/solana/id.json --fund-lamports 10000000 > ".m1-logs/$RUN-relay.log" 2>&1 &
  RELAY=$!
fi
cleanup() { [[ -n "$RELAY" ]] && kill $RELAY 2>/dev/null; [[ -n "$KEEPER" ]] && kill $KEEPER 2>/dev/null; bash scripts/m1-macserver.sh stop >/dev/null 2>&1; }
trap cleanup EXIT
APP="client-unity/Builds/Mac-p1-feellab/FallRush.app/Contents/MacOS/Fall Rush"
"$ROOT/$APP" -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logFile "$ROOT/.m1-logs/$RUN-client.log" \
  -fallrushProbe ws://127.0.0.1:7777 -fallrushProbeScript match -fallrushProbeView -fallrushProbeSecs 900 \
  -fallrushOnchainAuto 900 -fallrushOnchainShot "$ROOT/$OUT/final.png" -fallrushName P3Hybrid &
CLIENT=$!
if [[ "$MODE" != fail ]]; then
  for _ in $(seq 1 240); do J=$(ls "$LOGDIR"/match-*.jsonl 2>/dev/null | head -1); [[ -n "$J" ]] && break; sleep 1; done
  echo "jsonl: $J"
  ( cd "$P2" && P2_NET=devnet ARENA=2 MATCHES=1 MODE=hybrid INTERVAL_MS=11 RUN="$RUN" CLASSIC_JSONL="$J" node --experimental-strip-types tools/keeper/run.ts ) > ".m1-logs/$RUN-keeper.log" 2>&1 &
  KEEPER=$!
fi
# the match ends at the server's DONE / the next lobby
for _ in $(seq 1 1200); do grep -q "COMPLETE_MATCH\|CompleteMatch\|soak\|LobbyOpen.*serial" .m1-logs/macserver.log 2>/dev/null && grep -q "COMPLETE_MATCH" .m1-logs/macserver.log && break; kill -0 $CLIENT 2>/dev/null || break; sleep 1; done
sleep 15
[[ -n "$KEEPER" ]] && { for _ in $(seq 1 120); do kill -0 $KEEPER 2>/dev/null || break; sleep 1; done; }
kill $CLIENT 2>/dev/null
cp "$LOGDIR"/match-*.jsonl "$OUT/" 2>/dev/null
cp "$P2/docs/proof/p2/runs/$RUN-m1/summary.json" "$OUT/keeper-summary.json" 2>/dev/null
grep -E "\[gs\] (round |on-chain|fonc|mevt .*(COMPLETE|START_ROUND))" .m1-logs/macserver.log | cut -c1-300 > "$OUT/server.txt"
grep -E "^\[onchain\]|\[net\] fonc" ".m1-logs/$RUN-client.log" | cut -c1-300 > "$OUT/client.txt"
[[ -n "$RELAY" ]] && grep -vE "secret|Secret" ".m1-logs/$RUN-relay.log" | tail -40 > "$OUT/relay.txt"
tail -3 ".m1-logs/$RUN-keeper.log" 2>/dev/null | cut -c1-400
echo "--- server"; tail -12 "$OUT/server.txt"
echo "--- client"; grep -E "fonc|pre-join|switch|RESULT|joined" "$OUT/client.txt" | tail -12
echo "$(date -u +%T) balances: admin $(solana balance -u devnet -k ~/.config/solana/id.json) authority $(solana balance -u devnet -k ~/.config/fall-rush/devnet-game-authority.json)" | tee -a "$OUT/balances.txt"
