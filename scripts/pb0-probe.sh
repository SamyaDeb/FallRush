#!/usr/bin/env bash
# PB0 debug: start the local stack (no rebuild) and run programs/tests/pb0-probe.ts against it.
# Run under the heavy lock: heavy-lock.sh pb0-probe -- bash scripts/pb0-probe.sh
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
M0_SKIP_BUILD=1 M0_STACK_ONLY=1 bash "$ROOT/scripts/m0-test-local.sh" > "$ROOT/.m0-logs/pb0-stack.out" 2>&1 &
SP=$!
for i in $(seq 1 240); do grep -q "oracles running" "$ROOT/.m0-logs/pb0-stack.out" && break; kill -0 $SP 2>/dev/null || break; sleep 1; done
cat "$ROOT/.m0-logs/pb0-stack.out"
(. "$ROOT/scripts/local-env.sh"; cd "$ROOT/programs" && NODE_OPTIONS=--no-experimental-strip-types npx tsx tests/pb0-probe.ts) 2>&1
kill -TERM $SP 2>/dev/null; wait $SP 2>/dev/null
echo "probe done"
