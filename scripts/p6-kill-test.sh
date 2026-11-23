#!/usr/bin/env bash
# P6 (copy of p5-kill-test.sh; stages incl. sealed_reveal) kill tests on the VPS (fallrush-* containers only):
#   keeper <n> <stage>  restart fallrush-keeper-<n> the moment its log shows <stage> (e.g. final, settle, finalize);
#                       recovery = until it logs the match done (archived) after the restart
#   gs <n>              restart fallrush-gs<n> (between matches) and time until /api/status shows it up again and the
#                       connected test client is seated again (humans ≥ 1)
#   bash scripts/p5-kill-test.sh keeper 1 final   |   bash scripts/p5-kill-test.sh gs 1
set -uo pipefail
source ~/.config/fall-rush/vps.env
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=8 -i "$SSH_KEY" "root@$VPS_IP")
P="https://play.${VPS_IP//./-}.sslip.io"; OUT="$(dirname "$0")/../docs/proof/p6/public/kill.log"
log() { echo "$(date -u +%FT%TZ) $*" | tee -a "$OUT"; }
if [ "$1" = keeper ]; then
  N=$2; ST=$3; C=fallrush-keeper-$N
  log "keeper-$N: waiting for stage '$ST'"
  until "${SSH[@]}" "docker logs --since 20s $C 2>&1 | grep -q '\] [0-9TZ:.-]* $ST '"; do sleep 3; done
  M=$("${SSH[@]}" "docker logs --tail 200 $C 2>&1 | grep -o 'settle {\"match\":\"[1-9A-Za-z]*' | tail -1" | sed 's/.*"//')
  T0=$(date +%s); "${SSH[@]}" "docker restart $C >/dev/null"; log "keeper-$N restarted at stage '$ST'"
  until "${SSH[@]}" "docker logs --since 10m $C 2>&1 | grep -qE 'match_done|\] [0-9TZ:.-]* rewards '"; do sleep 3; [ $(( $(date +%s) - T0 )) -gt 900 ] && { log "keeper-$N: no recovery in 900 s"; exit 1; }; done
  log "keeper-$N recovered (resume from on-chain state) in $(( $(date +%s) - T0 )) s; stages after restart: $("${SSH[@]}" "docker logs --since 15m $C 2>&1 | grep '\[keeper' | tail -12 | sed 's/.*Z //' | cut -d' ' -f1 | tr '\n' ' '")"
elif [ "$1" = gs ]; then
  N=$2; C=fallrush-gs$N
  T0=$(date +%s); "${SSH[@]}" "docker restart $C >/dev/null"; log "gs$N restarted"
  until curl -s --max-time 5 "$P/api/status" | python3 -c "import json,sys; s=[x for x in json.load(sys.stdin)['servers'] if x['id']=='gs$N'][0]; sys.exit(0 if s['up'] and s['phase']=='LobbyOpen' else 1)" 2>/dev/null; do sleep 1; done
  log "gs$N back up (LobbyOpen) in $(( $(date +%s) - T0 )) s"
  until curl -s --max-time 5 "$P/api/status" | python3 -c "import json,sys; s=[x for x in json.load(sys.stdin)['servers'] if x['id']=='gs$N'][0]; sys.exit(0 if s.get('humans',0)>=1 else 1)" 2>/dev/null; do sleep 1; [ $(( $(date +%s) - T0 )) -gt 180 ] && { log "gs$N: client not back in 180 s"; exit 1; }; done
  log "gs$N: the client reconnected and is seated again $(( $(date +%s) - T0 )) s after the restart"
fi
