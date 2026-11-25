#!/usr/bin/env bash
# dizzy2 web-only deploy (M3 rules): swaps only web/unity (the WebGL build) on the VPS; game servers, keepers, indexer,
# chain bundle and the landing site stay as they are. Backup: web.prediz2 (full copy of web/ minus site).
#   bash scripts/dz-vps-web.sh check | deploy | rollback
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
source ~/.config/fall-rush/vps.env
DASH="${VPS_IP//./-}"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$SSH_KEY" "root@$VPS_IP")
SCP=(scp -q -o IdentitiesOnly=yes -i "$SSH_KEY")
LOG=docs/proof/dizzy2/deploy.log; mkdir -p docs/proof/dizzy2
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
    tar -C "$STAGE" -czf "$STAGE/../dz-unity.tgz" unity
    "${SCP[@]}" "$STAGE/../dz-unity.tgz" "root@$VPS_IP:/opt/fallrush/web-dz.tgz"; rm -f "$STAGE/../dz-unity.tgz"
    before=$(others)
    "${SSH[@]}" 'set -e; cd /opt/fallrush
      rm -rf web.prediz2; mkdir web.prediz2; (cd web && tar --exclude=./site -cf - .) | tar -C web.prediz2 -xf -
      rm -rf web.dz; mkdir web.dz; tar -C web.dz -xzf web-dz.tgz
      rm -rf web/unity; cp -R web.dz/unity web/unity; chmod -R a+rX web/unity
      docker exec fallrush-web nginx -t -q && docker exec fallrush-web nginx -s reload; cat web/unity/Build/build.json'
    after=$(others)
    [ "$before" = "$after" ] && note "deploy: web/unity swapped (backup web.prediz2); other projects unchanged" || { note "deploy: other projects changed state!"; echo "$before"; echo "$after"; } ;;
  rollback)
    "${SSH[@]}" 'set -e; cd /opt/fallrush; test -d web.prediz2/unity; rm -rf web/unity; cp -R web.prediz2/unity web/unity; docker exec fallrush-web nginx -s reload'
    note "rollback: web/unity restored from web.prediz2" ;;
  *) echo "usage: $0 check|deploy|rollback"; exit 2 ;;
esac
