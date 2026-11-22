#!/usr/bin/env bash
# P1-K/L (locked): export the 6 map scenes to maps/*.mapjson in batch Unity, build the blobs + hashes, run EditMode.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p1-export -- bash scripts/p1-export.sh [--no-tests]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
mkdir -p maps
bash scripts/m1-unity.sh p1-export -executeMethod MapBlobExport.ExportAll
cd crates && cargo build --offline --release -q -p rush-sim && cd ..
: > maps/map_blob_hashes_v1.txt
for j in maps/*.mapjson; do
  k="$(basename "$j" .mapjson)"
  crates/target/release/rush-sim mapblob build "$j" "maps/$k.mapblob"
  crates/target/release/rush-sim mapblob hash "maps/$k.mapblob" >> maps/map_blob_hashes_v1.txt
  crates/target/release/rush-sim mapblob inspect "maps/$k.mapblob" > "maps/$k.inspect.txt"
done
cat maps/map_blob_hashes_v1.txt
if [[ "${1:-}" != "--no-traces" && "${1:-}" != "--no-tests" ]]; then
  bash scripts/m1-unity.sh p1-traces -executeMethod P1Traces.RunAll
  ls docs/proof/p1/traces | wc -l
fi
if [[ "${1:-}" != "--no-tests" ]]; then
  bash scripts/m1-unity.sh p1-tests --tests -runTests -testPlatform EditMode -testResults "$ROOT/.m1-logs/p1-tests.xml"
  grep -o 'total="[0-9]*" passed="[0-9]*" failed="[0-9]*"' .m1-logs/p1-tests.xml | head -1
fi
