#!/usr/bin/env bash
# G4.5 tuning loop (local, dev only): rebuild Map_CrumbleShelf (Lid Flip), rebuild the Mac dev server, run the Final QA.
# usage: scripts/g45-iter.sh [runs=10] [tier=both] [extra QA args...]   → .m1-logs/g45/qa.md / qa.log
#        G45_NOBUILD=1 skips the two Unity steps (QA only on the current build).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; RUNS="${1:-10}"; TIER="${2:-both}"; shift $(( $# < 2 ? $# : 2 ))
if [[ -z "${G45_NOBUILD:-}" ]]; then
  bash "$ROOT/scripts/m1-unity.sh" cs-build -executeMethod CrumbleShelfBuilder.Run | grep -E "Lid Flip|mapHash|error CS|exit=" || true
  bash "$ROOT/scripts/m1-unity.sh" macserver -buildTarget OSXUniversal -executeMethod Build.MacDevServer | grep -E "result|error CS|exit=" || true
fi
BIN="$(ls "$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app/Contents/MacOS/"* | head -1)"
mkdir -p "$ROOT/.m1-logs/g45"
"$BIN" -batchmode -nographics -fallrushQA "$RUNS" -fallrushQATier "$TIER" -fallrushMap CrumbleShelf -fallrushQAOut "$ROOT/.m1-logs/g45/qa.md" -logFile "$ROOT/.m1-logs/g45/qa.log" "$@" > /dev/null 2>&1 || true
grep -h "\[qa-lid\]\|\[qa\] Final\|\[qa\] PASS\|\[qa\] FAIL\|NOT DETERMINISTIC" "$ROOT/.m1-logs/g45/qa.log" | sed 's/^.*\[qa/[qa/' | sed 's/elapsed=.*//'
