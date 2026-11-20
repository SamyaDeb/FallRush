#!/usr/bin/env bash
# pnpm m0:test:local — build, run the Rust + TS deal-parity tests, start the local MagicBlock
# stack (mb-stack + 2 vrf-oracles), run the lifecycle test, then always tear the stack down.
# Mirrors magicblock-examples@101e8fd scripts/test-locally.sh + scripts/local-env.sh, except that
# it does NOT run `solana config set` (your global Solana CLI config is left untouched).
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROG="$ROOT/programs"
LOGS="$ROOT/.m0-logs"
mkdir -p "$LOGS"
MB_PID=""; VRF_PID=""; VRF_ER_PID=""
STACK_RE="mb-stack|mb-test-validator|solana-test-validator|ephemeral-validator|query-filtering-service|vrf-oracle"

stack_procs() { pgrep -fl "$STACK_RE" || true; }

cleanup() {
  local code=$?
  trap - EXIT INT TERM
  echo ""
  echo "Stopping local MagicBlock stack..."
  for pid in $MB_PID $VRF_PID $VRF_ER_PID; do kill -TERM "$pid" 2>/dev/null || true; done
  sleep 2
  for pid in $MB_PID $VRF_PID $VRF_ER_PID; do kill -9 "$pid" 2>/dev/null || true; done
  for name in mb-stack mb-test-validator solana-test-validator ephemeral-validator query-filtering-service vrf-oracle; do
    pkill -f "$name" 2>/dev/null || true
  done
  sleep 1
  if [ -z "$(stack_procs)" ]; then echo "✓ stack stopped"; else echo "✗ still running:"; stack_procs; fi
  exit "$code"
}

for bin in anchor mb-stack vrf-oracle solana-keygen cargo pnpm; do
  command -v "$bin" >/dev/null || { echo "missing '$bin' on PATH"; exit 1; }
done
if [ -n "$(stack_procs)" ]; then
  echo "A MagicBlock/Solana validator is already running; stop it first:"; stack_procs; exit 1
fi
trap cleanup EXIT INT TERM

echo "== 1/5 anchor build (features: localnet) =="
(cd "$PROG" && anchor build -- --features localnet) > "$LOGS/build.log" 2>&1 || { tail -60 "$LOGS/build.log"; exit 1; }
echo "build ok ($(grep -c '^warning' "$LOGS/build.log") warnings, log: .m0-logs/build.log)"

echo "== 2/5 deal parity (Rust + TS) =="
(cd "$PROG" && cargo test -q -p fall_rush --test deal_parity) > "$LOGS/parity-rust.log" 2>&1 \
  || { tail -40 "$LOGS/parity-rust.log"; exit 1; }
grep "test result" "$LOGS/parity-rust.log" | sed 's/^/rust: /'
(cd "$ROOT/packages/shared" && pnpm -s test) > "$LOGS/parity-ts.log" 2>&1 || { tail -40 "$LOGS/parity-ts.log"; exit 1; }
grep -E "^ℹ (tests|pass|fail) " "$LOGS/parity-ts.log" | tr '\n' ' ' | sed 's/^/ts: /'; echo

echo "== 3/5 start mb-stack =="
WALLET_PUBKEY=$(solana-keygen pubkey "$HOME/.config/solana/id.json")
cd "$PROG"
rm -rf ./magicblock-test-storage
RUST_LOG=info mb-stack --reset \
  --account mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev "$PROG/tests/fixtures/local-validator-identity-account.json" \
  --upgradeable-program "$PROG/target/deploy/fall_rush-keypair.json" "$PROG/target/deploy/fall_rush.so" "$WALLET_PUBKEY" \
  > "$LOGS/mb-stack.log" 2>&1 < /dev/null &
MB_PID=$!
READY=0
for i in $(seq 1 "${MB_STACK_READY_TIMEOUT:-180}"); do
  kill -0 "$MB_PID" 2>/dev/null || { echo "mb-stack exited:"; tail -80 "$LOGS/mb-stack.log"; exit 1; }
  if grep -q "MagicBlock stack is ready" "$LOGS/mb-stack.log"; then READY=1; echo "stack ready after ${i}s"; break; fi
  sleep 1
done
[ "$READY" = 1 ] || { echo "mb-stack not ready:"; tail -80 "$LOGS/mb-stack.log"; exit 1; }

echo "== 4/5 start vrf-oracles (base + ER) =="
VRF_ORACLE_SKIP_PREFLIGHT=true RPC_URL=http://localhost:8899 WEBSOCKET_URL=ws://localhost:8900 RUST_LOG=info \
  vrf-oracle > "$LOGS/vrf-oracle.log" 2>&1 < /dev/null &
VRF_PID=$!
VRF_ORACLE_SKIP_PREFLIGHT=true RPC_URL=http://localhost:7799 WEBSOCKET_URL=ws://localhost:7800 RUST_LOG=info \
  vrf-oracle > "$LOGS/vrf-oracle-er.log" 2>&1 < /dev/null &
VRF_ER_PID=$!
sleep 3
kill -0 "$VRF_PID" 2>/dev/null || { echo "base vrf-oracle died:"; tail -40 "$LOGS/vrf-oracle.log"; exit 1; }
kill -0 "$VRF_ER_PID" 2>/dev/null || { echo "ER vrf-oracle died:"; tail -40 "$LOGS/vrf-oracle-er.log"; exit 1; }
echo "oracles running"

echo "== 5/5 lifecycle test =="
# shellcheck source=scripts/local-env.sh
. "$ROOT/scripts/local-env.sh"
pnpm -s test:lifecycle 2>&1 | tee "$LOGS/lifecycle.log"
TEST_CODE=${PIPESTATUS[0]}
if [ "$TEST_CODE" != 0 ]; then
  echo "----- mb-stack.log WARN/ERROR (last 40) -----"; grep -iE "error|warn" "$LOGS/mb-stack.log" | tail -40
  echo "----- vrf-oracle-er.log (last 20) -----"; tail -20 "$LOGS/vrf-oracle-er.log"
fi
exit "$TEST_CODE"
