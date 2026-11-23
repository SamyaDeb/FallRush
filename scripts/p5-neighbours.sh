#!/usr/bin/env bash
# P5 §1.3.7: read-only neighbour check (finity site + Ovelo containers + raw-IP behaviour), redacted output.
#   bash scripts/p5-neighbours.sh <label>  → docs/proof/p5/vps/neighbours.log (appends)
source ~/.config/fall-rush/vps.env
L="${1:-check}"; OUT="$(dirname "$0")/../docs/proof/p5/vps/neighbours.log"
F="${FINITY_URL:?set FINITY_URL in ~/.config/fall-rush/vps.env}"
r() { curl -s -o /dev/null -w '%{http_code} %{time_total}s' --max-time 10 "$@"; }
{
  echo "== $(date -u +%FT%TZ) $L"
  echo "finity https $(r $F/)  http $(r ${F/https:/http:}/)"
  echo "raw-ip http $(r http://$VPS_IP/)  https $(r -k https://$VPS_IP/)"
  ssh -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$SSH_KEY" "root@$VPS_IP" 'echo "finity conf md5 $(md5sum /etc/nginx/sites-available/finity | cut -c1-32)"; docker ps --filter name=ovelo --format "{{.Names}} {{.Status}}"; echo "sites-enabled: $(ls /etc/nginx/sites-enabled | tr "\n" " ")"; uptime'
} 2>&1 | sed "s/$VPS_IP/<VPS_IP>/g; s/${VPS_IP//./-}/<VPS_IP-dashed>/g" | tee -a "$OUT"
