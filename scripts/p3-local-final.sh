#!/usr/bin/env bash
# P3-I/J (locked, local stack): P2's local MagicBlock stack (its scripts/p2-local.sh, run from ../FallRush-p2) +
# P2's setup / keeper (unmodified) + this worktree's relay (--net local) + the Mac player: a Lid Flip Final, you +
# 2 bots, on the local rollup. Optional 2nd arg "revoke": after joining, revoke the session token (task J).
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p3-local -- bash scripts/p3-local-final.sh [N] [revoke]
set -uo pipefail
P3="$(cd "$(dirname "$0")/.." && pwd)"
P2="$P3/../FallRush-p2"
N="${1:-1}"; EXTRA="${2:-}"
cd "$P2"
bash scripts/p2-local.sh "
set -u
ARENA=0 PARTS=3 node_modules/.bin/tsx tools/keeper/setup.ts > '$P3/.m1-logs/p3-local-setup.log' 2>&1 || { tail -20 '$P3/.m1-logs/p3-local-setup.log'; exit 1; }
pkill -f '^node tools/p3/chain-relay' 2>/dev/null; sleep 1
for i in \$(seq 1 $N); do
  RUN=p3-local-\$(date +%Y%m%d-%H%M%S)
  ( cd '$P3' && exec node tools/p3/chain-relay.mjs --net local --fund-from \$HOME/.config/solana/id.json --fund-lamports 1000000000 ) > '$P3/.m1-logs/'\$RUN-relay.log 2>&1 &
  RELAY=\$!
  sleep 3
  '$P3/client-unity/Builds/Mac-p1-feellab/FallRush.app/Contents/MacOS/Fall Rush' -screen-width 1280 -screen-height 720 -screen-fullscreen 0 \
    -logFile '$P3/.m1-logs/'\$RUN-client.log -fallrushOnchain local -fallrushOnchainAuto 600 -fallrushOnchainShot '$P3/docs/proof/p3/local-'\$RUN.png -fallrushHud -fallrushName P3L &
  CLIENT=\$!
  for _ in \$(seq 1 60); do grep -q 'waitingLobby\|lobby' '$P3/.m1-logs/'\$RUN-relay.log 2>/dev/null && break; sleep 1; done
  if [ '$EXTRA' = revoke ]; then
    ( for _ in \$(seq 1 300); do grep -q 'joined: slot' '$P3/.m1-logs/'\$RUN-relay.log && break; sleep 1; done; sleep 25; echo REVOKE; curl -s -X POST http://127.0.0.1:8788/session/revoke ) > '$P3/.m1-logs/'\$RUN-revoke.log 2>&1 &
  fi
  ARENA=0 MATCHES=1 MODE=final INTERVAL_MS=50 RUN=\$RUN node --experimental-strip-types tools/keeper/run.ts > '$P3/.m1-logs/'\$RUN-keeper.log 2>&1
  echo \"keeper exit \$?\"
  sleep 6; kill \$CLIENT 2>/dev/null; sleep 1; kill \$RELAY 2>/dev/null; wait \$RELAY 2>/dev/null
  tail -2 '$P3/.m1-logs/'\$RUN-keeper.log | cut -c1-300
  grep '\[relay\] tick' '$P3/.m1-logs/'\$RUN-relay.log | tail -4 | cut -c1-200
  grep -E '^\[onchain\] (tick|RESULT|joined|session)' '$P3/.m1-logs/'\$RUN-client.log | tail -8 | cut -c1-260
  cat '$P3/.m1-logs/'\$RUN-revoke.log 2>/dev/null | cut -c1-300
done
"
