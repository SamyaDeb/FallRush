#!/usr/bin/env bash
# fallrush.fun front Worker (web/front-worker): pass-through to the VPS game host. The origin holds the VPS address and
# the shared secret guards the client-IP header; both live outside git:
#   ~/.config/fall-rush/vps.env   VPS_IP=…, SSH_KEY=…
#   ~/.config/fall-rush/front.env FRONT_KEY=…   (same value as the map in the VPS host block fallrush-p5.conf)
#   bash scripts/front-deploy.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT/web/front-worker"
source ~/.config/fall-rush/vps.env; source ~/.config/fall-rush/front.env
ORIGIN="https://play.${VPS_IP//./-}.sslip.io"
W=(npx --prefix ../site wrangler)
"${W[@]}" deploy --var "ORIGIN:$ORIGIN"
printf '%s' "$FRONT_KEY" | "${W[@]}" secret put FRONT_KEY
echo "deployed: https://fallrush.fun → $ORIGIN"
