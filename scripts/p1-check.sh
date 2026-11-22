#!/usr/bin/env bash
# P1 rush-core checks (P1 prompt A.6).
#   scripts/p1-check.sh          unlocked: test, clippy -D warnings, the float scan, the wasm build, the Node WASM parity
#   scripts/p1-check.sh --sbf    + locked: cargo build-sbf and the LiteSVM parity harness (heavy-lock.sh)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT/crates"
export CARGO_BUILD_JOBS="${CARGO_BUILD_JOBS:-2}"
N="nice -n 10"
echo "== cargo test (rush-core, sim, ffi, wasm host)"
$N cargo test --offline -p rush-core -p rush-sim -p rush-core-ffi -p rush-core-wasm 2>&1 | grep -E "^test result|FAILED|panicked" || true
$N cargo test --offline -p rush-core -p rush-sim -p rush-core-ffi -p rush-core-wasm >/dev/null 2>&1
echo "== clippy -D warnings"
$N cargo clippy --offline -p rush-core -p rush-sim -p rush-core-ffi -p rush-core-wasm --all-targets -- -D warnings
echo "== float scan (tests/float_oracles.rs::no_floats_in_src) — run above; grep as a second guard"
if grep -rnwE 'f32|f64' rush-core/src | grep -v '^\S*:\s*//' ; then echo "floats in rush-core/src"; exit 1; fi
echo "== vectors (native, debug build: overflow checks on)"
$N cargo run --offline -q -p rush-sim -- vectors check
echo "== wasm32-unknown-unknown build + Node parity"
$N cargo build --offline --release -p rush-core-wasm --target wasm32-unknown-unknown
node "$ROOT/tools/wasm-run.mjs" target/wasm32-unknown-unknown/release/rush_core_wasm.wasm rush-core/tests/vectors
echo "== wasm32v1-none (no_std) build of rush-core"
$N cargo build --offline --release -p rush-core --target wasm32v1-none
if [[ "${1:-}" == "--sbf" ]]; then
  echo "== sBPF (locked)"
  bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p1-sbf -- bash -c "
    cd '$ROOT/crates' && cargo build-sbf --manifest-path rush-core-sbf-check/Cargo.toml --sbf-out-dir target/deploy &&
    CARGO_BUILD_JOBS=2 cargo test --offline --release -p rush-core-sbf-check -- --nocapture"
fi
echo "p1-check: OK"
