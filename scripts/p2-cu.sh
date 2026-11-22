#!/usr/bin/env bash
# P2 0.2: sBPF CU per tick on the real map blobs (LiteSVM). Heavy: runs under heavy-lock.sh.
#   scripts/p2-cu.sh <label> [env BENCH_SECS / BENCH_RATE / BENCH_MAPS / BENCH_SCENARIO]
# Writes docs/proof/p2/cu-<label>.md and prints the .so size.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LABEL="${1:-run}"
export BENCH_OUT="$ROOT/docs/proof/p2/cu-$LABEL.md"
bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh "p2-cu-$LABEL" -- bash -c "
  cd '$ROOT/crates' && CARGO_BUILD_JOBS=2 cargo build-sbf --manifest-path rush-core-sbf-check/Cargo.toml --sbf-out-dir target/deploy 2>&1 | tail -3 &&
  ls -l target/deploy/rush_core_sbf_check.so &&
  CARGO_BUILD_JOBS=2 cargo test --offline --release -p rush-core-sbf-check --test ${BENCH_TEST:-bench} -- --nocapture 2>&1 | grep -vE '^\s*(Compiling|Running)' | tail -25"
