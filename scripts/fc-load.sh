#!/usr/bin/env bash
# fullchain plan item #10: multi-human load test on dev arena 7 (never the live arenas 2/3). The Classic Mac dev
# server in soak mode (GAMESERVER_ONCHAIN_ALL: every match handed to the rollup at roster lock) + the production keeper
# daemon (ONCHAIN_ALL=1, CRANK_GATE passed through) + N headless bundle clients (tools/fullchain/load.ts) that join the
# lobby and send submit_input at 10 Hz for the whole start_sim match, + BROWSERS browser humans you add by hand.
# Then replay-check of every archived match. Run twice and compare:
#   CRANK_GATE=0 bash scripts/heavy-lock.sh fc-load -- bash scripts/fc-load.sh
#   CRANK_GATE=1 bash scripts/heavy-lock.sh fc-load -- bash scripts/fc-load.sh
#   node --experimental-strip-types tools/fullchain/load.ts compare <gate0 load.json> <gate1 load.json>
# Env: N (11) BROWSERS (0) MATCHES (1) CRANK_GATE (0) FULL_MIN_HUMANS (N+BROWSERS: the keeper waits for this many
# on-chain joins before start_sim, if the keeper supports it; otherwise only its Classic-roster humans) and the
# load.ts env (LEAD, RAW_SOCKETS, FUND_FROM, ...). Cost: ~0.01 SOL per new burner (once; reused from
# tools/fullchain/.keys) + the keeper's usual ~0.011-0.0125 SOL per match.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
N=${N:-11}; BROWSERS=${BROWSERS:-0}; MATCHES=${MATCHES:-1}; CRANK_GATE=${CRANK_GATE:-0}
FULL_MIN_HUMANS=${FULL_MIN_HUMANS:-$((N + BROWSERS))}
RUN="fc-load-g${CRANK_GATE}-$(date +%Y%m%d-%H%M%S)"; OUT="docs/proof/fullchain/load/$RUN"; mkdir -p "$OUT" .m1-logs
LOGDIR="$ROOT/.m1-logs/$RUN-matchlogs"; ARCH="$ROOT/.m1-logs/$RUN-archive"; mkdir -p "$LOGDIR" "$ARCH"
ARENA=3uJwGdZSpdXN49uBPVKRgHfDLhZgc8Kv4bBUUiRnCin3   # dev arena 7 (PDA ["arena", 7])
WORLD=5e6BG7aod9wft3R36yR72gWJSKjHgdgpLv1YJbDzBDPf
bal() { echo "$(date -u +%T) admin $(solana balance -u devnet -k ~/.config/solana/id.json) authority $(solana balance -u devnet -k ~/.config/fall-rush/devnet-game-authority.json)"; }
bal | tee "$OUT/balances.txt"
# no network: the config, encoding and metrics must be sane before anything is sent
node --experimental-strip-types tools/fullchain/load.ts --dry-run > "$OUT/dry-run.json" || { echo "dry-run failed"; exit 1; }
bash scripts/m1-macserver.sh stop >/dev/null 2>&1; sleep 1
: > .m1-logs/macserver.log
cleanup() { [[ -n "${KEEPER:-}" ]] && kill $KEEPER 2>/dev/null; [[ -n "${LOAD:-}" ]] && kill $LOAD 2>/dev/null; bash scripts/m1-macserver.sh stop >/dev/null 2>&1; }
trap cleanup EXIT
# the headless clients first: init, funding and session tokens take ~1-2 s each on the base layer
P2_NET=devnet ARENA=7 N=$N BROWSERS=$BROWSERS MATCHES=$MATCHES CRANK_GATE=$CRANK_GATE LABEL="gate$CRANK_GATE" \
  MATCH_LOG_DIR="$LOGDIR" OUT="$OUT/load.json" node --experimental-strip-types tools/fullchain/load.ts > "$OUT/load.log" 2>&1 &
LOAD=$!
for _ in $(seq 1 300); do grep -q '"ev":"waitLobby"' "$OUT/load.log" 2>/dev/null && break; kill -0 $LOAD 2>/dev/null || { echo "load tool exited:"; tail -5 "$OUT/load.log"; exit 1; }; sleep 1; done
P2_NET=devnet ARENA=7 ONCHAIN_ALL=1 CRANK_GATE=$CRANK_GATE FULL_MIN_HUMANS=$FULL_MIN_HUMANS LOG_DIR="$LOGDIR" ARCHIVE="$ARCH" STATUS_PORT=3198 MM=0 SEALED=0 SEASONS=0 \
  node --experimental-strip-types tools/keeper/daemon.ts > "$OUT/keeper.log" 2>&1 &
KEEPER=$!
sleep 5
GAMESERVER_ONCHAIN_ALL=1 bash scripts/m1-macserver.sh start -fallrushBots 12 -fallrushMatchSize 12 -fallrushMatches "$MATCHES" \
  -fallrushMatchLogDir "$LOGDIR" -fallrushOnchainFinal -fallrushOnchainFinalNet devnet \
  -fallrushOnchainArena "$ARENA" -fallrushOnchainWorld "$WORLD" -fallrushOnchainAll
[[ "$BROWSERS" -gt 0 ]] && echo ">>> now join $BROWSERS browser player(s) to the Mac dev server's lobby (arena 7)"
# the load tool exits after MATCHES matches (0 = every criterion passed)
wait $LOAD; RC=$?; LOAD=
echo "load tool exit $RC (0 = pass)"
for _ in $(seq 1 600); do D=$(grep -l '"doneAt"' "$ARCH"/*/summary.json 2>/dev/null | wc -l | tr -d ' '); [[ "$D" -ge "$MATCHES" ]] && break; sleep 1; done
bal | tee -a "$OUT/balances.txt"
for d in "$ARCH"/*/; do
  m=$(basename "$d"); [[ -f "$d/summary.json" ]] || continue
  mkdir -p "$OUT/$m"; cp "$d"/summary.json "$d"/keeper.jsonl "$d"/ticks.json "$OUT/$m/" 2>/dev/null
  W=$(ls "$d"/world2.bin "$d"/world.bin 2>/dev/null | head -1)
  [[ -n "$W" ]] && node tools/p7/replay-check.mjs "$W" --sealed --summary "$d/summary.json" > "$OUT/$m/replay.txt" 2>&1
  echo "$m: $(tail -1 "$OUT/$m/replay.txt" 2>/dev/null)"
done | tee "$OUT/replays.txt"
grep -E '"start_sim"|humans' "$OUT/keeper.log" | cut -c1-300 > "$OUT/keeper-start-sim.txt" 2>/dev/null
echo "load run: $OUT (load.json: rounds / confirm / joined / verdict)"
exit $RC
