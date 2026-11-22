#!/usr/bin/env bash
# P0: start the local MagicBlock stack with the p0_tick spike program, run a command, tear down.
# Run ONLY inside the shared lock:
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p0-local -- bash scripts/p0-local.sh '<cmd>'
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SP="$ROOT/spikes/p0"; LOGS="$SP/.p0-logs"; mkdir -p "$LOGS"
STACK_RE="mb-stack|mb-test-validator|solana-test-validator|ephemeral-validator|query-filtering-service|vrf-oracle"
MB_PID=""
cleanup() {
  local code=$?; trap - EXIT INT TERM
  [ -n "$MB_PID" ] && kill -TERM "$MB_PID" 2>/dev/null; sleep 2; [ -n "$MB_PID" ] && kill -9 "$MB_PID" 2>/dev/null
  # only our own children: the stack was started by this script (lock guarantees nobody else runs one)
  for n in mb-stack mb-test-validator solana-test-validator ephemeral-validator query-filtering-service; do pkill -f "$n" 2>/dev/null; done
  echo "stack stopped"; exit "$code"
}
if pgrep -fl "$STACK_RE" >/dev/null; then echo "a validator is already running:"; pgrep -fl "$STACK_RE"; exit 1; fi
trap cleanup EXIT INT TERM
cd "$SP"; rm -rf ./magicblock-test-storage ./test-ledger
WALLET=$(solana-keygen pubkey "$HOME/.config/solana/id.json")
RUST_LOG=info mb-stack --reset \
  --account mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev "$SP/fixtures/local-validator-identity-account.json" \
  --upgradeable-program "$SP/target/deploy/p0_tick-keypair.json" "${P0_SO:-$SP/target/deploy/p0_tick.so}" "$WALLET" \
  > "$LOGS/mb-stack.log" 2>&1 < /dev/null &
MB_PID=$!
for i in $(seq 1 240); do
  kill -0 "$MB_PID" 2>/dev/null || { echo "mb-stack exited"; tail -40 "$LOGS/mb-stack.log"; exit 1; }
  grep -q "MagicBlock stack is ready" "$LOGS/mb-stack.log" && { echo "stack ready after ${i}s"; break; }
  sleep 1
done
cd "$ROOT/tools/load"
export P0_NET=local
bash -c "$1"
