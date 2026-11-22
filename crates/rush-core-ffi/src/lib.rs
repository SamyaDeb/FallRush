//! rush-core for native hosts (Unity Editor tests, the Mac player's Feel Lab): the same C ABI as rush-core-wasm,
//! built as `librush_core_ffi.dylib` (copied to `Plugins/macOS/rush_core.bundle` by scripts/p1-build-ffi.sh).
#![allow(clippy::missing_safety_doc)]
include!("../../rush-core-wasm/src/abi.rs");
