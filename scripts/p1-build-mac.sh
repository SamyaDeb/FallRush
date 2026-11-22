#!/usr/bin/env bash
# P1-N (locked): rush_core.bundle + blobs, then the Mac development client with the Feel Lab.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p1-mac -- bash scripts/p1-build-mac.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
bash scripts/p1-build-ffi.sh
START=$(date +%s)
bash scripts/m1-unity.sh p1-mac -buildTarget OSXUniversal -executeMethod Build.MacClient
echo "[p1] Mac client build $(( $(date +%s) - START )) s, size $(du -sh client-unity/Builds/Mac-p1-feellab/FallRush.app | cut -f1)"
