#!/usr/bin/env bash
# Runs Unity 6000.3.28f1 in batch mode with the Editor closed, logs to .m1-logs/, captures peak RSS.
# usage: scripts/m1-unity.sh <label> [--tests] <unity args...>
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.28f1/Unity.app/Contents/MacOS/Unity}"
LABEL="$1"; shift
MODE=build
if [[ "${1:-}" == "--tests" || "${1:-}" == "--no-quit" ]]; then MODE=noquit; shift; fi

[[ -x "$UNITY" ]] || { echo "Unity not found at $UNITY (set UNITY_EDITOR)"; exit 1; }
if pgrep -x Unity >/dev/null; then   # process name, not command line (a shell loop mentioning the path matched -f)
  echo "A Unity Editor process is running. Close it first (8 GB machine: never Editor + batch build at once)."; exit 1
fi

mkdir -p "$ROOT/.m1-logs"
STAMP="$(date +%Y%m%d-%H%M%S)"
LOG="$ROOT/.m1-logs/$LABEL-$STAMP.log"
TIMEF="$ROOT/.m1-logs/$LABEL-$STAMP.time"
SWAP_BEFORE="$(sysctl -n vm.swapusage)"

ARGS=(-batchmode -nographics -projectPath "$ROOT/client-unity" -logFile "$LOG")
# -quit must not be combined with -runTests (Unity docs: it quits before tests finish) or with methods that exit from a callback.
[[ "$MODE" == build ]] && ARGS+=(-quit)

echo "[m1] $LABEL → $LOG"
set +e
/usr/bin/time -l "$UNITY" "${ARGS[@]}" "$@" 2> "$TIMEF"
CODE=$?
set -e
SWAP_AFTER="$(sysctl -n vm.swapusage)"

grep -E "^\[build\]|\[setup\]|error CS" "$LOG" | sort -u || true
REAL=$(awk '/ real /{print $1}' "$TIMEF")
RSS=$(awk '/maximum resident set size/{print $1}' "$TIMEF")
echo "[m1] $LABEL exit=$CODE wall=${REAL}s maxRSS=$(( ${RSS:-0} / 1048576 ))MB"
echo "[m1] swap before: $SWAP_BEFORE"
echo "[m1] swap after:  $SWAP_AFTER"
{ echo "exit=$CODE"; echo "swap_before=$SWAP_BEFORE"; echo "swap_after=$SWAP_AFTER"; } >> "$TIMEF"
exit $CODE
