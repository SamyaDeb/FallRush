#!/usr/bin/env bash
# fullchain Gate 2 (locked): the real flow on devnet, dev arena 7 — the Classic Mac dev server in soak mode (12 bots,
# GAMESERVER_ONCHAIN_ALL: every match handed to the rollup at roster lock) + the production keeper daemon
# (ONCHAIN_ALL=1: start_sim, tasks + crank, markets, settle, receipt, finalize) + two bettors on arena 7 (hand-checked
# payouts). Then replay-check of every archived match. Live arenas 2 / 3 are not touched.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh fc-gate2 -- bash scripts/fc-gate2.sh [MATCHES]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
N=${1:-5}
RUN="fc-gate2-$(date +%Y%m%d-%H%M%S)"; OUT="docs/proof/fullchain/gate2/$RUN"; mkdir -p "$OUT" .m1-logs
LOGDIR="$ROOT/.m1-logs/$RUN-matchlogs"; ARCH="$ROOT/.m1-logs/$RUN-archive"; mkdir -p "$LOGDIR" "$ARCH"
ARENA=3uJwGdZSpdXN49uBPVKRgHfDLhZgc8Kv4bBUUiRnCin3
WORLD=5e6BG7aod9wft3R36yR72gWJSKjHgdgpLv1YJbDzBDPf
bal() { echo "$(date -u +%T) admin $(solana balance -u devnet -k ~/.config/solana/id.json) authority $(solana balance -u devnet -k ~/.config/fall-rush/devnet-game-authority.json)"; }
bal | tee "$OUT/balances.txt"
bash scripts/m1-macserver.sh stop >/dev/null 2>&1; sleep 1
: > .m1-logs/macserver.log
cleanup() { [[ -n "${KEEPER:-}" ]] && kill $KEEPER 2>/dev/null; [[ -n "${BET:-}" ]] && kill $BET 2>/dev/null; bash scripts/m1-macserver.sh stop >/dev/null 2>&1; }
trap cleanup EXIT
P2_NET=devnet ARENA=7 MATCHES=$N node --experimental-strip-types tools/fullchain/bettor.ts > "$OUT/bettor.log" 2>&1 &
BET=$!
P2_NET=devnet ARENA=7 ONCHAIN_ALL=1 LOG_DIR="$LOGDIR" ARCHIVE="$ARCH" STATUS_PORT=3197 MM=0 SEALED=0 SEASONS=0 \
  node --experimental-strip-types tools/keeper/daemon.ts > "$OUT/keeper.log" 2>&1 &
KEEPER=$!
sleep 5
GAMESERVER_ONCHAIN_ALL=1 bash scripts/m1-macserver.sh start -fallrushBots 12 -fallrushMatchSize 12 -fallrushMatches "$N" \
  -fallrushMatchLogDir "$LOGDIR" -fallrushOnchainFinal -fallrushOnchainFinalNet devnet \
  -fallrushOnchainArena "$ARENA" -fallrushOnchainWorld "$WORLD" -fallrushOnchainAll
# wait for N archived summaries with doneAt (a full match ≈ 7–9 min + setup)
for _ in $(seq 1 $(( N * 900 ))); do
  D=$(python3 -c "import json,glob;print(sum(1 for f in glob.glob('$ARCH/*/summary.json') if (lambda s: s.get('doneAt') and not s.get('resumed'))(json.load(open(f)))))" 2>/dev/null || echo 0)
  [[ "$D" -ge "$N" ]] && break
  kill -0 $KEEPER 2>/dev/null || { echo "keeper exited"; break; }
  sleep 1
done
echo "archived matches done: $D / $N"
for _ in $(seq 1 120); do kill -0 $BET 2>/dev/null || break; sleep 1; done
bal | tee -a "$OUT/balances.txt"
# evidence: per match summary + replay check against the sealed blobs
for d in "$ARCH"/*/; do
  m=$(basename "$d"); [[ -f "$d/summary.json" ]] || continue
  mkdir -p "$OUT/$m"; cp "$d"/summary.json "$d"/keeper.jsonl "$OUT/$m/" 2>/dev/null; cp "$d"/classic.jsonl "$OUT/$m/" 2>/dev/null
  W=$(ls "$d"/world2.bin "$d"/world.bin 2>/dev/null | head -1)
  [[ -n "$W" ]] && node tools/p7/replay-check.mjs "$W" --sealed --summary "$d/summary.json" > "$OUT/$m/replay.txt" 2>&1
  echo "$m: $(tail -1 "$OUT/$m/replay.txt" 2>/dev/null)"
done | tee "$OUT/replays.txt"
grep -E "\[gs\] (fullchain|onchainAll|round |on-chain)" .m1-logs/macserver.log | cut -c1-300 > "$OUT/server.txt"
cp "$LOGDIR"/match-*.jsonl "$OUT/" 2>/dev/null
echo "gate2 run: $OUT"
