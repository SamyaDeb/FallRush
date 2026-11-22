#!/usr/bin/env bash
# heavy-lock.sh — one heavy job at a time on the 8 GB M2 Air, across sessions and worktrees.
#
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh <label> -- <command> [args...]
#
# Waits until (1) no Unity / QA / soak / browser-measure job is running and (2) no other session
# holds the lock, then takes the lock, runs the command, and always releases the lock.
# "Heavy" = anchor/cargo build of Solana programs, the local MagicBlock stack, load-test clients,
# Chrome measurement, Docker builds. Small `cargo test` of a no_std crate may run without it.
#
#   heavy-lock.sh --status    shows who holds the lock and what is blocking
set -uo pipefail

LOCK=/tmp/fallrush-heavy.lock
BLOCK_RE='Unity.app/Contents/MacOS/Unity|m4-qa.sh|m4-soak.sh|g47-webgl-measure|g47-qa-all|mb-stack|ephemeral-validator|solana-test-validator'

blockers() { pgrep -fl "$BLOCK_RE" 2>/dev/null | grep -v heavy-lock || true; }

holder_alive() {
  [ -f "$LOCK/pid" ] || return 1
  kill -0 "$(cat "$LOCK/pid")" 2>/dev/null
}

if [ "${1:-}" = "--status" ]; then
  if [ -d "$LOCK" ]; then echo "lock held by: $(cat "$LOCK/label" 2>/dev/null) pid $(cat "$LOCK/pid" 2>/dev/null) since $(cat "$LOCK/since" 2>/dev/null)"; else echo "lock free"; fi
  echo "blockers:"; blockers; echo "load: $(sysctl -n vm.loadavg)"
  exit 0
fi

LABEL="${1:?label required}"; shift
[ "${1:-}" = "--" ] && shift
[ $# -gt 0 ] || { echo "usage: heavy-lock.sh <label> -- <command...>"; exit 2; }

waited=0
while :; do
  if [ -d "$LOCK" ] && ! holder_alive; then
    echo "heavy-lock: removing stale lock ($(cat "$LOCK/label" 2>/dev/null))"; rm -rf "$LOCK"
  fi
  b="$(blockers)"
  if [ -z "$b" ] && mkdir "$LOCK" 2>/dev/null; then
    echo $$ > "$LOCK/pid"; echo "$LABEL" > "$LOCK/label"; date -u +%FT%TZ > "$LOCK/since"
    break
  fi
  if [ $((waited % 300)) -eq 0 ]; then
    echo "heavy-lock: waiting ($LABEL) — holder: $(cat "$LOCK/label" 2>/dev/null || echo none); blockers: ${b:-none}"
  fi
  sleep 15; waited=$((waited + 15))
done

trap 'rm -rf "$LOCK"' EXIT INT TERM
echo "heavy-lock: acquired ($LABEL) after ${waited}s"
"$@"
