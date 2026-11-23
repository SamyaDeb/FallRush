#!/usr/bin/env bash
# P5 redeploy (one line): build locally → copy to the VPS → docker compose up -d → smoke (a). VPS rules (M3, doc 13):
# only /opt/fallrush/** and fallrush-* names; ports on 127.0.0.1; never touch finity / Ovelo / other nginx sites.
# The VPS address lives outside git: ~/.config/fall-rush/vps.env (VPS_IP=…, SSH_KEY=…).
#   bash scripts/p5-deploy.sh [web|node|gs|all] [--gs2]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
source ~/.config/fall-rush/vps.env
WHAT="${1:-all}"; GS2="${2:-}"
DASH="${VPS_IP//./-}"; PLAY="play.$DASH.sslip.io"
SSH=(ssh -o IdentitiesOnly=yes -i "$SSH_KEY" "root@$VPS_IP")
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
if [[ "$WHAT" == web || "$WHAT" == all ]]; then
  (cd web/app && node scripts/assets.mjs && ./node_modules/.bin/vite build --logLevel warn)
  rm -rf web/app/dist/unity && mkdir -p web/app/dist/unity
  cp -R client-unity/Builds/WebGL/Build client-unity/Builds/WebGL/StreamingAssets web/app/dist/unity/
  python3 tools/p5/build-json.py web/app/dist
  chmod -R a+rX web/app/dist
  tar -C web/app/dist -czf "$STAGE/web.tgz" .
  scp -q -o IdentitiesOnly=yes -i "$SSH_KEY" "$STAGE/web.tgz" "root@$VPS_IP:/opt/fallrush/web.tgz"
  "${SSH[@]}" 'set -e; cd /opt/fallrush; rm -rf web.new; mkdir web.new; tar -C web.new -xzf web.tgz; chmod -R a+rX web.new; mkdir -p web; find web -mindepth 1 -delete; cp -a web.new/. web/; rm -rf web.new web.old web.tgz'
fi
if [[ "$WHAT" == node || "$WHAT" == all ]]; then
  mkdir -p "$STAGE/node/tools" "$STAGE/node/programs/target"
  for d in keeper chain-worker indexer; do rsync -a --exclude .keys --exclude '*.json.bak' "tools/$d" "$STAGE/node/tools/"; done
  cp -R programs/target/idl "$STAGE/node/programs/target/"
  cp infra/prod/node-package.json "$STAGE/node/package.json"; cp infra/prod/node.Dockerfile "$STAGE/node/Dockerfile"
  tar -C "$STAGE/node" -czf "$STAGE/node.tgz" .
  scp -q -o IdentitiesOnly=yes -i "$SSH_KEY" "$STAGE/node.tgz" "root@$VPS_IP:/opt/fallrush/build/node.tgz"
  "${SSH[@]}" 'set -e; cd /opt/fallrush/build; rm -rf node; mkdir node; tar -C node -xzf node.tgz; docker build -q -t fallrush-node:p5 node'
fi
if [[ "$WHAT" == gs || "$WHAT" == all ]]; then
  tar -C client-unity/Builds/Server -czf "$STAGE/gs.tgz" FallRushServer.x86_64 UnityPlayer.so libdecor-0.so.0 libdecor-cairo.so FallRushServer_Data
  cp infra/prod/gameserver.Dockerfile "$STAGE/Dockerfile.gs"
  scp -q -o IdentitiesOnly=yes -i "$SSH_KEY" "$STAGE/gs.tgz" "$STAGE/Dockerfile.gs" "root@$VPS_IP:/opt/fallrush/build/"
  "${SSH[@]}" 'set -e; cd /opt/fallrush/build; rm -rf gs; mkdir gs; tar -C gs -xzf gs.tgz; docker build -q -f Dockerfile.gs -t fallrush-gameserver:p5 gs'
fi
scp -q -o IdentitiesOnly=yes -i "$SSH_KEY" infra/prod/docker-compose.yml infra/prod/nginx-web.conf "root@$VPS_IP:/opt/fallrush/"
"${SSH[@]}" "cd /opt/fallrush && docker compose ${GS2:+--profile gs2} up -d --remove-orphans 2>&1 | tail -3 && docker exec fallrush-web nginx -t -q && docker exec fallrush-web nginx -s reload"
sleep 5
echo "smoke a: $(curl -s -o /dev/null -w '%{http_code}' "https://$PLAY/") / api $(curl -s "https://$PLAY/api/status" | head -c 160)"
