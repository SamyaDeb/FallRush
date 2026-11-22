#!/usr/bin/env bash
# P1-N (locked, one job): Mac Feel Lab build → EditMode (incl. P1FeelLabTests through the FFI) → smoke test.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
bash scripts/p1-build-mac.sh
bash scripts/m1-unity.sh p1-tests2 --tests -runTests -testPlatform EditMode -testResults "$ROOT/.m1-logs/p1-tests2.xml" || true
grep -o 'total="[0-9]*" passed="[0-9]*" failed="[0-9]*"' .m1-logs/p1-tests2.xml | head -1
bash scripts/p1-smoke.sh
