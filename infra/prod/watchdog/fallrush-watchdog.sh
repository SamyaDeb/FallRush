#!/usr/bin/env bash
# fallrush-watchdog (P7 A3.4): every 60 s (systemd timer fallrush-watchdog.timer) checks the public game's own services
# and restarts ONLY a fallrush-* container from the fixed list below after 3 consecutive failed checks. It never touches
# anything else on the host (finity, Ovelo, cloudflared, nginx, other containers). Log: /opt/fallrush/logs/watchdog.log
# (self-rotated at 1 MB, one old copy). State: /opt/fallrush/logs/watchdog.state (consecutive failures per service).
set -uo pipefail
LOG=/opt/fallrush/logs/watchdog.log; STATE=/opt/fallrush/logs/watchdog.state
SERVICES="fallrush-web fallrush-indexer fallrush-gs1 fallrush-gs2 fallrush-gs3 fallrush-keeper-1 fallrush-keeper-2"
MAX_FAILS=3
# harden #3: a keeper mid-match (stage onchain_match, heartbeat < 60 s: the process is alive, only a stage limit or a
# slow /health tripped) gets 5 failed checks (~5 min) before a restart: a restart mid-race freezes the World for the
# keeper's boot time and then fast-forwards it (shQHm8: 2-4x)
KEEPER_MIDMATCH_MAX_FAILS=5
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
    # the game servers write their own heartbeat (status.json, every few s): independent of the indexer (P7 fix: with
    # the indexer down, /api/status failed and the old check restarted both game servers mid-match)
    fallrush-gs1|fallrush-gs2|fallrush-gs3) python3 -c 'import json,sys,time; s=json.load(open(sys.argv[1])); sys.exit(0 if time.time()*1000 - s.get("t",0) < 30000 else 1)' "/opt/fallrush/logs/${s#fallrush-}/status.json" 2>/dev/null ;;
    fallrush-keeper-1) curl -sf --max-time 5 http://127.0.0.1:3111/health | grep -q '"ok":true' ;;
    fallrush-keeper-2) curl -sf --max-time 5 http://127.0.0.1:3113/health | grep -q '"ok":true' ;;
  esac
}
# the failure budget of one service: 5 for a keeper that is alive inside an on-chain match, else MAX_FAILS
max_fails() { # $1 = service
  local port
  case "$1" in fallrush-keeper-1) port=3111 ;; fallrush-keeper-2) port=3113 ;; *) echo "$MAX_FAILS"; return ;; esac
  curl -s --max-time 5 "http://127.0.0.1:$port/status" 2>/dev/null | python3 -c 'import json,sys,time
try:
  s=json.load(sys.stdin); live=s.get("stage")=="onchain_match" and time.time()*1000-s.get("heartbeat",0)<60000
except Exception: live=False
print(sys.argv[1] if live else sys.argv[2])' "$KEEPER_MIDMATCH_MAX_FAILS" "$MAX_FAILS" 2>/dev/null || echo "$MAX_FAILS"
}
line="$(now)"
for s in $SERVICES; do
  if check "$s"; then FAILS[$s]=0; line+=" $s=ok"
  else
    FAILS[$s]=$(( ${FAILS[$s]:-0} + 1 )); line+=" $s=FAIL${FAILS[$s]}"
    lim=$MAX_FAILS; [ "${FAILS[$s]}" -ge "$MAX_FAILS" ] && lim=$(max_fails "$s")
    if [ "${FAILS[$s]}" -ge "$lim" ]; then
      case "$s" in fallrush-*) echo "$(now) restart $s after ${FAILS[$s]} failed checks" >> "$LOG"; docker restart "$s" >> "$LOG" 2>&1; FAILS[$s]=0 ;; esac
    fi
  fi
done
# harden #1: the wallets the game spends from (indexer /api/health: authority, treasury, guest funder): a low balance
# is a FAIL line in the log, never a restart (a top-up is an ops action)
LOWB=$(curl -s --max-time 8 http://127.0.0.1:3110/api/health 2>/dev/null | python3 -c 'import json,sys
try: h=json.load(sys.stdin)
except Exception: sys.exit(0)
low=h.get("low") or {}
xs=["%s:%s" % (k, h.get(k+"Lamports")) for k in ("authority","treasury","funder") if low.get(k)]
print(",".join(xs))' 2>/dev/null || true)
[ -n "$LOWB" ] && line+=" balances=FAIL(low $LOWB)"
# one compact line per minute only when something failed, plus a heartbeat every 30 min
if [[ "$line" == *FAIL* ]] || [ "$(date -u +%M)" = "00" ] || [ "$(date -u +%M)" = "30" ]; then echo "$line" >> "$LOG"; fi
: > "$STATE"; for s in $SERVICES; do echo "$s ${FAILS[$s]:-0}" >> "$STATE"; done
exit 0
