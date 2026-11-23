#!/usr/bin/env bash
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
L=/Users/samya/Downloads/FallRush/scripts/heavy-lock.sh
bash $L p4-mac -- bash scripts/m1-unity.sh p4-mac -buildTarget OSXUniversal -executeMethod Build.MacClient > .p4-mac-build.log 2>&1
echo "== mac $(grep -h "Build Finished" $(ls -t .m1-logs/p4-mac-*.log | head -1))"
for i in 4 5; do echo "== hybrid $i $(date -u +%T)"; bash $L p4-hybrid -- bash scripts/p4-devnet-hybrid.sh > .p4-hybrid-$i.log 2>&1; tail -2 .p4-hybrid-$i.log | cut -c1-200; done
echo "== webgl $(date -u +%T)"
bash $L p4-webgl -- bash scripts/p4-webgl.sh build a d e > .p4-webgl.log 2>&1
tail -25 .p4-webgl.log | cut -c1-250
echo "== done $(date -u +%T)"
