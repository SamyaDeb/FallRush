#!/usr/bin/env bash
# P6 (locked): the new keeper daemon (sealed bets + seasons) on devnet arena 3 with a replayed Classic jsonl (fresh match
# id; rounds 1–3 bridged, the Final on-chain with bots) + scripted sealed bettors (tools/p6/bettors.ts). No Unity.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p6-e2e -- bash scripts/p6-local-e2e.sh <src.jsonl> [N]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
SRC="$1"; NB="${2:-5}"
RUN="p6-e2e-$(date +%Y%m%d-%H%M%S)"; OUT="docs/proof/p6/e2e/$RUN"; mkdir -p "$OUT"
LOGDIR="$ROOT/.m1-logs/$RUN-matchlogs"; ARCH="$ROOT/.m1-logs/p6-e2e-archive"; mkdir -p "$LOGDIR" "$ARCH"
P=(); cleanup() { for p in "${P[@]}"; do kill $p 2>/dev/null; done; }; trap cleanup EXIT
cd tools/keeper
P2_NET=devnet ARENA=3 LOG_DIR="$LOGDIR" ARCHIVE="$ARCH" STATUS_PORT=3911 ACTION=1 TEST_SEASON="${TEST_SEASON:-0}" WEEKLY="${WEEKLY:-0}" node --experimental-strip-types --no-warnings daemon.ts > "$ROOT/$OUT/keeper.log" 2>&1 & P+=($!)
for _ in $(seq 1 90); do grep -q " idle " "$ROOT/$OUT/keeper.log" && break; sleep 2; done
echo "keeper idle"; tail -3 "$ROOT/$OUT/keeper.log"
P2_NET=devnet ARENA=3 N=$NB npx tsx ../p6/bettors.ts "$ROOT/$OUT/bettors.json" > "$ROOT/$OUT/bettors.log" 2>&1 & B=$!
sleep 25
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
for _ in $(seq 1 400); do grep -q '"ev":"match_done"' "$ARCH/keeper-devnet-arena3.jsonl" 2>/dev/null && grep -q "match_done" <(tail -n 5 "$ARCH/keeper-devnet-arena3.jsonl") && break; sleep 3; done
wait $B
M=$(python3 -c "import json;print(json.load(open('$ROOT/$OUT/bettors.json'))['match'])")
cp -R "$ARCH/$M" "$OUT/archive" 2>/dev/null
tail -8 "$ROOT/$OUT/keeper.log"
