#!/usr/bin/env bash
# P4 (locked): the economy on the local MagicBlock stack. localnet build → stack (+ mpl-core) → setup arenas 0 + 1 →
# security suite (arena 1) → one on-chain Final with the economy (arena 0: markets, mm, drops, receipt, capsules,
# rewards with a forced first failure) + the e2e bettors / spectator (tools/p4/econ-e2e.ts).
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p4-local -- bash scripts/p4-local-e2e.sh
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT/programs" && CARGO_BUILD_JOBS=2 nice -n 10 anchor build -- --features localnet 2>&1 | grep -E "^error|Finished" || exit 1
cd "$ROOT"
RUN="local-p4-$(date +%Y%m%d-%H%M%S)"
mkdir -p docs/proof/p4/local
bash scripts/p4-local.sh "
  set -x
  P2_NET=local ARENA=0 PARTS=1 node_modules/.bin/tsx tools/keeper/setup.ts > .p4-logs/setup0.log 2>&1 && tail -2 .p4-logs/setup0.log
  P2_NET=local ARENA=1 PARTS=1 node_modules/.bin/tsx tools/keeper/setup.ts > .p4-logs/setup1.log 2>&1 && tail -2 .p4-logs/setup1.log
  P2_NET=local ARENA=1 node_modules/.bin/tsx tools/keeper/security.ts > .p4-logs/security.log 2>&1; grep -cE '^PASS' .p4-logs/security.log; grep -E '^FAIL' .p4-logs/security.log
  cp docs/proof/p2/security-local.json docs/proof/p4/local/security-local.json 2>/dev/null
  ( sleep 25; P2_NET=local ARENA=0 RUN=$RUN node_modules/.bin/tsx tools/p4/econ-e2e.ts > .p4-logs/e2e.log 2>&1 ) &
  P2_NET=local ARENA=0 MATCHES=1 MODE=final ECON=1 FAIL_FIRST=1 RUN=$RUN node_modules/.bin/tsx tools/keeper/run.ts > .p4-logs/keeper.log 2>&1; echo keeper exit \$?
  wait
  tail -5 .p4-logs/keeper.log | cut -c1-800; tail -8 .p4-logs/e2e.log | cut -c1-800
  cp docs/proof/p2/runs/$RUN-m1/econ.json docs/proof/p4/local/econ-$RUN.json 2>/dev/null; cp docs/proof/p2/runs/$RUN-m1/summary.json docs/proof/p4/local/summary-$RUN.json 2>/dev/null
"
