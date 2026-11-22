#!/usr/bin/env bash
# P3-K (locked): N on-chain Lid Flip Finals on devnet-as: you (the Mac player, auto-play) + 2 bots.
#   relay (tools/p3/chain-relay.mjs --net devnet) <- Mac player (-fallrushOnchain devnet)
#   P2's keeper, unmodified, from ../FallRush-p2: MODE=final (synthetic rounds 1-3, start_final, ticks, commits,
#   finalize); the client joins the lobby while the keeper records rounds 1-3.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p3-devnet -- bash scripts/p3-devnet-final.sh [N] [webgl]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
P2="$ROOT/../FallRush-p2"
N="${1:-1}"
cd "$ROOT"
OUT="docs/proof/p3/devnet"; mkdir -p "$OUT" .m1-logs
APP="client-unity/Builds/Mac-p1-feellab/FallRush.app/Contents/MacOS/Fall Rush"
bal() { echo "$(date -u +%T) balances: admin $(solana balance -u devnet -k ~/.config/solana/id.json) authority $(solana balance -u devnet -k ~/.config/fall-rush/devnet-game-authority.json)"; }
bal | tee -a "$OUT/balances.txt"
pkill -f "^node tools/p3/chain-relay" 2>/dev/null; sleep 1
for i in $(seq 1 "$N"); do
  STAMP=$(date +%Y%m%d-%H%M%S); RUN="p3-k-$STAMP"
  echo "== final $i/$N run $RUN"
  node tools/p3/chain-relay.mjs --net devnet --fund-from ~/.config/solana/id.json --fund-lamports 10000000 > ".m1-logs/$RUN-relay.log" 2>&1 &
  RELAY=$!
  sleep 3
  # P3_HUMAN=1 (task L): you play with keyboard + mouse (no auto-play); otherwise the scripted auto-play measures
  if [[ "${P3_HUMAN:-0}" == 1 ]]; then PLAY=(-fallrushName You); else PLAY=(-fallrushOnchainAuto 400 -fallrushOnchainShot "$ROOT/$OUT/$RUN.png" -fallrushHud -fallrushName P3); fi
  "$ROOT/$APP" -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logFile "$ROOT/.m1-logs/$RUN-client.log" \
    -fallrushOnchain devnet "${PLAY[@]}" &
  CLIENT=$!
  for _ in $(seq 1 60); do grep -q "waitingLobby\|waitingMatch\|lobby" ".m1-logs/$RUN-relay.log" 2>/dev/null && break; sleep 1; done
  ( cd "$P2" && P2_NET=devnet ARENA=2 MATCHES=1 MODE=final INTERVAL_MS=11 RUN="$RUN" node --experimental-strip-types tools/keeper/run.ts ) > ".m1-logs/$RUN-keeper.log" 2>&1
  echo "keeper exit $?"
  sleep $([[ "${P3_HUMAN:-0}" == 1 ]] && echo 20 || echo 8)
  kill $CLIENT 2>/dev/null; sleep 1; kill $RELAY 2>/dev/null; wait 2>/dev/null
  mkdir -p "$OUT/$RUN"
  cp "$P2/docs/proof/p2/runs/$RUN-m1/summary.json" "$OUT/$RUN/keeper-summary.json" 2>/dev/null
  grep -E "^\[onchain\]" ".m1-logs/$RUN-client.log" > "$OUT/$RUN/client.txt"
  grep -vE "secret|Secret" ".m1-logs/$RUN-relay.log" | tail -60 > "$OUT/$RUN/relay.txt"
  tail -3 ".m1-logs/$RUN-keeper.log" | cut -c1-600
  tail -4 "$OUT/$RUN/client.txt" | cut -c1-300
done
bal | tee -a "$OUT/balances.txt"
