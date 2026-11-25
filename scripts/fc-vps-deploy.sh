#!/usr/bin/env bash
# fullchain deploy (all 4 rounds on-chain behind ONCHAIN_ALL / GAMESERVER_ONCHAIN_ALL): copy of sub-vps-deploy.sh, same M3 rules.
# New tags fallrush-node:fc + fallrush-gameserver:fc; the running :sub images are tagged :pre-fc first, the web build is kept as web.prefc, the compose
# file as docker-compose.prefc.yml. Rollback: `bash scripts/fc-vps-deploy.sh rollback`.
#   bash scripts/fc-vps-deploy.sh build | indexer | keepers | gs | web | rollback
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
source ~/.config/fall-rush/vps.env
DASH="${VPS_IP//./-}"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$SSH_KEY" "root@$VPS_IP")
SCP=(scp -q -o IdentitiesOnly=yes -i "$SSH_KEY")
LOG=docs/proof/fullchain/deploy.log; mkdir -p docs/proof/fullchain
note() { echo "$(date -u +%FT%TZ) $*" | sed "s/$VPS_IP/<VPS_IP>/g; s/$DASH/<VPS_IP-dashed>/g" | tee -a "$LOG"; }
busy() { curl -s --max-time 6 "https://fallrush.fun/api/status" | python3 -c "import json,sys; s=json.load(sys.stdin); print(sum(1 for x in s['servers'] if x.get('inMatch') and x.get('humans',0)>0))" 2>/dev/null || echo 0; }
wait_idle() { for _ in $(seq 1 120); do [ "$(busy)" = 0 ] && return 0; note "a human is in a match: waiting"; sleep 15; done; note "still busy after 30 min"; exit 1; }
healthy() { for _ in $(seq 1 50); do s=$("${SSH[@]}" "docker inspect -f '{{.State.Health.Status}}' $1 2>/dev/null || echo none"); [ "$s" = healthy ] && return 0; sleep 3; done; return 1; }
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
case "${1:-}" in
  build)
    # rollback tags of what runs now (the keepers / indexer run untagged image ids)
    "${SSH[@]}" 'set -e; cd /opt/fallrush
      docker image inspect fallrush-gameserver:pre-fc >/dev/null 2>&1 || docker tag fallrush-gameserver:sub fallrush-gameserver:pre-fc
      docker image inspect fallrush-node:pre-fc >/dev/null 2>&1 || docker tag fallrush-node:sub fallrush-node:pre-fc
      [ -f docker-compose.prefc.yml ] || cp docker-compose.yml docker-compose.prefc.yml
      echo "rollback tags + compose backup ready"'
    mkdir -p "$STAGE/node/tools" "$STAGE/node/programs/target"
    for d in keeper chain-worker indexer; do rsync -a --exclude .keys --exclude '*.json.bak' --exclude node_modules "tools/$d" "$STAGE/node/tools/"; done
    cp -R programs/target/idl "$STAGE/node/programs/target/"
    cp infra/prod/node-package.json "$STAGE/node/package.json"; cp infra/prod/node.Dockerfile "$STAGE/node/Dockerfile"
    tar -C "$STAGE/node" -czf "$STAGE/node.tgz" .
    "${SCP[@]}" "$STAGE/node.tgz" "root@$VPS_IP:/opt/fallrush/build/node-fc.tgz"
    "${SSH[@]}" 'set -e; cd /opt/fallrush/build; rm -rf node-fc; mkdir node-fc; tar -C node-fc -xzf node-fc.tgz; docker build -q -t fallrush-node:fc node-fc >/dev/null && echo "image fallrush-node:fc built"'
    tar -C client-unity/Builds/Server -czf "$STAGE/gs.tgz" FallRushServer.x86_64 UnityPlayer.so libdecor-0.so.0 libdecor-cairo.so FallRushServer_Data
    cp infra/prod/gameserver.Dockerfile "$STAGE/Dockerfile.gs"
    for t in 1 2 3 4; do "${SCP[@]}" "$STAGE/gs.tgz" "$STAGE/Dockerfile.gs" "root@$VPS_IP:/opt/fallrush/build/" && break; sleep 5; done
    "${SSH[@]}" 'set -e; cd /opt/fallrush/build; rm -rf gs-fc; mkdir gs-fc; tar -C gs-fc -xzf gs.tgz; docker build -q -f Dockerfile.gs -t fallrush-gameserver:fc gs-fc >/dev/null && echo "image fallrush-gameserver:fc built"'
    (cd web/app && node scripts/assets.mjs >/dev/null && ./node_modules/.bin/vite build --logLevel warn)
    rm -rf web/app/dist/unity && mkdir -p web/app/dist/unity
    cp -R client-unity/Builds/WebGL/Build client-unity/Builds/WebGL/StreamingAssets web/app/dist/unity/
    python3 tools/p5/build-json.py web/app/dist >/dev/null; chmod -R a+rX web/app/dist
    tar -C web/app/dist -czf "$STAGE/web.tgz" .
    for t in 1 2 3 4 5 6; do "${SCP[@]}" "$STAGE/web.tgz" "root@$VPS_IP:/opt/fallrush/web-fc.tgz" && break; sleep 5; done
    "${SSH[@]}" 'set -e; cd /opt/fallrush; rm -rf web.fc; mkdir web.fc; tar -C web.fc -xzf web-fc.tgz; chmod -R a+rX web.fc; du -sh web.fc | cut -f1'
    "${SCP[@]}" infra/prod/docker-compose.yml "root@$VPS_IP:/opt/fallrush/docker-compose.fc.yml"
    note "build: fallrush-node:fc + fallrush-gameserver:fc + web.fc staged (not live); rollback tags :pre-fc*" ;;
  indexer)
    wait_idle
    "${SSH[@]}" "set -e; cd /opt/fallrush; cp docker-compose.fc.yml docker-compose.yml; docker compose up -d --no-deps fallrush-indexer 2>&1 | tail -2"
    healthy fallrush-indexer && note "indexer: up on fallrush-node:fc (healthy)" || { note "indexer: NOT healthy"; exit 1; } ;;
  keepers)
    wait_idle
    "${SSH[@]}" "set -e; cd /opt/fallrush; cp docker-compose.fc.yml docker-compose.yml; docker compose --profile gs2 up -d --no-deps fallrush-keeper-1 fallrush-keeper-2 2>&1 | tail -2"
    healthy fallrush-keeper-1 && healthy fallrush-keeper-2 && note "keepers: up on fallrush-node:fc (healthy)" || { note "keepers: NOT healthy"; exit 1; } ;;
  gs)
    wait_idle
    "${SSH[@]}" "set -e; cd /opt/fallrush; cp docker-compose.fc.yml docker-compose.yml; docker compose --profile gs2 --profile gs3 up -d --no-deps fallrush-gs1 fallrush-gs2 fallrush-gs3 2>&1 | tail -3"
    healthy fallrush-gs1 && healthy fallrush-gs2 && healthy fallrush-gs3 && note "gs1/gs2/gs3: up on fallrush-gameserver:fc (healthy)" || { note "gs: NOT healthy"; exit 1; } ;;
  web)
    wait_idle
    "${SSH[@]}" 'set -e; cd /opt/fallrush; [ -d web.prefc ] || { mkdir web.prefc; (cd web && tar --exclude=./site -cf - .) | tar -C web.prefc -xf -; }; find web -mindepth 1 -maxdepth 1 ! -name site -exec rm -rf {} +; (cd web.fc && tar --exclude=./site -cf - .) | tar -C web -xf -; test -d web/site && echo "web/site kept"; docker exec fallrush-web nginx -t -q && docker exec fallrush-web nginx -s reload'
    note "web: switched to the fullchain build (web.prefc kept)" ;;
  rollback)
    "${SSH[@]}" 'set -e; cd /opt/fallrush; sed -i "s#fallrush-gameserver:fc#fallrush-gameserver:pre-fc#; s#fallrush-node:fc#fallrush-node:pre-fc#" docker-compose.yml; docker compose --profile gs2 --profile gs3 up -d --no-deps fallrush-gs1 fallrush-gs2 fallrush-gs3 fallrush-keeper-1 fallrush-keeper-2 fallrush-indexer 2>&1 | tail -3; if [ -d web.prefc ]; then find web -mindepth 1 -maxdepth 1 ! -name site -exec rm -rf {} +; (cd web.prefc && tar --exclude=./site -cf - .) | tar -C web -xf -; docker exec fallrush-web nginx -s reload; fi'
    note "rollback: images :pre-fc*, web.prefc" ;;
  *) echo "usage: $0 build|indexer|keepers|gs|web|rollback"; exit 2 ;;
esac
