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
  gsimage)
    # P7-UX: the game server image with protocol v7 (rooms + tiers); :p5 stays on the host for a rollback
    tar -C client-unity/Builds/Server -czf "$STAGE/gs.tgz" FallRushServer.x86_64 UnityPlayer.so libdecor-0.so.0 libdecor-cairo.so FallRushServer_Data
    cp infra/prod/gameserver.Dockerfile "$STAGE/Dockerfile.gs"
    for t in 1 2 3 4; do "${SCP[@]}" "$STAGE/gs.tgz" "$STAGE/Dockerfile.gs" "root@$VPS_IP:/opt/fallrush/build/" && break; sleep 5; done
    "${SSH[@]}" 'set -e; cd /opt/fallrush/build; rm -rf gs-p7ux; mkdir gs-p7ux; tar -C gs-p7ux -xzf gs.tgz; docker build -q -f Dockerfile.gs -t fallrush-gameserver:p7ux gs-p7ux >/dev/null && echo "image fallrush-gameserver:p7ux built"'
    "${SSH[@]}" 'set -e; mkdir -p /opt/fallrush/rooms /opt/fallrush/logs/gs3; chmod 755 /opt/fallrush/rooms; chown 1000:1000 /opt/fallrush/logs/gs3 2>/dev/null || true; ls -ld /opt/fallrush/rooms /opt/fallrush/logs/gs3 | wc -l'
    "${SSH[@]}" 'cd /opt/fallrush; grep -q "gs3=" .env || sed -i "s#^GS_PUBLIC=\(.*\)\$#GS_PUBLIC=\1,gs3=wss://gs1.'"$DASH"'.sslip.io/gs3#" .env; chmod 600 .env; grep -c "gs3=" .env'
    "${SCP[@]}" infra/prod/docker-compose.yml "root@$VPS_IP:/opt/fallrush/docker-compose.p7.yml"
    note "gsimage: fallrush-gameserver:p7ux built, rooms dir + gs3 logs ready, GS_PUBLIC has gs3 (not live)" ;;
  arena2)
    # gs1 on the v7 image (+ the rooms mount); keeper-1 untouched
    wait_idle
    "${SSH[@]}" "set -e; cd /opt/fallrush; cp docker-compose.yml docker-compose.p7pre-ux.yml; cp docker-compose.p7.yml docker-compose.yml; docker compose up -d --no-deps fallrush-gs1 2>&1 | tail -2"
    healthy fallrush-gs1 && note "arena2: gs1 on fallrush-gameserver:p7ux (healthy)" || { note "arena2: gs1 NOT healthy"; exit 1; } ;;
  arena3ux)
    wait_idle
    "${SSH[@]}" "set -e; cd /opt/fallrush; cp docker-compose.p7.yml docker-compose.yml; docker compose --profile gs2 up -d --no-deps fallrush-gs2 2>&1 | tail -2"
    healthy fallrush-gs2 && note "arena3: gs2 on fallrush-gameserver:p7ux (healthy)" || { note "arena3: gs2 NOT healthy"; exit 1; } ;;
  gs3)
    # the Classic-only room server: free memory first (the 1 GB cap is the P5 per-server cap)
    FREE=$("${SSH[@]}" "free -m | awk '/^Mem:/ {print \$7}'")
    note "gs3: host available memory ${FREE} MB"
    [ "${FREE:-0}" -ge 1400 ] || { note "gs3: SKIPPED (under 1.4 GB available): rooms stay arena-only"; exit 0; }
    "${SSH[@]}" "set -e; cd /opt/fallrush; cp docker-compose.p7.yml docker-compose.yml; docker compose --profile gs3 up -d --no-deps fallrush-gs3 2>&1 | tail -2"
    healthy fallrush-gs3 && note "gs3: up (Classic-only room server, 127.0.0.1:7779)" || { note "gs3: NOT healthy"; exit 1; } ;;
  nginx-gs3)
    # one location in the gs1 server blocks (port 80 + the certbot TLS block) of OUR conf only; nginx -t, then reload
    "${SSH[@]}" 'set -e; F=/etc/nginx/sites-available/fallrush-p5.conf; cp $F /opt/fallrush/fallrush-p5.conf.p7ux-bak
      if ! grep -q "location = /gs3" $F; then
        python3 - "$F" <<PY
import re,sys
p=sys.argv[1]; s=open(p).read()
loc="  location = /gs3 { proxy_pass http://127.0.0.1:7779/; proxy_http_version 1.1; proxy_set_header Upgrade \$http_upgrade; proxy_set_header Connection \$fallrush_p5_connection_upgrade; proxy_set_header Host \$host; proxy_read_timeout 3600s; proxy_send_timeout 3600s; }\n"
out=[]; n=0
for block in re.split(r"(?=server\s*\{)", s):
    if re.search(r"server_name\s+gs1\.", block) and "proxy_pass http://127.0.0.1:7777" in block:
        block=block.replace("  location / {", loc+"  location / {",1); n+=1
    out.append(block)
open(p,"w").write("".join(out)); print("gs1 blocks patched:", n)
PY
      fi
      if nginx -t 2>/dev/null; then systemctl reload nginx && echo reloaded; else cp /opt/fallrush/fallrush-p5.conf.p7ux-bak $F; echo "nginx -t FAILED: restored"; exit 1; fi'
    note "nginx-gs3: /gs3 location on the gs1 name (nginx -t ok, reloaded)" ;;
  *) echo "usage: $0 build|web|indexer|keeper-1|arena3|watchdog|gsimage|arena2|arena3ux|gs3|nginx-gs3"; exit 2 ;;
esac
