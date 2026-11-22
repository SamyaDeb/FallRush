#!/usr/bin/env bash
# P3-A (locked): rush-core from P2's branch, read-only. Extracts `crates maps packages/shared/config` of P2's commit
# into .p2-core/ (git archive), builds rush-core-wasm (wasm32) + rush-core-ffi (macOS) + the emulator core, installs
# them into the Unity project / tools, writes rush_core_version.txt, and runs the vector parity in Node.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p3-core -- bash scripts/p3-build-core.sh [<p2 rev>]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
P2="${P2_WORKTREE:-$ROOT/../FallRush-p2}"
REV="${1:-p2}"
SHA="$(git -C "$P2" rev-parse "$REV")"
cd "$ROOT"
rm -rf .p2-core/crates .p2-core/maps .p2-core/packages
mkdir -p .p2-core
git -C "$P2" archive "$SHA" crates maps packages/shared/config | tar -x -C .p2-core/
export CARGO_BUILD_JOBS="${CARGO_BUILD_JOBS:-2}" CARGO_TARGET_DIR="$ROOT/.p2-core/target"
N="nice -n 10"
( cd .p2-core/crates
  $N cargo build --offline --release -p rush-core-wasm --target wasm32-unknown-unknown
  $N cargo build --offline --release -p rush-core-ffi )
( cd tools/chain-emulator/core && $N cargo build --offline --release --target wasm32-unknown-unknown )
T="$CARGO_TARGET_DIR"
PLUG=client-unity/Assets/_Game/Plugins
mkdir -p "$PLUG/WebGL" client-unity/Assets/Plugins/macOS client-unity/Assets/StreamingAssets tools/chain-emulator/bin
cp "$T/wasm32-unknown-unknown/release/rush_core_wasm.wasm" client-unity/Assets/StreamingAssets/rush_core.wasm
cp "$T/release/librush_core_ffi.dylib" client-unity/Assets/Plugins/macOS/rush_core.bundle
install_name_tool -id @rpath/rush_core.bundle client-unity/Assets/Plugins/macOS/rush_core.bundle 2>/dev/null || true
cp "$T/wasm32-unknown-unknown/release/rush_core_wasm.wasm" tools/chain-emulator/bin/rush_core.wasm
cp "$T/wasm32-unknown-unknown/release/rush_emu_core.wasm" tools/chain-emulator/bin/rush_emu_core.wasm
# Map blobs (P2's maps/) for the client (Resources) and the emulator.
RES=client-unity/Assets/_Game/Scripts/FeelLab/Resources/feellab
mkdir -p "$RES" tools/chain-emulator/bin/maps
for j in .p2-core/maps/*.mapjson; do
  k="$(basename "$j" .mapjson)"
  scene=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['scene'])" "$j")
  cp ".p2-core/maps/$k.mapblob" "$RES/${scene#Map_}.bytes"
  cp ".p2-core/maps/$k.mapblob" "tools/chain-emulator/bin/maps/${scene#Map_}.mapblob"
done
# Vectors for the EditMode / browser parity.
VEC=client-unity/Assets/_Game/Tests/Vectors/rushcore
mkdir -p "$VEC" client-unity/Assets/StreamingAssets/rcvectors
cp .p2-core/crates/rush-core/tests/vectors/*.bin "$VEC/"
find client-unity/Assets/StreamingAssets/rcvectors -type f ! -name "*.meta" -delete
VER=$(node -e "const fs=require('fs');WebAssembly.instantiate(fs.readFileSync('$T/wasm32-unknown-unknown/release/rush_core_wasm.wasm')).then(r=>console.log('0x'+r.instance.exports.rc_version().toString(16)))")
cat > "$PLUG/rush_core_version.txt" <<TXT
p2_commit $SHA
p2_subject $(git -C "$P2" log -1 --format=%s "$SHA")
built_utc $(date -u +%Y-%m-%dT%H:%M:%SZ)
rc_version $VER
wasm_sha256 $(shasum -a 256 client-unity/Assets/StreamingAssets/rush_core.wasm | cut -c1-64)
wasm_bytes $(stat -f %z client-unity/Assets/StreamingAssets/rush_core.wasm)
bundle_sha256 $(shasum -a 256 client-unity/Assets/Plugins/macOS/rush_core.bundle | cut -c1-64)
TXT
cat "$PLUG/rush_core_version.txt"
node tools/p3/rc-subset.mjs client-unity/Assets/StreamingAssets/rush_core.wasm .p2-core/crates/rush-core/tests/vectors "$VEC/subset_expected.txt"
cp "$VEC"/subset_expected.txt "$VEC"/*.map client-unity/Assets/StreamingAssets/rcvectors/
node tools/p3/ffi-expect.mjs client-unity/Assets/StreamingAssets/rush_core.wasm "$RES" "$VEC/ffi_expected.txt"
