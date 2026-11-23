#!/usr/bin/env bash
# P7 A3.2 (locked): the chaos tests in two waves on the PUBLIC stack (fallrush-* only, under the M3 rules). Each wave
# plays real public matches with WebGL players (tools/p7/shots.mjs: they also give the after-screenshots).
#   wave1: gs1 player throttled (Slow 4G + 150 ms, 1920×1080) + keeper-1 restarted at the Final;
#          gs2 player (1280×720, candy look) + gs2 restarted mid-round 2 (abort + refunds expected)
#   wave2: gs2 player with a sealed bet + keeper-2 without the TEE (bad TEE_URL; Path B self-reveal expected)
#          + the indexer stopped mid-match (the watchdog must restart it)
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p7-chaos -- bash scripts/p7-chaos-waves.sh wave1|wave2
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"; source ~/.config/fall-rush/vps.env
D="${VPS_IP//./-}"; P="https://play.$D.sslip.io"; OUT=docs/proof/p7/chaos; mkdir -p "$OUT"
SSH=(ssh -o IdentitiesOnly=yes -o ConnectTimeout=8 -i "$SSH_KEY" "root@$VPS_IP")
S=/private/tmp/claude-501/-Users-samya-Downloads-FallRush/3189a6f4-0381-45f9-bcef-e5c20d0c695b/scratchpad
case "$1" in
  wave1)
    bash scripts/p7-neighbours.sh chaos-wave1-before >/dev/null
    node tools/p7/shots.mjs --flow player --base "$P" --server "wss://gs1.$D.sslip.io" --out "$S/chaos-gs1" --every 3 --max 1200 --w 1920 --h 1080 --throttle --q "&autobet=10" > "$OUT/w1-gs1-player.out" 2>&1 & A=$!
    sleep 20
    node tools/p7/shots.mjs --flow player --base "$P" --server "wss://gs2.$D.sslip.io" --out "$S/chaos-gs2" --every 3 --max 900 --w 1280 --h 720 --port 9372 --q "&look=candy&autobet=10" > "$OUT/w1-gs2-player.out" 2>&1 & B=$!
    bash scripts/p7-chaos.sh keeper 1 final > "$OUT/w1-keeper.out" 2>&1 & C=$!
    bash scripts/p7-chaos.sh gsround 2 2 > "$OUT/w1-gsround.out" 2>&1 & E=$!
    wait $A $B $C $E
    bash scripts/p7-neighbours.sh chaos-wave1-after >/dev/null ;;
  wave2)
    bash scripts/p7-neighbours.sh chaos-wave2-before >/dev/null
    printf 'services:\n  fallrush-keeper-2:\n    environment: { TEE_URL: "https://devnet-tee.invalid" }\n' | "${SSH[@]}" 'cat > /opt/fallrush/docker-compose.chaos-tee.yml'
    "${SSH[@]}" 'cd /opt/fallrush && docker compose -f docker-compose.yml -f docker-compose.chaos-tee.yml --profile gs2 up -d --no-deps fallrush-keeper-2 2>&1 | tail -1'
    echo "$(date -u +%FT%TZ) keeper-2 started without the TEE (TEE_URL=devnet-tee.invalid)" | tee -a "$OUT/kill.log"
    node tools/p7/shots.mjs --flow player --base "$P" --server "wss://gs2.$D.sslip.io" --out "$S/chaos-tee" --every 4 --max 1200 --w 1280 --h 720 --port 9373 --q "&autobet=10" > "$OUT/w2-player.out" 2>&1 & A=$!
    # the indexer down once the match is in round 2
    until curl -s --max-time 5 "$P/api/status" | python3 -c "import json,sys; s=[x for x in json.load(sys.stdin)['servers'] if x['id']=='gs2'][0]; sys.exit(0 if s.get('inMatch') and s.get('round',0)>=2 else 1)" 2>/dev/null; do sleep 3; done
    bash scripts/p7-chaos.sh indexer > "$OUT/w2-indexer.out" 2>&1
    wait $A
    "${SSH[@]}" 'cd /opt/fallrush && docker compose --profile gs2 up -d --no-deps fallrush-keeper-2 2>&1 | tail -1; rm -f docker-compose.chaos-tee.yml'
    echo "$(date -u +%FT%TZ) keeper-2 back on the normal TEE_URL" | tee -a "$OUT/kill.log"
    bash scripts/p7-neighbours.sh chaos-wave2-after >/dev/null ;;
esac
sed -i '' "s/$VPS_IP/<VPS_IP>/g; s/$D/<VPS_IP-dashed>/g" "$OUT"/*.out "$OUT"/*.log 2>/dev/null || true
