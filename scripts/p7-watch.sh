#!/usr/bin/env bash
# P7 A4: what to watch live during a playtest (read-only; nothing on the VPS changes). Every 10 s: each arena's phase,
# humans, keeper stage and Final tick from /api/status; the guest-gas funder; and the newest warnings / errors from the
# fallrush-* containers (keepers, game servers, indexer) since the last poll. Ctrl-C to stop. Redacts the VPS address.
#   bash scripts/p7-watch.sh            (or: bash scripts/p7-watch.sh 30   for a 30 s period)
set -uo pipefail
source ~/.config/fall-rush/vps.env
DASH="${VPS_IP//./-}"; PLAY="https://play.$DASH.sslip.io"; EVERY="${1:-10}"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$SSH_KEY" "root@$VPS_IP")
red() { sed "s/$VPS_IP/<VPS_IP>/g; s/$DASH/<VPS_IP-dashed>/g"; }
since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
while true; do
  now=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  echo "==== $(date -u +%T) UTC"
  curl -s --max-time 6 "$PLAY/api/status" | python3 -c '
import json,sys
s=json.load(sys.stdin)
for x in s["servers"]:
  k=x.get("keeper",{})
  print(f"  {x[\"id\"]}: {\"UP\" if x.get(\"up\") else \"DOWN\"} phase={x.get(\"phase\")} round={x.get(\"round\")} humans={x.get(\"humans\")} keeper={k.get(\"stage\")} alive={k.get(\"alive\")} final_tick={k.get(\"worldTick\") if k.get(\"stage\")==\"final\" else \"-\"}")
' 2>/dev/null || echo "  /api/status unreachable"
  curl -s --max-time 6 "$PLAY/api/health" | python3 -c 'import json,sys; h=json.load(sys.stdin); print(f"  funder {h.get(\"funderLamports\",0)/1e9:.3f} SOL · archived matches {h.get(\"matches\")}")' 2>/dev/null
  "${SSH[@]}" "for c in fallrush-keeper-1 fallrush-keeper-2 fallrush-gs1 fallrush-gs2 fallrush-indexer; do docker logs --since $since \$c 2>&1 | grep -iE 'error|warn|fail|stall|abort|exception|timeout|429' | grep -v ' 0 errors' | tail -4 | sed \"s/^/  [\$c] /\"; done" 2>/dev/null | cut -c1-220 | red
  since=$now
  sleep "$EVERY"
done
