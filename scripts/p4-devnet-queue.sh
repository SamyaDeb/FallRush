#!/usr/bin/env bash
# P4: the rest of the devnet exit gate, one job at a time (each step takes the heavy lock itself).
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
L=/Users/samya/Downloads/FallRush/scripts/heavy-lock.sh
echo "== tests + mac build $(date -u +%T)"
bash $L p4-unity-tests -- bash scripts/m1-unity.sh p4-tests --tests -runTests -testPlatform EditMode -testResults "$ROOT/.m1-logs/p4-tests.xml" > .p4-unity-tests.log 2>&1
grep -o 'total="[0-9]*" passed="[0-9]*" failed="[0-9]*"' .m1-logs/p4-tests.xml | head -1
bash $L p4-mac -- bash scripts/m1-unity.sh p4-mac -buildTarget OSXUniversal -executeMethod Build.MacClient > .p4-mac-build.log 2>&1
grep -E "Build Finished" $(ls -t .m1-logs/p4-mac-*.log | head -1)
echo "== window +12 x3 $(date -u +%T)"
WINDOW=12 LEAD=12 bash $L p4-devnet-w12 -- bash scripts/p4-devnet-final.sh 3 w12 > .p4-w12.log 2>&1
python3 tools/p4/window-summary.py docs/proof/p4/devnet-window > docs/proof/p4/devnet-window/summary.json
echo "== feel x2 (drops) $(date -u +%T)"
DROPS=1 WINDOW=12 LEAD=12 bash $L p4-devnet-feel -- bash scripts/p4-devnet-final.sh 2 feel > .p4-feel.log 2>&1
for i in 1 2 3; do echo "== hybrid $i $(date -u +%T)"; bash $L p4-hybrid -- bash scripts/p4-devnet-hybrid.sh > .p4-hybrid-$i.log 2>&1; tail -4 .p4-hybrid-$i.log | cut -c1-300; done
echo "== done $(date -u +%T)"
