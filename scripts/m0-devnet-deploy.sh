#!/usr/bin/env bash
# pnpm m0:devnet:deploy — build WITHOUT the `localnet` feature (VRF queue = DEFAULT_EPHEMERAL_QUEUE)
# and deploy/upgrade fall_rush on devnet. Upgrade authority = your default wallet (~/.config/solana/id.json).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT/programs"
anchor build
PROGRAM_ID=$(solana-keygen pubkey target/deploy/fall_rush-keypair.json)
grep -q "declare_id!(\"$PROGRAM_ID\")" fall_rush/src/lib.rs || { echo "declare_id! does not match $PROGRAM_ID"; exit 1; }
echo "Deploying $PROGRAM_ID to devnet (payer/upgrade authority: $(solana-keygen pubkey ~/.config/solana/id.json))"
solana program deploy --url devnet --keypair ~/.config/solana/id.json \
  --program-id target/deploy/fall_rush-keypair.json target/deploy/fall_rush.so
solana program show --url devnet "$PROGRAM_ID"
