#!/usr/bin/env bash
# LOCAL MEASUREMENT (M3b gate): 3 headless race clients (SimProbe script "race": the waypoint bot on the predicted state)
# against a server at <url>; logs .m1-logs/probe-<label>-<n>.log; prints the summary.
# usage: scripts/m3-gate.sh <ws-url> <secs> <label> [extra probe args...]
#   e.g. local:  scripts/m1-macserver.sh start -fallrushMap BlockPartyDash && scripts/m3-gate.sh ws://127.0.0.1:7777 150 m3-local
#   M4: -fallrushMap BlockPartyDash is a one-round dev match (lobby → deal → the race, which crowns its winner → lobby); for full matches use scripts/m4-match-run.sh
#        netem:  scripts/m2-netem.sh start 75 15 2 && scripts/m3-gate.sh ws://127.0.0.1:7778 150 m3-netem
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
URL="${1:?url}"; SECS="${2:-150}"; LABEL="${3:-m3}"
shift $(( $# < 3 ? $# : 3 ))
for n in 1 2 3; do
  bash "$ROOT/scripts/m2-probe.sh" "$URL" "$SECS" race "$LABEL-$n" "$@" > /dev/null &
  sleep 1
done
wait
cd "$ROOT" && python3 scripts/m2-summarize.py "$LABEL-1" "$LABEL-2" "$LABEL-3"
for n in 1 2 3; do grep -c "hazardCorr" ".m1-logs/probe-$LABEL-$n.log" >/dev/null || true; done
