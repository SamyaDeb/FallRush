#!/usr/bin/env bash
# P1-M (locked): real-map bot QA at 15 / 20 / 30 Hz (100 seeded runs per map per rate, one map × rate at a time),
# the 20-match soak at the default rate, and the work counters on the real maps.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p1-qa -- bash scripts/p1-qa.sh [runs]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT/crates"
cargo build --offline --release -q -p rush-sim
SIM=target/release/rush-sim
OUT="$ROOT/docs/proof/p1/qa"
mkdir -p "$OUT"
RUNS="${1:-100}"
: > "$OUT/qa.jsonl"
for m in BlockPartyDash GooTower StarScramble CrumbleShelf; do
  for r in 15 20 30; do
    $SIM qa "$ROOT/maps/$m.mapblob" --runs "$RUNS" --rate "$r" --tier normal --json | tee -a "$OUT/qa.jsonl"
  done
  $SIM qa "$ROOT/maps/$m.mapblob" --runs "$RUNS" --rate 20 --tier reference --json | tee -a "$OUT/qa.jsonl"
done
$SIM soak --runs 20 --rate 20 | tee "$OUT/soak-20hz.txt"
: > "$OUT/counters.md"
for m in BlockPartyDash GooTower StarScramble CrumbleShelf; do
  for r in 15 20 30; do $SIM counters --map "$ROOT/maps/$m.mapblob" --rate "$r" --secs 60 >> "$OUT/counters.md"; echo >> "$OUT/counters.md"; done
done
cat "$OUT/counters.md"
