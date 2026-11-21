#!/usr/bin/env bash
# G4.8: Unity batch WITH graphics (Editor proof shots need a GPU; scripts/m1-unity.sh always passes -nographics).
# usage: scripts/g48-unity-gfx.sh <label> <unity args...>   (refuses while any Unity runs: 8 GB machine)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.28f1/Unity.app/Contents/MacOS/Unity}"
LABEL="$1"; shift
if pgrep -x Unity >/dev/null; then echo "A Unity process is running. Wait (8 GB machine)."; exit 1; fi
mkdir -p "$ROOT/.m1-logs"
LOG="$ROOT/.m1-logs/$LABEL-$(date +%Y%m%d-%H%M%S).log"
echo "[g48] $LABEL → $LOG"
set +e
/usr/bin/time -l "$UNITY" -batchmode -projectPath "$ROOT/client-unity" -logFile "$LOG" -quit "$@" 2> "$LOG.time"
CODE=$?
set -e
grep -E "^\[setup\] (built|maphash)|error CS|Exception" "$LOG" | sort -u | head -40 || true
echo "[g48] $LABEL exit=$CODE wall=$(awk '/ real /{print $1}' "$LOG.time")s maxRSS=$(( $(awk '/maximum resident set size/{print $1}' "$LOG.time") / 1048576 ))MB"
exit $CODE
