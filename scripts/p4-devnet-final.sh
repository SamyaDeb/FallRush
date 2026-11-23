#!/usr/bin/env bash
# P4-0 (locked): N on-chain Lid Flip Finals on devnet-as with the Mac player (auto-play) + 2 bots, for the input
# window A/B (corrections per 5 s). P3's p3-devnet-final.sh with this worktree's keeper (wait for humans: HUMANS=1).
#   WINDOW=8|12 (client cap + LEAD for the arena; unset LEAD = leave the arena as is)
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p4-devnet -- bash scripts/p4-devnet-final.sh [N] [label]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
N="${1:-1}"; LABEL="${2:-w${WINDOW:-8}}"
cd "$ROOT"
OUT="docs/proof/p4/devnet-window"; mkdir -p "$OUT" .m1-logs
APP="client-unity/Builds/Mac-p1-feellab/FallRush.app/Contents/MacOS/Fall Rush"
bal() { echo "$(date -u +%T) balances: admin $(solana balance -u devnet -k ~/.config/solana/id.json) authority $(solana balance -u devnet -k ~/.config/fall-rush/devnet-game-authority.json)"; }
bal | tee -a "$OUT/balances.txt"
pkill -f "^node tools/p3/chain-relay" 2>/dev/null; sleep 1
for i in $(seq 1 "$N"); do
  STAMP=$(date +%Y%m%d-%H%M%S); RUN="p4-$LABEL-$STAMP"
  echo "== final $i/$N run $RUN"
  node tools/p3/chain-relay.mjs --net devnet --fund-from ~/.config/solana/id.json --fund-lamports 10000000 > ".m1-logs/$RUN-relay.log" 2>&1 &
  RELAY=$!
  sleep 3
  # HUMAN=1 (scripts/p4-feel.sh): you play with keyboard + mouse; otherwise the scripted auto-play measures
  if [[ "${HUMAN:-0}" == 1 ]]; then PLAY=(-fallrushName You); else PLAY=(-fallrushOnchainAuto 400 -fallrushOnchainShot "$ROOT/$OUT/$RUN.png" -fallrushHud -fallrushName P4); fi
  "$ROOT/$APP" -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logFile "$ROOT/.m1-logs/$RUN-client.log" \
    -fallrushOnchain devnet -fallrushOnchainWindow "${WINDOW:-8}" "${PLAY[@]}" &
  CLIENT=$!
  for _ in $(seq 1 60); do grep -q "waitingLobby\|waitingMatch\|lobby" ".m1-logs/$RUN-relay.log" 2>/dev/null && break; sleep 1; done
  # P4 feel runs (DROPS=1): a bundle spectator buys Chaos Drops during the Final
  SPEC=""; [[ "${DROPS:-0}" == 1 ]] && { node tools/p4/bundle-player.mjs --net devnet --name C --role spectator --drops "${DROP_KINDS:-1,3,2}" --run "$RUN" > ".m1-logs/$RUN-C.log" 2>&1 & SPEC=$!; }
  ( env P2_NET=devnet ARENA=2 MATCHES=1 MODE=final INTERVAL_MS=11 HUMANS=1 WAIT_HUMANS_S=20 ${LEAD:+LEAD=$LEAD} RUN="$RUN" node --experimental-strip-types tools/keeper/run.ts ) > ".m1-logs/$RUN-keeper.log" 2>&1
  echo "keeper exit $?"
  sleep 8
  kill $CLIENT 2>/dev/null; sleep 1; kill $RELAY 2>/dev/null; [[ -n "$SPEC" ]] && kill $SPEC 2>/dev/null; wait 2>/dev/null
  mkdir -p "$OUT/$RUN"
  cp "docs/proof/p2/runs/$RUN-m1/summary.json" "$OUT/$RUN/keeper-summary.json" 2>/dev/null; cp "docs/proof/p2/keeper-$RUN.jsonl" "$OUT/$RUN/keeper.jsonl" 2>/dev/null
  grep -E "^\[onchain\]" ".m1-logs/$RUN-client.log" > "$OUT/$RUN/client.txt"
  [[ -f "docs/proof/p2/runs/$RUN-m1/world.bin" ]] && python3 tools/p4/drop-deaths.py "docs/proof/p2/runs/$RUN-m1/world.bin" > "$OUT/$RUN/drops.json"
  cp "$ROOT/$OUT/$RUN"*-drop*.png "$OUT/$RUN/" 2>/dev/null
  grep -vE "secret|Secret" ".m1-logs/$RUN-relay.log" | tail -60 > "$OUT/$RUN/relay.txt"
  tail -3 ".m1-logs/$RUN-keeper.log" | cut -c1-600
  tail -4 "$OUT/$RUN/client.txt" | cut -c1-300
done
bal | tee -a "$OUT/balances.txt"
