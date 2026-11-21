#!/usr/bin/env bash
# G4.4 tuning loop (local, dev only): rebuild Map_StarScramble, rebuild the Mac dev server, run the Skill QA.
# usage: scripts/g44-iter.sh [runs=10] [extra QA args...]   → .m1-logs/g44/qa.md / qa.log
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; RUNS="${1:-10}"; shift || true
bash "$ROOT/scripts/m1-unity.sh" ss-build -executeMethod StarScrambleBuilder.Run | grep -E "Star Fountain|error CS|exit=" || true
bash "$ROOT/scripts/m1-unity.sh" macserver -buildTarget OSXUniversal -executeMethod Build.MacDevServer | grep -E "result|error CS" || true
BIN="$(ls "$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app/Contents/MacOS/"* | head -1)"
mkdir -p "$ROOT/.m1-logs/g44"
"$BIN" -batchmode -nographics -fallrushQA "$RUNS" -fallrushQATier both -fallrushMap StarScramble -fallrushQAOut "$ROOT/.m1-logs/g44/qa.md" -logFile "$ROOT/.m1-logs/g44/qa.log" "$@" > /dev/null 2>&1 || true
grep -h "qa-skill\|qa-camp" "$ROOT/.m1-logs/g44/qa.log" | sed 's/^.*\[qa-/[qa-/'
