#!/usr/bin/env bash
# P6 task 6.6 (locked): stranger → lobby, 3 clean-profile runs (/arenas → click Play → seated on a game server).
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"; source ~/.config/fall-rush/vps.env; P="https://play.${VPS_IP//./-}.sslip.io"
for i in 1 2 3; do
  node tools/p6/smoke.mjs --flow lobby --base "$P" --out "docs/proof/p6/public/lobby-$i" --port 951$i --max 90 > "docs/proof/p6/public/lobby-$i.out" 2>&1
  pkill -f "remote-debugging-port=951$i" 2>/dev/null
  echo "run $i: $(python3 -c "import json;r=json.load(open('docs/proof/p6/public/lobby-$i/result.json'));print(r.get('clickToLobbyS'), 'popups', r.get('popups',0), 'pass', r.get('pass'))")"
  sleep 25
done
