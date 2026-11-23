#!/usr/bin/env bash
# P5 health (for P6 / P7 and judging): every fallrush-* service + a public smoke check + the neighbours (finity, Ovelo).
# Read-only. Reads ~/.config/fall-rush/vps.env (VPS_IP, SSH_KEY, FINITY_URL).
#   bash scripts/p5-health.sh
set -uo pipefail
source ~/.config/fall-rush/vps.env
DASH="${VPS_IP//./-}"; PLAY="https://play.$DASH.sslip.io"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$SSH_KEY" "root@$VPS_IP")
echo "== services"; "${SSH[@]}" 'docker ps --filter name=fallrush- --format "{{.Names}}\t{{.Status}}"; docker stats --no-stream --format "{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}" | grep -E "fallrush-|ovelo"; uptime'
echo "== neighbours"; "${SSH[@]}" 'docker ps --filter name=ovelo --format "{{.Names}}\t{{.Status}}"; md5sum /etc/nginx/sites-available/finity'
[ -n "${FINITY_URL:-}" ] && echo "finity: $(curl -s -o /dev/null -w '%{http_code} %{time_total}s' "$FINITY_URL")"
echo "== public"
echo "landing  $(curl -s -o /dev/null -w '%{http_code} %{time_total}s' "$PLAY/")"
echo "webgl    $(curl -s -o /dev/null -w '%{http_code} %{size_download}B' "$PLAY/unity/Build/WebGL.loader.js")"
curl -s "$PLAY/api/status" | python3 -c "import json,sys; s=json.load(sys.stdin); [print(f\"{x['id']}: up={x.get('up')} phase={x.get('phase')} humans={x.get('humans')} keeper={x.get('keeper',{}).get('stage')} alive={x.get('keeper',{}).get('alive')}\") for x in s['servers']]"
curl -s "$PLAY/api/health"; echo
for g in gs1 gs2; do echo "$g wss: $(curl -s -o /dev/null -w '%{http_code}' -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' --max-time 5 "https://$g.$DASH.sslip.io/")"; done
