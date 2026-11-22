#!/usr/bin/env bash
# Builds rush-core-ffi (release, arm64) and installs it as Assets/Plugins/macOS/rush_core.bundle, and copies the map
# blobs into Scripts/FeelLab/Resources/feellab/<SceneKey>.bytes (scene name without "Map_").
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT/crates"
CARGO_BUILD_JOBS="${CARGO_BUILD_JOBS:-2}" nice -n 10 cargo build --offline --release -p rush-core-ffi
mkdir -p "$ROOT/client-unity/Assets/Plugins/macOS"
cp target/release/librush_core_ffi.dylib "$ROOT/client-unity/Assets/Plugins/macOS/rush_core.bundle"
install_name_tool -id @rpath/rush_core.bundle "$ROOT/client-unity/Assets/Plugins/macOS/rush_core.bundle" 2>/dev/null || true
cd "$ROOT"
RES=client-unity/Assets/_Game/Scripts/FeelLab/Resources/feellab
mkdir -p "$RES"
for j in maps/*.mapjson; do
  scene=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['scene'])" "$j")
  cp "${j%.mapjson}.mapblob" "$RES/${scene#Map_}.bytes"
done
ls -la "$RES" client-unity/Assets/Plugins/macOS/rush_core.bundle
