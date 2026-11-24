#!/usr/bin/env bash
# P7 (copy of p6-builds.sh, locked): fresh builds from this branch — EditMode tests, the Linux server (VPS image), the Mac dev server
# (local hybrid checks; P4 §8.2: a stale server build played its own Final), the WebGL release (≤ 25 MB).
# Disk: ≥ 5 GB free before each build (prompt §1.1). Writes docs/proof/p7/builds.txt.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p7-builds -- bash scripts/p7-builds.sh [tests server mac webgl]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
OUT=docs/proof/p7/builds.txt; mkdir -p docs/proof/p7 .m1-logs
STEPS="${*:-tests server mac webgl}"
free_gb() { df -g / | awk 'NR==2 {print $4}'; }
for s in $STEPS; do
  F=$(free_gb); if [ "$F" -lt 5 ]; then echo "$(date -u +%FT%TZ) $s SKIPPED: only ${F} GB free" | tee -a "$OUT"; continue; fi
  T0=$(date +%s)
  case $s in
    tests) bash scripts/m1-unity.sh p7-editmode --tests -runTests -testPlatform EditMode -testResults "$ROOT/.m1-logs/p7-editmode.xml"; R=$?
       S=$(python3 -c "import re;t=open('.m1-logs/p7-editmode.xml').read();m=re.search(r'<test-run[^>]*total=\"(\d+)\"[^>]*passed=\"(\d+)\"[^>]*failed=\"(\d+)\"',t);print(m.groups() if m else 'no result')" 2>/dev/null)
       echo "$(date -u +%FT%TZ) editmode exit $R (total, passed, failed) $S $(( $(date +%s) - T0 )) s" | tee -a "$OUT" ;;
    server) bash scripts/m1-unity.sh p7-server -buildTarget Linux64 -executeMethod Build.LinuxDevServer; R=$?
       echo "$(date -u +%FT%TZ) linux server exit $R $(( $(date +%s) - T0 )) s $(du -sk client-unity/Builds/Server | cut -f1) KB" | tee -a "$OUT" ;;
    mac) bash scripts/m1-unity.sh p7-macserver -buildTarget OSXUniversal -executeMethod Build.MacDevServer; R=$?
       echo "$(date -u +%FT%TZ) mac dev server exit $R $(( $(date +%s) - T0 )) s" | tee -a "$OUT" ;;
    webgl) bash scripts/m1-unity.sh p7-webgl -buildTarget WebGL -executeMethod Build.WebGL -fallrushRelease; R=$?
       echo "$(date -u +%FT%TZ) webgl release exit $R $(( $(date +%s) - T0 )) s $(find client-unity/Builds/WebGL -type f -exec stat -f %z {} + | awk '{s+=$1} END {print s}') B" | tee -a "$OUT" ;;
  esac
done
# P7: Unity batch builds rewrite two ProjectSettings keys (m_EnableMultiplayerRoles, webGLExceptionSupport): revert them
git -C "$ROOT" checkout -- client-unity/ProjectSettings/ProjectSettings.asset 2>/dev/null || true
git -C "$ROOT" status --short client-unity/ProjectSettings | head -5
