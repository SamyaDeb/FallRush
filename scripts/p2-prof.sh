#!/usr/bin/env bash
# P2 0.2: sBPF per-function profile of one real tick. Heavy (locked).
#   scripts/p2-prof.sh <map stem> [rush ticks before the traced tick]   e.g. scripts/p2-prof.sh BlockPartyDash 300
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MAP="${1:-CrumbleShelf}"; AFTER="${2:-300}"
TR="${TMPDIR:-/tmp}/p2-sbf-trace-$MAP"; rm -rf "$TR"; mkdir -p "$TR"
bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh "p2-prof-$MAP" -- bash -c "
  cd '$ROOT/crates' && CARGO_BUILD_JOBS=2 cargo build-sbf --manifest-path rush-core-sbf-check/Cargo.toml --sbf-out-dir target/deploy 2>&1 | grep -E '^error' ;
  SBF_OUT_DIR='$ROOT/crates/target/deploy' SBF_TRACE_DIR='$TR' PROF_MAP='$MAP' PROF_RUSH_TICKS='$AFTER' CARGO_BUILD_JOBS=2 cargo test --offline --release -p rush-core-sbf-check --test prof -- --ignored --nocapture 2>&1 | grep -E 'tick|panicked|error' | head -5"
R=$(ls -S "$TR"/*.regs | head -1)
python3 "$ROOT/tools/p2/sbf-prof.py" "$R" "$ROOT/crates/target/sbpf-solana-solana/release/rush_core_sbf_check.so" 45
rm -rf "$TR"
