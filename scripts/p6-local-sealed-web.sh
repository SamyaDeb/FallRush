#!/usr/bin/env bash
# P6 (locked): a browser spectator on /live (phone viewport, clean profile) places a sealed Champion bet during a
# keeper-driven devnet match on arena 3 (replayed Classic jsonl; the Final on-chain) through the local web app +
# indexer, exactly as on the VPS. + N scripted sealed bettors (tools/p6/bettors.ts).
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p6-web -- bash scripts/p6-local-sealed-web.sh <src.jsonl> [N]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
SRC="$1"; NB="${2:-2}"
RUN="p6-web-$(date +%Y%m%d-%H%M%S)"; OUT="docs/proof/p6/e2e/$RUN"; mkdir -p "$OUT"
LOGDIR="$ROOT/.m1-logs/$RUN-matchlogs"; ARCH="$ROOT/.m1-logs/p6-e2e-archive"; mkdir -p "$LOGDIR" "$ARCH"
P=(); cleanup() { for p in "${P[@]}"; do kill $p 2>/dev/null; done; }; trap cleanup EXIT
(cd tools/keeper && P2_NET=devnet ARENA=3 LOG_DIR="$LOGDIR" ARCHIVE="$ARCH" STATUS_PORT=3911 ACTION=1 TEST_SEASON="${TEST_SEASON:-0}" WEEKLY="${WEEKLY:-0}" node --experimental-strip-types --no-warnings daemon.ts > "$ROOT/$OUT/keeper.log" 2>&1) & P+=($!)
ARCHIVE="$ARCH" PORT=3912 KEEPERS="gs2=http://127.0.0.1:3911" FUNDER_KEYPAIR="$ROOT/tools/keeper/.keys/devnet-funder.json" node --experimental-strip-types --no-warnings tools/indexer/server.ts > "$OUT/indexer.log" 2>&1 & P+=($!)
node tools/p5/static.mjs 4173 http://127.0.0.1:3912 > "$OUT/static.log" 2>&1 & P+=($!)
for _ in $(seq 1 90); do grep -q " idle " "$OUT/keeper.log" && break; sleep 2; done
echo "keeper idle"
node tools/p6/smoke.mjs --flow sealed --base http://localhost:4173 --arena 3 --out "$OUT/live" --port 9361 --max 600 > "$OUT/smoke.log" 2>&1 & S=$!
[ "$NB" -gt 0 ] && (cd tools/keeper && P2_NET=devnet ARENA=3 N=$NB npx tsx ../p6/bettors.ts "$ROOT/$OUT/bettors.json" > "$ROOT/$OUT/bettors.log" 2>&1) & B=$!
sleep 45
MID=$(python3 -c "import os;print(os.urandom(16).hex())")
python3 - "$SRC" "$LOGDIR/match-$MID.jsonl" "$MID" <<'PY'
import json,sys
src,dst,mid=sys.argv[1:4]
out=[]
for l in open(src):
    o=json.loads(l)
    if o.get("type")=="match": o["matchId"]=mid
    out.append(json.dumps(o))
open(dst,"w").write("\n".join(out)+"\n")
PY
echo "jsonl match $MID"
wait $S; echo "smoke exit $?"
wait $B
for _ in $(seq 1 120); do grep -q "match_done" <(tail -n 3 "$ARCH/keeper-devnet-arena3.jsonl") && break; sleep 3; done
cat "$OUT/smoke.log" | tail -12
tail -4 "$OUT/keeper.log"
