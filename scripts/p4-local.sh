#!/usr/bin/env bash
# P4: start the local MagicBlock stack (+ Metaplex Core) (mb-stack + 2 VRF oracles) with the localnet fall_rush build, run a command
# against it, always tear the stack down. Run inside the heavy lock:
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p2-local -- bash scripts/p2-local.sh '<cmd>'
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROG="$ROOT/programs"; LOGS="$ROOT/.p4-logs"; mkdir -p "$LOGS"
STACK_RE="mb-stack|mb-test-validator|solana-test-validator|ephemeral-validator|query-filtering-service|vrf-oracle"
PIDS=""
cleanup() {
  local code=$?; trap - EXIT INT TERM
  for p in $PIDS; do kill -TERM "$p" 2>/dev/null; done; sleep 2; for p in $PIDS; do kill -9 "$p" 2>/dev/null; done
  for n in mb-stack mb-test-validator solana-test-validator ephemeral-validator query-filtering-service vrf-oracle; do pkill -f "$n" 2>/dev/null; done
  echo "stack stopped"; exit "$code"
}
if pgrep -fl "$STACK_RE" >/dev/null; then echo "a validator is already running:"; pgrep -fl "$STACK_RE"; exit 1; fi
trap cleanup EXIT INT TERM
cd "$PROG"; rm -rf ./magicblock-test-storage ./test-ledger
WALLET=$(solana-keygen pubkey "$HOME/.config/solana/id.json")
RUST_LOG=info mb-stack --reset \
  --account mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev "$PROG/tests/fixtures/local-validator-identity-account.json" \
  --upgradeable-program "$PROG/target/deploy/fall_rush-keypair.json" "$PROG/target/deploy/fall_rush.so" "$WALLET" \
  --url https://api.devnet.solana.com --clone-upgradeable-program KeyspM2ssCJbqUhQ4k7sveSiY4WjnYsrXkC8oDbwde5 --clone-upgradeable-program CoREENxT6tW1HoK8ypY1SxRMZTcVPm7R94rH4PZNhX7d \
  > "$LOGS/mb-stack.log" 2>&1 < /dev/null &
PIDS="$!"
for i in $(seq 1 240); do
  grep -q "MagicBlock stack is ready" "$LOGS/mb-stack.log" && { echo "stack ready after ${i}s"; break; }
  sleep 1
done
grep -q "MagicBlock stack is ready" "$LOGS/mb-stack.log" || { echo "mb-stack not ready"; tail -40 "$LOGS/mb-stack.log"; exit 1; }
VRF_ORACLE_SKIP_PREFLIGHT=true RPC_URL=http://localhost:8899 WEBSOCKET_URL=ws://localhost:8900 RUST_LOG=info vrf-oracle > "$LOGS/vrf-base.log" 2>&1 < /dev/null &
PIDS="$PIDS $!"
VRF_ORACLE_SKIP_PREFLIGHT=true RPC_URL=http://localhost:7799 WEBSOCKET_URL=ws://localhost:7800 RUST_LOG=info vrf-oracle > "$LOGS/vrf-er.log" 2>&1 < /dev/null &
PIDS="$PIDS $!"
sleep 3
solana airdrop 100 "$WALLET" --url http://localhost:8899 >/dev/null 2>&1 || true
cd "$ROOT"
export P2_NET=local
bash -c "$1"
