#!/usr/bin/env bash
# fallrush-watchdog (P7 A3.4): every 60 s (systemd timer fallrush-watchdog.timer) checks the public game's own services
# and restarts ONLY a fallrush-* container from the fixed list below after 3 consecutive failed checks. It never touches
# anything else on the host (finity, Ovelo, cloudflared, nginx, other containers). Log: /opt/fallrush/logs/watchdog.log
# (self-rotated at 1 MB, one old copy). State: /opt/fallrush/logs/watchdog.state (consecutive failures per service).
set -uo pipefail
LOG=/opt/fallrush/logs/watchdog.log; STATE=/opt/fallrush/logs/watchdog.state
SERVICES="fallrush-web fallrush-indexer fallrush-gs1 fallrush-gs2 fallrush-keeper-1 fallrush-keeper-2"
MAX_FAILS=3
mkdir -p /opt/fallrush/logs
[ -f "$LOG" ] && [ "$(stat -c %s "$LOG")" -gt 1048576 ] && mv -f "$LOG" "$LOG.1"
declare -A FAILS; [ -f "$STATE" ] && while read -r k v; do FAILS[$k]=$v; done < "$STATE"
now() { date -u +%FT%TZ; }
# the public status (through the web container + the indexer): one fetch, then per-service checks
STATUS=$(curl -s --max-time 8 http://127.0.0.1:3110/api/status || true)
check() { # $1 = service → exit 0 when healthy
  local s=$1 h
  h=$(docker inspect -f '{{.State.Status}}/{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$s" 2>/dev/null) || return 1
  case "$h" in running/healthy|running/none) ;; *) return 1 ;; esac
  case "$s" in
    fallrush-web) curl -sf --max-time 5 http://127.0.0.1:3110/healthz >/dev/null ;;
    fallrush-indexer) echo "$STATUS" | grep -q '"ok":true' ;;
    fallrush-gs1) echo "$STATUS" | python3 -c 'import json,sys; s=json.load(sys.stdin); sys.exit(0 if any(x["id"]=="gs1" and x.get("up") for x in s["servers"]) else 1)' 2>/dev/null ;;
    fallrush-gs2) echo "$STATUS" | python3 -c 'import json,sys; s=json.load(sys.stdin); sys.exit(0 if any(x["id"]=="gs2" and x.get("up") for x in s["servers"]) else 1)' 2>/dev/null ;;
    fallrush-keeper-1) curl -sf --max-time 5 http://127.0.0.1:3111/health | grep -q '"ok":true' ;;
    fallrush-keeper-2) curl -sf --max-time 5 http://127.0.0.1:3113/health | grep -q '"ok":true' ;;
  esac
}
line="$(now)"
for s in $SERVICES; do
  if check "$s"; then FAILS[$s]=0; line+=" $s=ok"
  else
    FAILS[$s]=$(( ${FAILS[$s]:-0} + 1 )); line+=" $s=FAIL${FAILS[$s]}"
    if [ "${FAILS[$s]}" -ge "$MAX_FAILS" ]; then
      case "$s" in fallrush-*) echo "$(now) restart $s after ${FAILS[$s]} failed checks" >> "$LOG"; docker restart "$s" >> "$LOG" 2>&1; FAILS[$s]=0 ;; esac
    fi
  fi
done
# one compact line per minute only when something failed, plus a heartbeat every 30 min
if [[ "$line" == *FAIL* ]] || [ "$(date -u +%M)" = "00" ] || [ "$(date -u +%M)" = "30" ]; then echo "$line" >> "$LOG"; fi
: > "$STATE"; for s in $SERVICES; do echo "$s ${FAILS[$s]:-0}" >> "$STATE"; done
exit 0
