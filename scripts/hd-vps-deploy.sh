#!/usr/bin/env bash
# harden deploy (copy of fc-vps-deploy.sh, same M3 rules): new tag fallrush-node:hd (keepers + indexer) and web.hd (web build +
# nginx-web.conf). The game servers are unchanged (no gs rebuild). Every container recreate goes through infra/prod/deploy-gate.sh
# on the VPS (keepers idle + both arena servers in LobbyOpen with 0 humans), one container at a time.
# Rollback: `bash scripts/hd-vps-deploy.sh rollback` (images :pre-hd = what ran before, web.prehd, nginx-web.conf.prehd, .env.prehd).
#   bash scripts/hd-vps-deploy.sh build | gate-install | keeper-2 | keeper-1 | indexer | web | rollback
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
source ~/.config/fall-rush/vps.env
DASH="${VPS_IP//./-}"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$SSH_KEY" "root@$VPS_IP")
SCP=(scp -q -o IdentitiesOnly=yes -i "$SSH_KEY")
LOG=docs/proof/fullchain/harden-deploy.log
note() { echo "$(date -u +%FT%TZ) $*" | sed "s/$VPS_IP/<VPS_IP>/g; s/$DASH/<VPS_IP-dashed>/g" | tee -a "$LOG"; }
healthy() { for _ in $(seq 1 60); do s=$("${SSH[@]}" "docker inspect -f '{{.State.Health.Status}}' $1 2>/dev/null || echo none"); [ "$s" = healthy ] && return 0; sleep 3; done; return 1; }
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
gate() { # $1 service, extra env before
  "${SSH[@]}" "cd /opt/fallrush && $2 MAX_WAIT_S=${MAX_WAIT_S:-1800} bash /opt/fallrush/deploy-gate.sh $1" 2>&1 | sed "s/$VPS_IP/<VPS_IP>/g" | tail -6
}
case "${1:-}" in
  build)
    "${SSH[@]}" 'set -e; cd /opt/fallrush
      docker image inspect fallrush-node:pre-hd >/dev/null 2>&1 || docker tag fallrush-node:fc fallrush-node:pre-hd  # = the last fc build (the indexer runs it; the keepers run older fc layers that were re-tagged)
      [ -f docker-compose.prehd.yml ] || cp docker-compose.yml docker-compose.prehd.yml
      [ -f .env.prehd ] || cp .env .env.prehd
      [ -f nginx-web.conf.prehd ] || cp nginx-web.conf nginx-web.conf.prehd
      echo "rollback tag fallrush-node:pre-hd + compose/.env/nginx backups ready"'
    mkdir -p "$STAGE/node/tools" "$STAGE/node/programs/target"
    for d in keeper chain-worker indexer; do rsync -a --exclude .keys --exclude '*.json.bak' --exclude node_modules --exclude '*.test.ts' "tools/$d" "$STAGE/node/tools/"; done
    cp -R programs/target/idl "$STAGE/node/programs/target/"
    cp infra/prod/node-package.json "$STAGE/node/package.json"; cp infra/prod/node.Dockerfile "$STAGE/node/Dockerfile"
    tar -C "$STAGE/node" -czf "$STAGE/node.tgz" .
    "${SCP[@]}" "$STAGE/node.tgz" "root@$VPS_IP:/opt/fallrush/build/node-hd.tgz"
    "${SSH[@]}" 'set -e; cd /opt/fallrush/build; rm -rf node-hd; mkdir node-hd; tar -C node-hd -xzf node-hd.tgz; docker build -q -t fallrush-node:hd node-hd >/dev/null && echo "image fallrush-node:hd built"'
    (cd web/app && node scripts/assets.mjs >/dev/null && ./node_modules/.bin/vite build --logLevel warn)
    rm -rf web/app/dist/unity && mkdir -p web/app/dist/unity
    cp -R client-unity/Builds/WebGL/Build client-unity/Builds/WebGL/StreamingAssets web/app/dist/unity/
    python3 tools/p5/build-json.py web/app/dist >/dev/null; chmod -R a+rX web/app/dist
    tar -C web/app/dist -czf "$STAGE/web.tgz" .
    for t in 1 2 3 4 5 6; do "${SCP[@]}" "$STAGE/web.tgz" "root@$VPS_IP:/opt/fallrush/web-hd.tgz" && break; sleep 5; done
    "${SSH[@]}" 'set -e; cd /opt/fallrush; rm -rf web.hd; mkdir web.hd; tar -C web.hd -xzf web-hd.tgz; chmod -R a+rX web.hd; du -sh web.hd | cut -f1'
    sed 's#fallrush-node:fc#fallrush-node:hd#' infra/prod/docker-compose.yml > "$STAGE/compose.yml"
    "${SCP[@]}" "$STAGE/compose.yml" "root@$VPS_IP:/opt/fallrush/docker-compose.hd.yml"
    "${SCP[@]}" infra/prod/nginx-web.conf "root@$VPS_IP:/opt/fallrush/nginx-web.conf.hd"
    note "build: fallrush-node:hd + web.hd + nginx-web.conf.hd + docker-compose.hd.yml staged (not live)" ;;
  gate-install)
    "${SCP[@]}" infra/prod/deploy-gate.sh "root@$VPS_IP:/opt/fallrush/deploy-gate.sh"
    "${SSH[@]}" 'set -e; cd /opt/fallrush; chmod +x deploy-gate.sh; cp watchdog/fallrush-watchdog.sh watchdog/fallrush-watchdog.sh.prehd 2>/dev/null || true'
    "${SCP[@]}" infra/prod/watchdog/fallrush-watchdog.sh "root@$VPS_IP:/opt/fallrush/watchdog/fallrush-watchdog.sh"
    "${SSH[@]}" 'chmod +x /opt/fallrush/watchdog/fallrush-watchdog.sh; bash -n /opt/fallrush/watchdog/fallrush-watchdog.sh && echo watchdog ok'
    gate fallrush-keeper-1 "DRY_RUN=1 MAX_WAIT_S=20"
    note "gate-install: deploy-gate.sh + watchdog (prev kept as .prehd)" ;;
  keeper-1|keeper-2|indexer)
    "${SSH[@]}" "cd /opt/fallrush && cp docker-compose.hd.yml docker-compose.yml"
    R=""; [ "$1" != indexer ] && R="RESERVE=1"
    gate "fallrush-$1" "$R"
    healthy "fallrush-$1" && note "$1: up on fallrush-node:hd (healthy)" || { note "$1: NOT healthy"; exit 1; } ;;
  web)
    gate fallrush-web "DRY_RUN=1" | tail -2
    "${SSH[@]}" 'set -e; cd /opt/fallrush; [ -d web.prehd ] || { mkdir web.prehd; (cd web && tar --exclude=./site -cf - .) | tar -C web.prehd -xf -; }
      cp nginx-web.conf.hd nginx-web.conf
      if ! docker exec fallrush-web nginx -t -q; then cp nginx-web.conf.prehd nginx-web.conf; echo "nginx -t FAILED: restored"; exit 1; fi
      find web -mindepth 1 -maxdepth 1 ! -name site -exec rm -rf {} +; (cd web.hd && tar --exclude=./site -cf - .) | tar -C web -xf -; test -d web/site && echo "web/site kept"
      docker exec fallrush-web nginx -s reload && echo reloaded'
    note "web: switched to the harden build + nginx-web.conf (web.prehd kept)" ;;
  rollback)
    "${SSH[@]}" 'set -e; cd /opt/fallrush; cp docker-compose.prehd.yml docker-compose.yml
      docker tag fallrush-node:pre-hd fallrush-node:fc; docker compose up -d --no-deps fallrush-keeper-1 fallrush-keeper-2 fallrush-indexer 2>&1 | tail -2
      if [ -d web.prehd ]; then find web -mindepth 1 -maxdepth 1 ! -name site -exec rm -rf {} +; (cd web.prehd && tar --exclude=./site -cf - .) | tar -C web -xf -; fi
      cp nginx-web.conf.prehd nginx-web.conf; docker exec fallrush-web nginx -t -q && docker exec fallrush-web nginx -s reload
      [ -f watchdog/fallrush-watchdog.sh.prehd ] && cp watchdog/fallrush-watchdog.sh.prehd watchdog/fallrush-watchdog.sh; echo rolled back'
    note "rollback: node :pre-hd, web.prehd, nginx-web.conf.prehd, watchdog.prehd" ;;
  *) echo "usage: $0 build|gate-install|keeper-1|keeper-2|indexer|web|rollback"; exit 2 ;;
esac
