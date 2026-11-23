#!/usr/bin/env bash
# P6 (copy of p5-devnet-upgrade.sh): upgrade fall_rush (4yehff…, same program id) on devnet with programs/target/deploy/fall_rush.so (built WITHOUT the
# localnet feature). The upgrade buffer needs the whole .so's rent (~6,960 lamports per byte) at once: SOL is moved from
# the game authority to the admin (upgrade authority) for the deploy and moved back after (the buffer rent is refunded).
# Extends the program data first when the .so is bigger (permanent rent). Logs every balance into docs/proof/p6/sol.md.
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p4-upgrade -- bash scripts/p4-devnet-upgrade.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
PID=4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4
SO=programs/target/deploy/fall_rush.so
KP=programs/target/deploy/fall_rush-keypair.json
AUTH=~/.config/fall-rush/devnet-game-authority.json
LOG=docs/proof/p6/sol.md
SZ=$(stat -f %z "$SO")
CUR=$(solana program show -u devnet "$PID" | awk '/Data Length/ {print $3}')
admin() { solana balance -u devnet --lamports -k ~/.config/solana/id.json | awk '{print $1}'; }
authb() { solana balance -u devnet --lamports -k "$AUTH" | awk '{print $1}'; }
note() { echo "| $(date -u +%FT%TZ) | $1 | admin $(solana balance -u devnet -k ~/.config/solana/id.json) | authority $(solana balance -u devnet -k $AUTH) |" | tee -a "$LOG"; }
note "before upgrade: .so $SZ B, program data $CUR B"
if [ "$SZ" -gt "$CUR" ]; then
  ADD=$(( SZ - CUR + 4096 ))
  solana program extend -u devnet "$PID" "$ADD"
  note "extended program data by $ADD B"
fi
# P6: the real buffer rent (`solana rent`), + 0.12 SOL for fees (the P5 6,960 lamports/byte estimate was ~37 % high)
RENT=$(solana rent -u devnet --lamports $(( SZ + 45 )) | awk '{print $3}')
NEED=$(( RENT + 120000000 ))
A=$(admin)
MOVED=0
if [ "$A" -lt "$NEED" ]; then
  MOVED=$(( NEED - A ))
  solana transfer -u devnet --from "$AUTH" --fee-payer "$AUTH" ~/.config/solana/id.json "$(python3 -c "print($MOVED/1e9)")" --allow-unfunded-recipient
  note "moved $MOVED lamports authority → admin for the buffer"
fi
solana program deploy -u devnet --program-id "$KP" "$SO" --with-compute-unit-price 10000 --max-sign-attempts 60 2>&1 | tail -3
note "deployed"
if [ "$MOVED" -gt 0 ]; then
  solana transfer -u devnet --fee-payer ~/.config/solana/id.json "$(solana-keygen pubkey $AUTH)" "$(python3 -c "print($MOVED/1e9)")" --allow-unfunded-recipient
  note "moved $MOVED lamports back admin → authority"
fi
solana program show -u devnet "$PID" | tee -a "$LOG"
solana program show --buffers -u devnet | tee -a "$LOG"
