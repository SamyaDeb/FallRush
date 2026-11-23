#!/usr/bin/env bash
# P5 task 1.2 (locked): a full hybrid match locally with the FRESH Mac dev server (P4 §8.2 stale-build bug gone?):
# Mac dev server (12 bots, on-chain Final on devnet arena 3) + the keeper daemon (ACTION=1) + the indexer + the web app,
# and the WebGL build in Chrome as the player (/play?bot=match), through the web app exactly as on the VPS.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p5-local -- bash scripts/p5-local-hybrid.sh
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
RUN="p5-local-$(date +%Y%m%d-%H%M%S)"; OUT="docs/proof/p5/local/$RUN"; mkdir -p "$OUT" .m1-logs
LOGDIR="$ROOT/.m1-logs/$RUN-matchlogs"; ARCH="$ROOT/.m1-logs/p5-local-archive"; mkdir -p "$LOGDIR" "$ARCH"
ARENA_KEY=E9XE7vtQgrWnXLcGMn5FYXAFLb6GmBjrKcZq5wgPcEbG; WORLD=9Q5KBadvfx3Yoen1uqLUgbU1x6tXn4GmkEkjGqfPuRWn
P=()
cleanup() { for p in "${P[@]}"; do kill $p 2>/dev/null; done; bash scripts/m1-macserver.sh stop >/dev/null 2>&1; }
trap cleanup EXIT
(cd web/app && node scripts/assets.mjs >/dev/null && ./node_modules/.bin/vite build --logLevel warn) && rm -rf web/app/dist/unity && mkdir -p web/app/dist/unity && cp -R client-unity/Builds/WebGL/Build client-unity/Builds/WebGL/StreamingAssets web/app/dist/unity/ && python3 tools/p5/build-json.py web/app/dist
P2_NET=devnet ARENA=3 LOG_DIR="$LOGDIR" ARCHIVE="$ARCH" STATUS_PORT=3911 ACTION=1 node --experimental-strip-types --no-warnings tools/keeper/daemon.ts > "$OUT/keeper.log" 2>&1 & P+=($!)
ARCHIVE="$ARCH" PORT=3912 KEEPERS="gs1=http://127.0.0.1:3911" GS_PUBLIC="gs1=ws://127.0.0.1:7777" FUNDER_KEYPAIR="$ROOT/tools/keeper/.keys/devnet-funder.json" node --experimental-strip-types --no-warnings tools/indexer/server.ts > "$OUT/indexer.log" 2>&1 & P+=($!)
node tools/p5/static.mjs 4173 http://127.0.0.1:3912 > "$OUT/static.log" 2>&1 & P+=($!)
: > .m1-logs/macserver.log
bash scripts/m1-macserver.sh start -fallrushBots 12 -fallrushMatchSize 12 -fallrushMatchLogDir "$LOGDIR" \
  -fallrushOnchainFinal -fallrushOnchainFinalNet devnet -fallrushOnchainArena "$ARENA_KEY" -fallrushOnchainWorld "$WORLD"
for _ in $(seq 1 60); do curl -s localhost:3911/health | grep -q '"ok":true' && grep -q idle "$OUT/keeper.log" && break; sleep 2; done
node tools/p5/smoke.mjs --flow player --base http://localhost:4173 --out "$OUT/player" --port 9351 --max 1300
M=$(python3 -c "import json;print(json.load(open('$OUT/player/result.json'))['match']['match'])" 2>/dev/null)
echo "match $M"
[ -n "$M" ] && cp -R "$ARCH/$M" "$OUT/archive" 2>/dev/null
grep -E "\[gs\] (on-chain|fonc|round |capsule)" .m1-logs/macserver.log | cut -c1-250 > "$OUT/server.txt"
grep -E "^\S+\s+\S+\s+\[onchain\]" "$OUT/player/console.log" | cut -c1-250 | head -40 > "$OUT/client-onchain.txt"
tail -5 "$OUT/keeper.log"
