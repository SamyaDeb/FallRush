#!/usr/bin/env bash
# Submission deploy (judge mode, copy fixes): the same M3 rules as p7-vps-deploy.sh. New tags fallrush-node:sub and
# fallrush-gameserver:sub; the running images are tagged :pre-sub first (node: fallrush-node:p7, the beta-audit build), the web build is kept as web.presub, the compose
# file as docker-compose.presub.yml. Rollback: `bash scripts/sub-vps-deploy.sh rollback`.
#   bash scripts/sub-vps-deploy.sh build | indexer | keepers | gs | web | rollback
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
source ~/.config/fall-rush/vps.env
DASH="${VPS_IP//./-}"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$SSH_KEY" "root@$VPS_IP")
SCP=(scp -q -o IdentitiesOnly=yes -i "$SSH_KEY")
LOG=docs/proof/sub/deploy.log; mkdir -p docs/proof/sub
note() { echo "$(date -u +%FT%TZ) $*" | sed "s/$VPS_IP/<VPS_IP>/g; s/$DASH/<VPS_IP-dashed>/g" | tee -a "$LOG"; }
busy() { curl -s --max-time 6 "https://fallrush.fun/api/status" | python3 -c "import json,sys; s=json.load(sys.stdin); print(sum(1 for x in s['servers'] if x.get('inMatch') and x.get('humans',0)>0))" 2>/dev/null || echo 0; }
wait_idle() { for _ in $(seq 1 120); do [ "$(busy)" = 0 ] && return 0; note "a human is in a match: waiting"; sleep 15; done; note "still busy after 30 min"; exit 1; }
healthy() { for _ in $(seq 1 50); do s=$("${SSH[@]}" "docker inspect -f '{{.State.Health.Status}}' $1 2>/dev/null || echo none"); [ "$s" = healthy ] && return 0; sleep 3; done; return 1; }
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
case "${1:-}" in
  build)
    # rollback tags of what runs now (the keepers / indexer run untagged image ids)
    "${SSH[@]}" 'set -e; cd /opt/fallrush
      docker image inspect fallrush-gameserver:pre-sub >/dev/null 2>&1 || docker tag fallrush-gameserver:p7ux fallrush-gameserver:pre-sub
      docker image inspect fallrush-node:pre-sub >/dev/null 2>&1 || docker tag fallrush-node:p7 fallrush-node:pre-sub
      [ -f docker-compose.presub.yml ] || cp docker-compose.yml docker-compose.presub.yml
      echo "rollback tags + compose backup ready"'
    mkdir -p "$STAGE/node/tools" "$STAGE/node/programs/target"
    for d in keeper chain-worker indexer; do rsync -a --exclude .keys --exclude '*.json.bak' --exclude node_modules "tools/$d" "$STAGE/node/tools/"; done
    cp -R programs/target/idl "$STAGE/node/programs/target/"
    cp infra/prod/node-package.json "$STAGE/node/package.json"; cp infra/prod/node.Dockerfile "$STAGE/node/Dockerfile"
    tar -C "$STAGE/node" -czf "$STAGE/node.tgz" .
    "${SCP[@]}" "$STAGE/node.tgz" "root@$VPS_IP:/opt/fallrush/build/node-sub.tgz"
    "${SSH[@]}" 'set -e; cd /opt/fallrush/build; rm -rf node-sub; mkdir node-sub; tar -C node-sub -xzf node-sub.tgz; docker build -q -t fallrush-node:sub node-sub >/dev/null && echo "image fallrush-node:sub built"'
    tar -C client-unity/Builds/Server -czf "$STAGE/gs.tgz" FallRushServer.x86_64 UnityPlayer.so libdecor-0.so.0 libdecor-cairo.so FallRushServer_Data
    cp infra/prod/gameserver.Dockerfile "$STAGE/Dockerfile.gs"
    for t in 1 2 3 4; do "${SCP[@]}" "$STAGE/gs.tgz" "$STAGE/Dockerfile.gs" "root@$VPS_IP:/opt/fallrush/build/" && break; sleep 5; done
    "${SSH[@]}" 'set -e; cd /opt/fallrush/build; rm -rf gs-sub; mkdir gs-sub; tar -C gs-sub -xzf gs.tgz; docker build -q -f Dockerfile.gs -t fallrush-gameserver:sub gs-sub >/dev/null && echo "image fallrush-gameserver:sub built"'
    (cd web/app && node scripts/assets.mjs >/dev/null && ./node_modules/.bin/vite build --logLevel warn)
    rm -rf web/app/dist/unity && mkdir -p web/app/dist/unity
    cp -R client-unity/Builds/WebGL/Build client-unity/Builds/WebGL/StreamingAssets web/app/dist/unity/
    python3 tools/p5/build-json.py web/app/dist >/dev/null; chmod -R a+rX web/app/dist
    tar -C web/app/dist -czf "$STAGE/web.tgz" .
    for t in 1 2 3 4 5 6; do "${SCP[@]}" "$STAGE/web.tgz" "root@$VPS_IP:/opt/fallrush/web-sub.tgz" && break; sleep 5; done
    "${SSH[@]}" 'set -e; cd /opt/fallrush; rm -rf web.sub; mkdir web.sub; tar -C web.sub -xzf web-sub.tgz; chmod -R a+rX web.sub; du -sh web.sub | cut -f1'
    "${SCP[@]}" infra/prod/docker-compose.yml "root@$VPS_IP:/opt/fallrush/docker-compose.sub.yml"
    note "build: fallrush-node:sub + fallrush-gameserver:sub + web.sub staged (not live); rollback tags :pre-sub*" ;;
  indexer)
    wait_idle
    "${SSH[@]}" "set -e; cd /opt/fallrush; cp docker-compose.sub.yml docker-compose.yml; docker compose up -d --no-deps fallrush-indexer 2>&1 | tail -2"
    healthy fallrush-indexer && note "indexer: up on fallrush-node:sub (healthy)" || { note "indexer: NOT healthy"; exit 1; } ;;
  keepers)
    wait_idle
    "${SSH[@]}" "set -e; cd /opt/fallrush; cp docker-compose.sub.yml docker-compose.yml; docker compose --profile gs2 up -d --no-deps fallrush-keeper-1 fallrush-keeper-2 2>&1 | tail -2"
    healthy fallrush-keeper-1 && healthy fallrush-keeper-2 && note "keepers: up on fallrush-node:sub (healthy)" || { note "keepers: NOT healthy"; exit 1; } ;;
  gs)
    wait_idle
    "${SSH[@]}" "set -e; cd /opt/fallrush; cp docker-compose.sub.yml docker-compose.yml; docker compose --profile gs2 --profile gs3 up -d --no-deps fallrush-gs1 fallrush-gs2 fallrush-gs3 2>&1 | tail -3"
    healthy fallrush-gs1 && healthy fallrush-gs2 && healthy fallrush-gs3 && note "gs1/gs2/gs3: up on fallrush-gameserver:sub (healthy)" || { note "gs: NOT healthy"; exit 1; } ;;
  web)
    wait_idle
    "${SSH[@]}" 'set -e; cd /opt/fallrush; [ -d web.presub ] || { mkdir web.presub; (cd web && tar --exclude=./site -cf - .) | tar -C web.presub -xf -; }; find web -mindepth 1 -maxdepth 1 ! -name site -exec rm -rf {} +; (cd web.sub && tar --exclude=./site -cf - .) | tar -C web -xf -; test -d web/site && echo "web/site kept"; docker exec fallrush-web nginx -t -q && docker exec fallrush-web nginx -s reload'
    note "web: switched to the submission build (web.presub kept)" ;;
  rollback)
    "${SSH[@]}" 'set -e; cd /opt/fallrush; sed -i "s#fallrush-gameserver:sub#fallrush-gameserver:pre-sub#; s#fallrush-node:sub#fallrush-node:pre-sub#" docker-compose.yml; docker compose --profile gs2 --profile gs3 up -d --no-deps fallrush-gs1 fallrush-gs2 fallrush-gs3 fallrush-keeper-1 fallrush-keeper-2 fallrush-indexer 2>&1 | tail -3; if [ -d web.presub ]; then find web -mindepth 1 -maxdepth 1 ! -name site -exec rm -rf {} +; (cd web.presub && tar --exclude=./site -cf - .) | tar -C web -xf -; docker exec fallrush-web nginx -s reload; fi'
    note "rollback: images :pre-sub*, web.presub" ;;
  *) echo "usage: $0 build|indexer|keepers|gs|web|rollback"; exit 2 ;;
esac
