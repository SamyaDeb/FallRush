#!/usr/bin/env bash
# P5: quick web redeploy (the app + StreamingAssets + small Unity files; the two big .unityweb files are kept on the
# VPS): for small changes over a flaky uplink. Unpacks over /opt/fallrush/web (the bind-mounted dir, in place).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"; source ~/.config/fall-rush/vps.env
(cd web/app && node scripts/assets.mjs >/dev/null && ./node_modules/.bin/vite build --logLevel warn)
rm -rf web/app/dist/unity && mkdir -p web/app/dist/unity && cp -R client-unity/Builds/WebGL/Build client-unity/Builds/WebGL/StreamingAssets web/app/dist/unity/
python3 tools/p5/build-json.py web/app/dist >/dev/null; chmod -R a+rX web/app/dist
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
tar -C web/app/dist --exclude='*.data.unityweb' --exclude='*.wasm.unityweb' -czf "$T/q.tgz" .
for t in 1 2 3 4 5 6 7 8; do scp -q -o IdentitiesOnly=yes -i "$SSH_KEY" "$T/q.tgz" "root@$VPS_IP:/opt/fallrush/webq.tgz" 2>/dev/null && break; [ $t = 8 ] && exit 1; sleep 5; done
ssh -o IdentitiesOnly=yes -i "$SSH_KEY" "root@$VPS_IP" 'cd /opt/fallrush && rm -rf web/assets && tar -C web -xzf webq.tgz && chmod -R a+rX web && rm webq.tgz && echo deployed'
