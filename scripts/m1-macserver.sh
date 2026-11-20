#!/usr/bin/env bash
# LOCAL TEST ONLY: run the native macOS dev server (Build.MacDevServer) headless on 127.0.0.1.
# usage: scripts/m1-macserver.sh start [extra player args, e.g. -fallrushBots 12 -fallrushSnapHz 30 -fallrushServerFps 60]|stop|status
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app"
BIN="$(ls "$APP/Contents/MacOS/"* 2>/dev/null | head -1 || true)"
PIDF="$ROOT/.m1-logs/macserver.pid"
LOG="$ROOT/.m1-logs/macserver.log"
mkdir -p "$ROOT/.m1-logs"
case "${1:-}" in
  start)
    [[ -x "$BIN" ]] || { echo "build it first: pnpm m1:build:macserver"; exit 1; }
    if [[ -f "$PIDF" ]] && kill -0 "$(cat "$PIDF")" 2>/dev/null; then echo "[m1] mac server already running pid $(cat "$PIDF")"; exit 0; fi
    GAMESERVER_PORT="${GAMESERVER_PORT:-7777}" GAMESERVER_ID="${GAMESERVER_ID:-gs-mac-dev}" \
      nohup "$BIN" -batchmode -nographics -fallrushServer -fallrushListen 127.0.0.1 -logFile - "${@:2}" >> "$LOG" 2>&1 &
    echo $! > "$PIDF"
    for _ in $(seq 1 30); do grep -q "\[gs\] listening" "$LOG" 2>/dev/null && break; sleep 1; done
    grep "\[gs\] listening" "$LOG" | tail -1 || { echo "[m1] server did not report listening; see $LOG"; exit 1; }
    ;;
  stop)
    if [[ -f "$PIDF" ]] && kill -0 "$(cat "$PIDF")" 2>/dev/null; then kill "$(cat "$PIDF")"; echo "[m1] stopped mac server"; fi
    rm -f "$PIDF"
    ;;
  status)
    if [[ -f "$PIDF" ]] && kill -0 "$(cat "$PIDF")" 2>/dev/null; then echo "running pid $(cat "$PIDF")"; else echo "stopped"; fi
    ;;
  *) echo "usage: $0 start|stop|status"; exit 2;;
esac
