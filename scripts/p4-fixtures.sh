#!/usr/bin/env bash
# P4: programs the LiteSVM economy tests load (devnet dumps, gitignored *.so): Metaplex Core, session-keys.
set -euo pipefail
D="$(cd "$(dirname "$0")/.." && pwd)/crates/rush-core-sbf-check/tests/fixtures"
mkdir -p "$D"
solana program dump -u devnet CoREENxT6tW1HoK8ypY1SxRMZTcVPm7R94rH4PZNhX7d "$D/mpl_core.so"
solana program dump -u devnet KeyspM2ssCJbqUhQ4k7sveSiY4WjnYsrXkC8oDbwde5 "$D/session_keys.so"
shasum -a 256 "$D"/*.so
