#!/usr/bin/env bash
# P7 (copy of p6-kill-test.sh; + gs mid-round, indexer down) kill tests on the VPS (fallrush-* containers only):
#   keeper <n> <stage>  restart fallrush-keeper-<n> the moment its log shows <stage> (e.g. final, settle, finalize);
#                       recovery = until it logs the match done (archived) after the restart
#   gs <n>              restart fallrush-gs<n> (between matches) and time until /api/status shows it up again and the
#                       connected test client is seated again (humans ≥ 1)
#   bash scripts/p5-kill-test.sh keeper 1 final   |   bash scripts/p5-kill-test.sh gs 1
set -uo pipefail
source ~/.config/fall-rush/vps.env
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=8 -i "$SSH_KEY" "root@$VPS_IP")
P="https://play.${VPS_IP//./-}.sslip.io"; mkdir -p "$(dirname "$0")/../docs/proof/p7/chaos"; OUT="$(dirname "$0")/../docs/proof/p7/chaos/kill.log"
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
elif [ "$1" = gsround ]; then
  # P7: restart fallrush-gs<n> while its match is in round <r> (default 2): what do the humans see, does the match
  # abort cleanly on-chain (markets refund), how long until the lobby is back
  N=$2; R=${3:-2}; C=fallrush-gs$N
  log "gs$N: waiting for round $R"
  until curl -s --max-time 5 "$P/api/status" | python3 -c "import json,sys; s=[x for x in json.load(sys.stdin)['servers'] if x['id']=='gs$N'][0]; sys.exit(0 if s.get('inMatch') and s.get('round')==$R else 1)" 2>/dev/null; do sleep 2; done
  M=$(curl -s "$P/api/status" | python3 -c "import json,sys; print([x for x in json.load(sys.stdin)['servers'] if x['id']=='gs$N'][0]['keeper'].get('match',''))")
  T0=$(date +%s); "${SSH[@]}" "docker restart $C >/dev/null"; log "gs$N restarted mid-round $R (on-chain match $M)"
  until curl -s --max-time 5 "$P/api/status" | python3 -c "import json,sys; s=[x for x in json.load(sys.stdin)['servers'] if x['id']=='gs$N'][0]; sys.exit(0 if s['up'] and s['phase']=='LobbyOpen' else 1)" 2>/dev/null; do sleep 1; done
  log "gs$N back up (LobbyOpen) in $(( $(date +%s) - T0 )) s"
  K=keeper-$([ "$N" = 1 ] && echo 1 || echo 2)
  for _ in $(seq 1 120); do "${SSH[@]}" "docker logs --since 10m fallrush-$K 2>&1 | grep -qE 'abort|match_done|classic_abort'" && break; sleep 5; done
  log "keeper after the gs restart: $("${SSH[@]}" "docker logs --since 10m fallrush-$K 2>&1 | grep '\[keeper' | tail -8 | sed 's/.*Z //' | cut -c1-80 | tr '\n' '|'")"
  echo "$M" > "$(dirname "$OUT")/gsround-match.txt"
elif [ "$1" = indexer ]; then
  # P7: stop the indexer (the game keeps playing; the web pages degrade); the watchdog must restart it after 3 checks
  T0=$(date +%s); "${SSH[@]}" "docker stop fallrush-indexer >/dev/null"; log "indexer stopped"
  curl -s -o /dev/null -w "during: /api/status %{http_code}\n" --max-time 8 "$P/api/status" | tee -a "$OUT"
  curl -s -o /dev/null -w "during: /arenas %{http_code}\n" --max-time 8 "$P/arenas" | tee -a "$OUT"
  until "${SSH[@]}" "docker inspect -f '{{.State.Health.Status}}' fallrush-indexer 2>/dev/null | grep -q healthy"; do sleep 5; [ $(( $(date +%s) - T0 )) -gt 600 ] && { log "indexer: not back in 600 s"; exit 1; }; done
  log "indexer back (healthy) $(( $(date +%s) - T0 )) s after the stop; watchdog: $("${SSH[@]}" "grep restart /opt/fallrush/logs/watchdog.log | tail -1")"
fi
