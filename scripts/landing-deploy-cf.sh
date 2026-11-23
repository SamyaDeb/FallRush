#!/usr/bin/env bash
# The landing's Cloudflare copy (waitlist.fallrush.fun): same build as the game URL's front door, but Play / Live links
# and the live widgets point at the game URL, and the worker lets the game URL call /api cross-origin.
# The game URL is derived from the VPS address, which lives outside git (~/.config/fall-rush/vps.env: VPS_IP=…).
#   bash scripts/landing-deploy-cf.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
source ~/.config/fall-rush/vps.env
PLAY="https://play.${VPS_IP//./-}.sslip.io"
FRONT="https://fallrush.fun"   # the front Worker (scripts/front-deploy.sh) in front of $PLAY
(cd web/site && VITE_PLAY_URL="$FRONT" VITE_LIVE_API="$FRONT" npm run build --silent)
(cd web/waitlist-worker && npx --prefix ../site wrangler deploy --var "ALLOWED_ORIGINS:$FRONT,https://www.fallrush.fun,$PLAY")
echo "deployed: https://waitlist.fallrush.fun (Play → $FRONT/play)"
