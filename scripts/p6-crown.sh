#!/usr/bin/env bash
# P6 carry-over 3 (D-CROWN, locked): up to N keeper-driven devnet matches on arena 3 (replayed Classic jsonl, fresh
# match ids) where two scripted humans (tools/p6/crown-harness.ts: bundle join + session-key inputs from rush-core's
# bot brain) play the on-chain Final in finalist slots 4 and 7; stops at the first match a human wins, then checks the
# Crown Core asset was created by the Magic Action in the undelegation tx (fee payer = the validator) with 0 keeper
# mint_rewards calls.   bash heavy-lock.sh p6-crown -- bash scripts/p6-crown.sh <src.jsonl> [N=8]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
SRC="$1"; N="${2:-8}"
RUN="p6-crown-$(date +%Y%m%d-%H%M%S)"; OUT="docs/proof/p6/crown/$RUN"; mkdir -p "$OUT"
LOGDIR="$ROOT/.m1-logs/$RUN-matchlogs"; ARCH="$ROOT/.m1-logs/p6-e2e-archive"; mkdir -p "$LOGDIR" "$ARCH"
P=(); cleanup() { for p in "${P[@]}"; do kill $p 2>/dev/null; done; }; trap cleanup EXIT
(cd tools/keeper && P2_NET=devnet ARENA=3 LOG_DIR="$LOGDIR" ARCHIVE="$ARCH" STATUS_PORT=3911 ACTION=1 TEST_SEASON="${TEST_SEASON:-0}" WEEKLY="${WEEKLY:-0}" node --experimental-strip-types --no-warnings daemon.ts > "$ROOT/$OUT/keeper.log" 2>&1) & P+=($!)
for _ in $(seq 1 90); do grep -q " idle " "$OUT/keeper.log" && break; sleep 2; done
for i in $(seq 1 "$N"); do
  MID=$(python3 -c "import os;print(os.urandom(16).hex())")
  python3 - "$SRC" "$LOGDIR/match-$MID.jsonl" "$MID" <<'PY'
import json,sys
src,dst,mid=sys.argv[1:4]
out=[]
for l in open(src):
    o=json.loads(l)
    if o.get("type")=="match":
        o["matchId"]=mid
        for r in o.get("roster",[]):
            if r["slot"] in (4,7): r["bot"]=False; r["userId"]="guest:p6-harness:%d"%r["slot"]
            elif r["slot"]==0: r["bot"]=True
    out.append(json.dumps(o))
open(dst,"w").write("\n".join(out)+"\n")
PY
  (cd tools/keeper && P2_NET=devnet npx tsx ../p6/crown-harness.ts "$MID" 4,7 "$ROOT/$OUT/harness-$i.json" > "$ROOT/$OUT/harness-$i.log" 2>&1)
  for _ in $(seq 1 120); do n=$(grep -c '"ev":"match_done"' "$ARCH/keeper-devnet-arena3.jsonl"); [ "${n:-0}" -ge "$i" ] 2>/dev/null && grep -q "$(python3 -c "print('match_done')")" <(tail -n 2 "$ARCH/keeper-devnet-arena3.jsonl") && break; sleep 3; done
  M=$(cd tools/keeper && P2_NET=devnet node --experimental-strip-types --no-warnings -e "import('./lib.ts').then(({pda})=>console.log(pda.match(Buffer.from('$MID','hex')).toBase58()))")
  sleep 5
  CH=$(python3 -c "import json;s=json.load(open('$ARCH/$M/summary.json'));r=s['receipt'];print(r['champion'], r['humanMask'], r['flags'], r['mintedMask'], s.get('finalizeMode'), json.dumps(s.get('rewards',{}).get('attempts')))" 2>/dev/null)
  echo "match $i $M champion/humanMask/flags/minted/mode/keeperMintAttempts: $CH" | tee -a "$OUT/runs.txt"
  cp -R "$ARCH/$M" "$OUT/match-$i" 2>/dev/null; rm -f "$OUT/match-$i/world2.bin"
  set -- $CH; if [ "${1:-255}" = "4" ] || [ "${1:-255}" = "7" ]; then echo "HUMAN CROWN in match $i ($M)" | tee -a "$OUT/runs.txt"; break; fi
done
tail -3 "$OUT/keeper.log"
