#!/usr/bin/env bash
# G4.0c route-cost QA (doc 08 §8–9): per section × route, median time (reference bots) and hazard contacts (normal
# bots); flags FREE ROUTEs. Mac dev server, headless, no networking.
# usage: scripts/g4-route-qa.sh [runs=100] [map=BlockPartyDash] [out=docs/proof/g4/route-cost.md]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BIN="$(ls "$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app/Contents/MacOS/"* | head -1)"
RUNS="${1:-100}"; MAP="${2:-BlockPartyDash}"; OUTMD="${3:-$ROOT/docs/proof/g4/route-cost.md}"
[[ "$OUTMD" = /* ]] || OUTMD="$PWD/$OUTMD"   # the player runs from its own folder
mkdir -p "$ROOT/.m1-logs/g4" "$(dirname "$OUTMD")"
start=$(date +%s)
"$BIN" -batchmode -nographics -fallrushQA "$RUNS" -fallrushQaRoutes -fallrushMap "$MAP" -fallrushQAOut "$OUTMD" -logFile "$ROOT/.m1-logs/g4/routes-$MAP.log" > /dev/null 2>&1 || true
echo "[g4-routes] $MAP: $(grep -o 'ROUTE COST [A-Z]*' "$OUTMD" | tail -1) in $(( $(date +%s) - start )) s → $OUTMD"
