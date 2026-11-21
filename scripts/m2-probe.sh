#!/usr/bin/env bash
# LOCAL MEASUREMENT ONLY: runs the Mac dev build as a headless scripted client (SimProbe) against a server.
# usage: scripts/m2-probe.sh <ws-url> <seconds> [run|course|idle|flood] [label] [extra player args...]
# Logs to .m1-logs/probe-<label>.log; prints the [probe] lines.
# R22 (G4.3): probe clients use a 4 s connect timeout (PROBE_CONNECT_MS) — UTP desktop WebSocket gives the HTTP upgrade one
# final ConnectTimeoutMS, and at 150 ms + 2 % loss one lost segment (1 s initial RTO) blew the 1000 ms default.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app"
BIN="$(ls "$APP/Contents/MacOS/"* 2>/dev/null | head -1 || true)"
[[ -x "$BIN" ]] || { echo "build it first: pnpm m1:build:macserver"; exit 1; }
URL="${1:-ws://127.0.0.1:7777}"; SECS="${2:-60}"; SCRIPT="${3:-run}"; LABEL="${4:-$(date +%H%M%S)}"
shift $(( $# < 4 ? $# : 4 ))
LOG="$ROOT/.m1-logs/probe-$LABEL.log"
mkdir -p "$ROOT/.m1-logs"
"$BIN" -batchmode -nographics -fallrushProbe "$URL" -fallrushProbeSecs "$SECS" -fallrushProbeScript "$SCRIPT" -fallrushConnectTimeoutMs "${PROBE_CONNECT_MS:-4000}" "$@" -logFile - > "$LOG" 2>&1 || true
grep "\[probe\]" "$LOG" || { echo "no probe output; see $LOG"; tail -20 "$LOG"; }
