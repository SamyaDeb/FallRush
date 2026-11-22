#!/usr/bin/env bash
# P0: deploy the p0_tick spike program to devnet (run inside heavy-lock). Prints balance before/after.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SP="$ROOT/spikes/p0"
SO="$SP/target/deploy/p0_tick.so"
KP="$SP/target/deploy/p0_tick-keypair.json"
B0=$(solana balance -u devnet | awk '{print $1}')
NEED=$(solana rent "$(( $(stat -f %z "$SO") + 45 ))" | awk '{print $3}')
echo "balance $B0 SOL; program rent ~$NEED SOL; .so $(stat -f %z "$SO") B"
awk -v b="$B0" -v n="$NEED" 'BEGIN{ if (b < n + 0.8) { print "balance too low: need program rent + ~0.8 SOL for accounts"; exit 1 } }'
solana program deploy -u devnet --program-id "$KP" "$SO" --max-len "$(stat -f %z "$SO")" --with-compute-unit-price 10000
B1=$(solana balance -u devnet | awk '{print $1}')
echo "deployed; balance $B0 -> $B1 SOL"
solana program show -u devnet "$(solana-keygen pubkey "$KP")"
