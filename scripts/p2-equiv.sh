#!/usr/bin/env bash
# P2 0.2: behaviour equivalence of a rush-core change vs the P1 baseline binary (../FallRush-p1, read-only):
# full 12-bot matches through the director at 30 Hz must give identical transcripts.  scripts/p2-equiv.sh [runs]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
N="${1:-5}"
BASE="${P2_BASE:-/Users/samya/Downloads/FallRush-p1/crates/target/release/rush-sim}"  # P2_BASE: another baseline binary
(cd "$ROOT/crates" && CARGO_BUILD_JOBS=2 nice -n 10 cargo build --offline --release -q -p rush-sim)
"$BASE" soak --runs "$N" --rate 30 --maps "$ROOT/maps" > "${TMPDIR:-/tmp}/p2-equiv-base.txt"
"$ROOT/crates/target/release/rush-sim" soak --runs "$N" --rate 30 --maps "$ROOT/maps" > "${TMPDIR:-/tmp}/p2-equiv-new.txt"
if diff "${TMPDIR:-/tmp}/p2-equiv-base.txt" "${TMPDIR:-/tmp}/p2-equiv-new.txt"; then echo "p2-equiv: IDENTICAL ($N matches)"; else echo "p2-equiv: DIFFERENT"; exit 1; fi
# per-map QA (single rounds, every seed's transcript hash is in the report); wall time ignored
for m in CrumbleShelf StarScramble GooTower BlockPartyDash; do
  a=$("$BASE" qa "$ROOT/maps/$m.mapblob" --runs 20 --rate 30 2>&1 | grep -v wall_s | shasum)
  b=$("$ROOT/crates/target/release/rush-sim" qa "$ROOT/maps/$m.mapblob" --runs 20 --rate 30 2>&1 | grep -v wall_s | shasum)
  if [ "$a" = "$b" ]; then echo "p2-equiv: qa $m IDENTICAL"; else echo "p2-equiv: qa $m DIFFERENT"; exit 1; fi
done
