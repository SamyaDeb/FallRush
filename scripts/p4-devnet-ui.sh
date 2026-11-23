#!/usr/bin/env bash
# P4 task 6/8 (locked): UI screenshots in the Mac player build on devnet: the player spectates an on-chain Final
# (bet panel + drop bar + Chaos Drop banner + telegraphs + receipt card), auto-bets and buys 2 drops through the in-game
# economy; a bundle bettor bets too. Screens: macOS screencapture every 12 s → docs/proof/p4/ui/.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p4-ui -- bash scripts/p4-devnet-ui.sh
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
RUN="p4-ui-$(date +%Y%m%d-%H%M%S)"; OUT="docs/proof/p4/ui"; mkdir -p "$OUT" .m1-logs
TSX=node_modules/.bin/tsx
node tools/p3/chain-relay.mjs --net devnet --fund-from ~/.config/solana/id.json --fund-lamports 20000000 > ".m1-logs/$RUN-relay.log" 2>&1 &
RELAY=$!
node tools/p4/bundle-player.mjs --net devnet --name A --role bettor --run "$RUN" > ".m1-logs/$RUN-A.log" 2>&1 & PA=$!
sleep 8
APP="client-unity/Builds/Mac-p1-feellab/FallRush.app/Contents/MacOS/Fall Rush"
"$ROOT/$APP" -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logFile "$ROOT/.m1-logs/$RUN-client.log" \
  -fallrushOnchain devnet -fallrushOnchainSpectate -fallrushOnchainAuto 400 -fallrushOnchainAutoDrop -fallrushOnchainShots "$ROOT/$OUT/$RUN" -fallrushName P4UI &
CLIENT=$!
mkdir -p "$OUT/$RUN"; SHOTS=
sleep 30
env P2_NET=devnet ARENA=2 MATCHES=1 MODE=final INTERVAL_MS=11 ROUND_GAP_MS=8000 RUN="$RUN" $TSX tools/keeper/run.ts > ".m1-logs/$RUN-keeper.log" 2>&1
sleep 25
kill $CLIENT $RELAY $PA 2>/dev/null
grep -E "auto-bet|auto-drop|drop seen|econ" ".m1-logs/$RUN-client.log" | cut -c1-200 > "$OUT/$RUN-client.txt"
grep -c NullReference ".m1-logs/$RUN-client.log" >> "$OUT/$RUN-client.txt"
tail -1 ".m1-logs/$RUN-keeper.log" | cut -c1-300
