#!/usr/bin/env bash
# P7 (copy of p6-vps-deploy.sh; P6 header below). Adds `watchdog` (installs the fallrush-watchdog systemd timer).
# P6 task 5: staged redeploy on the shared VPS under the M3 rules (doc 13 §2a, prompt §1.3). Only /opt/fallrush/** and
# fallrush-* names; one service at a time, never while a human is in a match (checked via /api/status); neighbours
# (finity, Ovelo) + health after every step; the previous web build is kept as web.p6bak and the fallrush-node:p5 image
# stays for a rollback (compose image back to :p5 + `docker compose up -d <svc>`).
#   bash scripts/p6-vps-deploy.sh build      stage + upload the node image (build only) and the web build (not live yet)
#   bash scripts/p6-vps-deploy.sh <svc>      switch one service: web | indexer | keeper-1 | arena3 (gs2 + keeper-2)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
source ~/.config/fall-rush/vps.env
DASH="${VPS_IP//./-}"; PLAY="https://play.$DASH.sslip.io"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$SSH_KEY" "root@$VPS_IP")
SCP=(scp -q -o IdentitiesOnly=yes -i "$SSH_KEY")
LOG=docs/proof/p7/vps/deploy.log; mkdir -p docs/proof/p7/vps
note() { echo "$(date -u +%FT%TZ) $*" | sed "s/$VPS_IP/<VPS_IP>/g; s/$DASH/<VPS_IP-dashed>/g" | tee -a "$LOG"; }
busy() { curl -s --max-time 6 "$PLAY/api/status" | python3 -c "import json,sys; s=json.load(sys.stdin); print(sum(1 for x in s['servers'] if x.get('inMatch') and x.get('humans',0)>0))" 2>/dev/null || echo 0; }
wait_idle() { for _ in $(seq 1 120); do [ "$(busy)" = 0 ] && return 0; note "a human is in a match: waiting"; sleep 15; done; note "still busy after 30 min"; exit 1; }
healthy() { for _ in $(seq 1 40); do s=$("${SSH[@]}" "docker inspect -f '{{.State.Health.Status}}' $1 2>/dev/null || echo none"); [ "$s" = healthy ] && return 0; sleep 3; done; return 1; }
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
case "${1:-}" in
  build)
    mkdir -p "$STAGE/node/tools" "$STAGE/node/programs/target"
    for d in keeper chain-worker indexer; do rsync -a --exclude .keys --exclude '*.json.bak' "tools/$d" "$STAGE/node/tools/"; done
    cp -R programs/target/idl "$STAGE/node/programs/target/"
    cp infra/prod/node-package.json "$STAGE/node/package.json"; cp infra/prod/node.Dockerfile "$STAGE/node/Dockerfile"
    tar -C "$STAGE/node" -czf "$STAGE/node.tgz" .
    "${SCP[@]}" "$STAGE/node.tgz" "root@$VPS_IP:/opt/fallrush/build/node-p7.tgz"
    "${SSH[@]}" 'set -e; cd /opt/fallrush/build; rm -rf node-p7; mkdir node-p7; tar -C node-p7 -xzf node-p7.tgz; docker build -q -t fallrush-node:p7 node-p7 >/dev/null && echo "image fallrush-node:p7 built"'
    (cd web/app && node scripts/assets.mjs >/dev/null && ./node_modules/.bin/vite build --logLevel warn)
    rm -rf web/app/dist/unity && mkdir -p web/app/dist/unity
    cp -R client-unity/Builds/WebGL/Build client-unity/Builds/WebGL/StreamingAssets web/app/dist/unity/
    python3 tools/p5/build-json.py web/app/dist >/dev/null; chmod -R a+rX web/app/dist
    tar -C web/app/dist -czf "$STAGE/web.tgz" .
    for t in 1 2 3 4 5 6; do "${SCP[@]}" "$STAGE/web.tgz" "root@$VPS_IP:/opt/fallrush/web-p7.tgz" && break; sleep 5; done
    "${SSH[@]}" 'set -e; cd /opt/fallrush; rm -rf web.p7; mkdir web.p7; tar -C web.p7 -xzf web-p7.tgz; chmod -R a+rX web.p7; du -sh web.p7 | cut -f1'
    "${SCP[@]}" infra/prod/docker-compose.yml "root@$VPS_IP:/opt/fallrush/docker-compose.p7.yml"
    "${SSH[@]}" 'cd /opt/fallrush; grep -q "^SEALED=" .env || printf "SEALED=1\nSEASONS=1\nTEE_URL=https://devnet-tee.magicblock.app\nTEST_SEASON=0\n" >> .env; chmod 600 .env; grep -c "^SEALED=\|^SEASONS=\|^TEE_URL=\|^TEST_SEASON=" .env'
    note "build: node image fallrush-node:p7 + web.p7 staged (not live)" ;;
  web)
    wait_idle
    # the landing (web/site, another session's deploy) lives in web/site: never delete or overwrite it
    "${SSH[@]}" 'set -e; cd /opt/fallrush; rm -rf web.p6bak; mkdir web.p6bak; (cd web && tar --exclude=./site -cf - .) | tar -C web.p6bak -xf -; find web -mindepth 1 -maxdepth 1 ! -name site -exec rm -rf {} +; (cd web.p7 && tar --exclude=./site -cf - .) | tar -C web -xf -; test -d web/site && echo "web/site kept"; docker exec fallrush-web nginx -t -q && docker exec fallrush-web nginx -s reload'
    note "web: switched to the P7 build (web.p6bak kept)" ;;
  indexer|keeper-1)
    wait_idle
    "${SSH[@]}" "set -e; cd /opt/fallrush; cp docker-compose.yml docker-compose.p6bak.yml 2>/dev/null || true; cp docker-compose.p7.yml docker-compose.yml; docker compose up -d --no-deps fallrush-$1 2>&1 | tail -2"
    healthy "fallrush-$1" && note "$1: up on fallrush-node:p7 (healthy)" || { note "$1: NOT healthy"; exit 1; } ;;
  arena3)
    wait_idle
    "${SSH[@]}" "set -e; cd /opt/fallrush; cp docker-compose.p7.yml docker-compose.yml; docker compose --profile gs2 up -d --no-deps fallrush-gs2 fallrush-keeper-2 2>&1 | tail -2"
    healthy fallrush-keeper-2 && healthy fallrush-gs2 && note "arena3: gs2 + keeper-2 up (keeper on :p7)" || { note "arena3: NOT healthy"; exit 1; } ;;
  watchdog)
    # P7 A3.4: a host systemd timer named fallrush-watchdog (CPUQuota 10 %, MemoryMax 64 M); it restarts only fallrush-*
    "${SSH[@]}" 'mkdir -p /opt/fallrush/watchdog /opt/fallrush/logs'
    "${SCP[@]}" infra/prod/watchdog/fallrush-watchdog.sh "root@$VPS_IP:/opt/fallrush/watchdog/fallrush-watchdog.sh"
    "${SCP[@]}" infra/prod/watchdog/fallrush-watchdog.service infra/prod/watchdog/fallrush-watchdog.timer "root@$VPS_IP:/etc/systemd/system/"
    "${SSH[@]}" 'set -e; chmod 755 /opt/fallrush/watchdog/fallrush-watchdog.sh; systemctl daemon-reload; systemctl enable --now fallrush-watchdog.timer; systemctl start fallrush-watchdog.service; sleep 2; systemctl is-active fallrush-watchdog.timer; tail -3 /opt/fallrush/logs/watchdog.log 2>/dev/null || true'
    note "watchdog: fallrush-watchdog.timer active" ;;
  *) echo "usage: $0 build|web|indexer|keeper-1|arena3|watchdog"; exit 2 ;;
esac
