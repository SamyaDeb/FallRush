#!/usr/bin/env bash
# fallrush deploy gate (harden #3): recreate ONE fallrush-* container only when no match is running, so a deploy never
# lands mid-race (a keeper restart freezes the World for its boot time and then fast-forwards it; a game-server restart
# abandons the match). Read-only checks until the gate opens:
#   - both keepers' /status (127.0.0.1:3111, :3113) show stage "idle" with a fresh heartbeat;
#   - both arena game servers' status.json (/opt/fallrush/logs/gs1|gs2) are fresh and show LobbyOpen with humans == 0;
#   - (fallrush-gs3) its own status.json the same way.
# Then: snapshot (docker inspect + the last log lines + the keeper's state file) into /opt/fallrush/logs/deploy/, and
# `docker compose up -d --no-deps --force-recreate <service>` (add --build with BUILD=1). Nothing else is touched.
#
#   deploy-gate.sh fallrush-keeper-2                 wait (≤ MAX_WAIT_S, default 900) for idle, snapshot, recreate
#   DRY_RUN=1 deploy-gate.sh fallrush-keeper-1       wait + report only (never recreates)
#   RESERVE=1 deploy-gate.sh fallrush-gs1            first reserve the paired arena server with a private room file
#                                                    (/opt/fallrush/rooms/<gs>.json, a random code) so no new public
#                                                    lobby fills while the current match ends; removed on exit
# Exit codes: 0 recreated (or dry run passed), 2 usage, 3 gate timed out (nothing changed), 4 recreate failed.
set -uo pipefail
SVC="${1:-}"
ROOT="${FALLRUSH_ROOT:-/opt/fallrush}"
MAX_WAIT_S="${MAX_WAIT_S:-900}"
POLL_S="${POLL_S:-5}"
STABLE_N="${STABLE_N:-2}" # consecutive passing polls before the gate opens
DRY_RUN="${DRY_RUN:-0}"
RESERVE="${RESERVE:-0}"
BUILD="${BUILD:-0}"
LOGDIR="$ROOT/logs/deploy"
case "$SVC" in
  fallrush-web|fallrush-indexer|fallrush-gs1|fallrush-gs2|fallrush-gs3|fallrush-keeper-1|fallrush-keeper-2) ;;
  *) echo "usage: $0 fallrush-{web,indexer,gs1,gs2,gs3,keeper-1,keeper-2}  (env: DRY_RUN=1 RESERVE=1 BUILD=1 MAX_WAIT_S=900)" >&2; exit 2 ;;
esac
mkdir -p "$LOGDIR"
now() { date -u +%FT%TZ; }
say() { echo "$(now) [gate $SVC] $*" | tee -a "$LOGDIR/gate.log"; }

# the arena server paired with the target (the room reservation holds it); web / indexer: none
case "$SVC" in fallrush-keeper-1|fallrush-gs1) PAIR=gs1 ;; fallrush-keeper-2|fallrush-gs2) PAIR=gs2 ;; fallrush-gs3) PAIR=gs3 ;; *) PAIR="" ;; esac
ROOMFILE=""
cleanup() { [ -n "$ROOMFILE" ] && rm -f "$ROOMFILE" && say "room reservation removed ($ROOMFILE)"; }
trap cleanup EXIT
if [ "$RESERVE" = "1" ] && [ -n "$PAIR" ]; then
  f="$ROOT/rooms/$PAIR.json"
  if [ -e "$f" ]; then say "room file $f exists (a live room): not reserving"
  else
    code="DEPLOY$(head -c 8 /dev/urandom | od -An -tx1 | tr -dc 'a-f0-9' | tr 'a-f' 'A-F' | head -c 2)"
    exp=$(( ($(date +%s) + MAX_WAIT_S + 600) * 1000 ))
    printf '{"code":"%s","expiresAt":%s,"botMix":1}\n' "$code" "$exp" > "$f.tmp" && mv -f "$f.tmp" "$f" && ROOMFILE="$f" && say "reserved $PAIR with a private room (deploy hold)"
  fi
fi

# one poll: prints "ok" or the reason it is not idle
probe() {
  python3 - "$ROOT" "$SVC" <<'PY'
import json, sys, time, urllib.request
root, svc = sys.argv[1], sys.argv[2]
now = time.time() * 1000
why = []
for name, port in (("keeper-1", 3111), ("keeper-2", 3113)):
    try:
        s = json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=5))
    except Exception as e:
        # a keeper that is down cannot be mid-match; only the target itself may be down (that is why we deploy)
        if svc != f"fallrush-{name}": why.append(f"{name} unreachable")
        continue
    if now - s.get("heartbeat", 0) > 30000: why.append(f"{name} heartbeat stale")
    if s.get("stage") != "idle": why.append(f"{name} stage={s.get('stage')}")
gss = ["gs1", "gs2"] + (["gs3"] if svc == "fallrush-gs3" else [])
for gs in gss:
    try:
        s = json.load(open(f"{root}/logs/{gs}/status.json"))
    except Exception:
        if svc != f"fallrush-{gs}": why.append(f"{gs} status unreadable")
        continue
    if now - s.get("t", 0) > 30000:
        if svc != f"fallrush-{gs}": why.append(f"{gs} status stale")
        continue
    if s.get("phase") != "LobbyOpen" or s.get("inMatch") or s.get("humans", 0) != 0:
        why.append(f"{gs} phase={s.get('phase')} humans={s.get('humans')} inMatch={s.get('inMatch')}")
print("ok" if not why else "; ".join(why))
PY
}

t0=$(date +%s); passed=0; last=""
say "waiting for idle (max ${MAX_WAIT_S}s, dry_run=$DRY_RUN)"
while :; do
  r=$(probe 2>&1)
  if [ "$r" = "ok" ]; then passed=$((passed + 1)); else passed=0; fi
  [ "$r" != "$last" ] && say "probe: $r" && last="$r"
  [ "$passed" -ge "$STABLE_N" ] && break
  if [ $(( $(date +%s) - t0 )) -ge "$MAX_WAIT_S" ]; then say "TIMEOUT after ${MAX_WAIT_S}s: nothing changed"; exit 3; fi
  sleep "$POLL_S"
done
waited=$(( $(date +%s) - t0 ))
say "gate open after ${waited}s"
if [ "$DRY_RUN" = "1" ]; then say "dry run: not recreating"; exit 0; fi

# snapshot: container config + image, the last log lines, the keeper state file (resume relies on it)
snap="$LOGDIR/$(date -u +%Y%m%dT%H%M%SZ)-$SVC"
mkdir -p "$snap"
docker inspect "$SVC" > "$snap/inspect.json" 2>/dev/null || true
docker logs --tail 300 "$SVC" > "$snap/tail.log" 2>&1 || true
case "$SVC" in
  fallrush-keeper-1) cp -f "$ROOT"/archive/keeper-devnet-arena2.json "$snap/" 2>/dev/null || true ;;
  fallrush-keeper-2) cp -f "$ROOT"/archive/keeper-devnet-arena3.json "$snap/" 2>/dev/null || true ;;
esac
say "snapshot $snap"
cd "$ROOT" || exit 4
args=(up -d --no-deps --force-recreate)
[ "$BUILD" = "1" ] && args+=(--build)
if docker compose --profile gs2 --profile gs3 "${args[@]}" "$SVC" >> "$LOGDIR/gate.log" 2>&1; then
  say "recreated (waited ${waited}s for idle)"
  exit 0
fi
say "RECREATE FAILED (see $LOGDIR/gate.log)"
exit 4
