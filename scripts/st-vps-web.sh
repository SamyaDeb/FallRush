#!/usr/bin/env bash
# settings-fix web-only deploy (2026-11-27) (M3 rules): swaps only web/unity (the WebGL build) on the VPS; game servers, keepers, indexer,
# chain bundle and the landing site stay as they are. Backup: web.presettings (full copy of web/ minus site).
#   bash scripts/st-vps-web.sh check | deploy | rollback
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
source ~/.config/fall-rush/vps.env
DASH="${VPS_IP//./-}"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$SSH_KEY" "root@$VPS_IP")
SCP=(scp -q -o IdentitiesOnly=yes -i "$SSH_KEY")
LOG=docs/proof/settings-fix/deploy.log; mkdir -p docs/proof/settings-fix
note() { echo "$(date -u +%FT%TZ) $*" | sed "s/$VPS_IP/<VPS_IP>/g; s/$DASH/<VPS_IP-dashed>/g" | tee -a "$LOG"; }
busy() { curl -s --max-time 6 "https://fallrush.fun/api/status" | python3 -c "import json,sys; s=json.load(sys.stdin); print(sum(1 for x in s['servers'] if x.get('inMatch') and x.get('humans',0)>0))" 2>/dev/null || echo 0; }
others() { "${SSH[@]}" 'docker ps --format "{{.Names}} {{.Status}}" | grep -v -i fallrush | sort; systemctl is-active nginx' ; }
case "${1:-}" in
  check)
    others; note "check: humans in match = $(busy)" ;;
  deploy)
    for _ in $(seq 1 40); do [ "$(busy)" = 0 ] && break; note "a human is in a match: waiting"; sleep 15; done
    B=client-unity/Builds/WebGL
    STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
    mkdir -p "$STAGE/unity"; cp -R "$B/Build" "$B/StreamingAssets" "$STAGE/unity/"
    python3 tools/p5/build-json.py "$STAGE" >/dev/null; chmod -R a+rX "$STAGE"
    tar -C "$STAGE" -czf "$STAGE/../st-unity.tgz" unity
    "${SCP[@]}" "$STAGE/../st-unity.tgz" "root@$VPS_IP:/opt/fallrush/web-st.tgz"; rm -f "$STAGE/../st-unity.tgz"
    before=$(others)
    "${SSH[@]}" 'set -e; cd /opt/fallrush
      rm -rf web.presettings; mkdir web.presettings; (cd web && tar --exclude=./site -cf - .) | tar -C web.presettings -xf -
      rm -rf web.st; mkdir web.st; tar -C web.st -xzf web-st.tgz
      rm -rf web/unity; cp -R web.st/unity web/unity; chmod -R a+rX web/unity
      docker exec fallrush-web nginx -t -q && docker exec fallrush-web nginx -s reload; cat web/unity/Build/build.json'
    after=$(others)
    [ "$before" = "$after" ] && note "deploy: web/unity swapped (backup web.presettings); other projects unchanged" || { note "deploy: other projects changed state!"; echo "$before"; echo "$after"; } ;;
  rollback)
    "${SSH[@]}" 'set -e; cd /opt/fallrush; test -d web.presettings/unity; rm -rf web/unity; cp -R web.presettings/unity web/unity; docker exec fallrush-web nginx -s reload'
    note "rollback: web/unity restored from web.presettings" ;;
  *) echo "usage: $0 check|deploy|rollback"; exit 2 ;;
esac
