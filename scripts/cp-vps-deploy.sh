#!/usr/bin/env bash
# copy-fix deploy (2026-11-26, M3 rules): full-mode copy + logic fixes (judge card onchainAll, /live drops + Final-winner
# market in full mode, keeper Qualify seeding for full judge matches, landing copy + live stat).
# Swaps ONLY: the web app's index.html + assets/ (chain configs, maps, unity, nft untouched), the landing web/site, and the
# node image (indexer + keepers, through deploy-gate.sh, one container at a time). Game servers and the WebGL build untouched.
#   bash scripts/cp-vps-deploy.sh build | indexer | keeper-1 | keeper-2 | web | site | rollback
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
source ~/.config/fall-rush/vps.env
DASH="${VPS_IP//./-}"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$SSH_KEY" "root@$VPS_IP")
SCP=(scp -q -o IdentitiesOnly=yes -i "$SSH_KEY")
LOG=docs/proof/fullchain/copyfix-deploy.log
note() { echo "$(date -u +%FT%TZ) $*" | sed "s/$VPS_IP/<VPS_IP>/g; s/$DASH/<VPS_IP-dashed>/g" | tee -a "$LOG"; }
healthy() { for _ in $(seq 1 60); do s=$("${SSH[@]}" "docker inspect -f '{{.State.Health.Status}}' $1 2>/dev/null || echo none"); [ "$s" = healthy ] && return 0; sleep 3; done; return 1; }
gate() { "${SSH[@]}" "cd /opt/fallrush && $2 MAX_WAIT_S=${MAX_WAIT_S:-1800} bash /opt/fallrush/deploy-gate.sh $1" 2>&1 | sed "s/$VPS_IP/<VPS_IP>/g" | tail -6; }
SITE_OUT="${SITE_OUT:?set SITE_OUT to the landing build dir}"
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
case "${1:-}" in
  build)
    "${SSH[@]}" 'set -e; cd /opt/fallrush
      docker image inspect fallrush-node:pre-cp >/dev/null 2>&1 || docker tag fallrush-node:hd fallrush-node:pre-cp  # the indexer ran a pruned image id; :hd = what the keepers run
      [ -f docker-compose.precp.yml ] || cp docker-compose.yml docker-compose.precp.yml
      [ -d web.precp ] || { mkdir web.precp; cp -R web/index.html web/assets web.precp/; }
      [ -d web.site-precp ] || cp -R web/site web.site-precp
      echo "rollback: fallrush-node:pre-cp (indexer image; keepers run :hd), compose/web/site backups"'
    mkdir -p "$STAGE/node/tools" "$STAGE/node/programs/target"
    for d in keeper chain-worker indexer; do rsync -a --exclude .keys --exclude '*.json.bak' --exclude node_modules --exclude '*.test.ts' "tools/$d" "$STAGE/node/tools/"; done
    cp -R programs/target/idl "$STAGE/node/programs/target/"
    cp infra/prod/node-package.json "$STAGE/node/package.json"; cp infra/prod/node.Dockerfile "$STAGE/node/Dockerfile"
    tar -C "$STAGE/node" -czf "$STAGE/node.tgz" .
    "${SCP[@]}" "$STAGE/node.tgz" "root@$VPS_IP:/opt/fallrush/build/node-cp.tgz"
    "${SSH[@]}" 'set -e; cd /opt/fallrush/build; rm -rf node-cp; mkdir node-cp; tar -C node-cp -xzf node-cp.tgz; docker build -q -t fallrush-node:cp node-cp >/dev/null && echo "image fallrush-node:cp built"
      cd /opt/fallrush; sed "s#fallrush-node:hd#fallrush-node:cp#" docker-compose.yml > docker-compose.cp.yml; grep -c "fallrush-node:cp" docker-compose.cp.yml'
    tar -C web/app/dist -czf "$STAGE/web.tgz" index.html assets
    tar -C "$SITE_OUT" -czf "$STAGE/site.tgz" .
    "${SCP[@]}" "$STAGE/web.tgz" "$STAGE/site.tgz" "root@$VPS_IP:/opt/fallrush/build/"
    "${SSH[@]}" 'set -e; cd /opt/fallrush/build; rm -rf web-cp site-cp; mkdir web-cp site-cp; tar -C web-cp -xzf web.tgz; tar -C site-cp -xzf site.tgz; chmod -R a+rX web-cp site-cp; ls web-cp site-cp'
    note "build: fallrush-node:cp + web-cp (index+assets) + site-cp staged (not live); rollback fallrush-node:pre-cp, web.precp, web.site-precp" ;;
  indexer|keeper-1|keeper-2)
    "${SSH[@]}" "cd /opt/fallrush && cp docker-compose.cp.yml docker-compose.yml"
    R=""; [ "$1" != indexer ] && R="RESERVE=1"
    gate "fallrush-$1" "$R"
    healthy "fallrush-$1" && note "$1: up on fallrush-node:cp (healthy)" || { note "$1: NOT healthy"; exit 1; } ;;
  web)
    "${SSH[@]}" 'set -e; cd /opt/fallrush; cp -R build/web-cp/assets/. web/assets/; cp build/web-cp/index.html web/index.html; chmod -R a+rX web/index.html web/assets
      docker exec fallrush-web nginx -t -q && docker exec fallrush-web nginx -s reload && echo reloaded'
    note "web: index.html + assets swapped (chain configs, maps, unity untouched; web.precp kept)" ;;
  site)
    "${SSH[@]}" 'set -e; cd /opt/fallrush; cp -R build/site-cp/. web/site/; chmod -R a+rX web/site; docker exec fallrush-web nginx -t -q && docker exec fallrush-web nginx -s reload && echo reloaded'
    note "site: landing swapped (web.site-precp kept)" ;;
  rollback)
    "${SSH[@]}" 'set -e; cd /opt/fallrush; cp docker-compose.precp.yml docker-compose.yml
      docker tag fallrush-node:pre-cp fallrush-node:cp-rolledback
      cp web.precp/index.html web/index.html; rm -rf web/site; cp -R web.site-precp web/site; docker exec fallrush-web nginx -s reload
      echo "web + site restored; recreate indexer/keepers through deploy-gate.sh (compose = precp)"'
    note "rollback: compose precp, web index, site restored" ;;
  *) echo "usage: $0 build|indexer|keeper-1|keeper-2|web|site|rollback"; exit 2 ;;
esac
