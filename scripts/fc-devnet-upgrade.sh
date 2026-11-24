#!/usr/bin/env bash
# fullchain (copy of p6-devnet-upgrade.sh): upgrade fall_rush (4yehff…, SAME program id — deployed by address, no
# program keypair, so a new program id is impossible) on devnet with programs/target/deploy/fall_rush.so (built WITHOUT
# the localnet feature). The upgrade buffer needs the whole .so's rent at once: SOL is moved from the game authority to
# the upgrade authority for the deploy and moved back after (the buffer rent is refunded). Every balance → sol.md.
# Rollback: the live P6 .so is kept at ~/.config/fall-rush/rollback/fall_rush-live-p6-f7fcc5f0.so (SO=<that> to redeploy).
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh fc-upgrade -- bash scripts/fc-devnet-upgrade.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
PID=4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4
SO=${SO:-programs/target/deploy/fall_rush.so}
AUTH=~/.config/fall-rush/devnet-game-authority.json
LOG=docs/proof/fullchain/sol.md
[ -f "$LOG" ] || printf '# fullchain devnet SOL log\n\n| when (UTC) | what | admin (upgrade authority) | game authority |\n|---|---|---|---|\n' > "$LOG"
SZ=$(stat -f %z "$SO")
CUR=$(solana program show -u devnet "$PID" | awk '/Data Length/ {print $3}')
admin() { solana balance -u devnet --lamports -k ~/.config/solana/id.json | awk '{print $1}'; }
authb() { solana balance -u devnet --lamports -k "$AUTH" | awk '{print $1}'; }
note() { echo "| $(date -u +%FT%TZ) | $1 | $(solana balance -u devnet -k ~/.config/solana/id.json) | $(solana balance -u devnet -k $AUTH) |" | tee -a "$LOG"; }
note "before upgrade: .so $SZ B ($(shasum -a 256 "$SO" | cut -c1-16)), program data $CUR B"
if [ "$SZ" -gt "$CUR" ]; then
  ADD=$(( SZ - CUR + 4096 ))
  solana program extend -u devnet "$PID" "$ADD"
  note "extended program data by $ADD B"
fi
RENT=$(solana rent -u devnet --lamports $(( SZ + 45 )) | awk '{print $3}')
NEED=$(( RENT + 120000000 ))
A=$(admin); G=$(authb)
KEEP=300000000   # the game authority keeps 0.3 SOL for matches
if [ $(( A + G - KEEP )) -lt "$NEED" ]; then
  note "NOT ENOUGH SOL: need $NEED lamports in the upgrade authority (have $A + movable $(( G - KEEP )))"
  exit 3
fi
MOVED=0
if [ "$A" -lt "$NEED" ]; then
  MOVED=$(( NEED - A ))
  solana transfer -u devnet --from "$AUTH" --fee-payer "$AUTH" ~/.config/solana/id.json "$(python3 -c "print($MOVED/1e9)")" --allow-unfunded-recipient
  note "moved $MOVED lamports game authority → upgrade authority for the buffer"
fi
solana program deploy -u devnet --program-id "$PID" "$SO" --upgrade-authority ~/.config/solana/id.json --with-compute-unit-price 10000 --max-sign-attempts 60 2>&1 | tail -3
note "deployed"
if [ "$MOVED" -gt 0 ]; then
  solana transfer -u devnet --fee-payer ~/.config/solana/id.json "$(solana-keygen pubkey $AUTH)" "$(python3 -c "print($MOVED/1e9)")" --allow-unfunded-recipient
  note "moved $MOVED lamports back"
fi
solana program show -u devnet "$PID" | tee -a "$LOG"
solana program show --buffers -u devnet | tee -a "$LOG"
