#!/usr/bin/env bash
# P3-H/N (locked): the WebGL build in Chrome, served by the chain emulator (--static):
#   a  ?rcselftest=1          browser parity          b  ?onchain=emu Final (auto-play)   c  hidden tab 10 s + 30 s
#   d  ?onchain=devnet Final  (P2's keeper MODE=final; the page's burner is funded once from the admin wallet, 0.01)
#   e  plain Classic match    (Mac dev server, ?bot=match)   f  Classic match + on-chain Final on the emulator (switch)
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p3-webgl-n -- bash scripts/p3-webgl-n.sh [a b c d e f]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
P2="$ROOT/../FallRush-p2"
OUT=docs/proof/p3/webgl; LOG=.m1-logs/p3-n; mkdir -p "$OUT" "$LOG"
STEPS="${*:-a b c d e f}"
node tools/chain-emulator/src/main.mjs --port 8797 --static client-unity/Builds/WebGL > "$LOG/emu.out" 2>&1 &
EMU=$!
trap 'kill $EMU 2>/dev/null; bash scripts/m1-macserver.sh stop >/dev/null 2>&1' EXIT
sleep 1
U=http://localhost:8797/index.html
M="node scripts/p3-webgl-measure.mjs"
for s in $STEPS; do
  echo "== $s"
  case $s in
    a) $M "$U?rcselftest=1" "$LOG/a" 90 --shot=30 ;;
    b) $M "$U?onchain=emu&onchainauto=150&hud=1" "$LOG/b" 220 --shot=20 ;;
    c) $M "$U?onchain=emu&onchainauto=120" "$LOG/c10" 170 --shot=30 --hide=40:10
       $M "$U?onchain=emu&onchainauto=150" "$LOG/c30" 200 --shot=30 --hide=40:30 ;;
    d) PROF="$ROOT/.m1-logs/p3-n-chrome-devnet"
       $M "$U?onchain=devnet&onchainauto=240&hud=1" "$LOG/d" 300 --shot=20 --profile="$PROF" &
       MP=$!
       for _ in $(seq 1 60); do B=$(grep -o "burner [1-9A-HJ-NP-Za-km-z]\{32,44\}" "$LOG/d/console.log" 2>/dev/null | head -1 | awk '{print $2}'); [[ -n "$B" ]] && break; sleep 1; done
       echo "browser burner $B"
       BAL=$(solana balance -u devnet "$B" 2>/dev/null | awk '{print $1}')
       if awk "BEGIN{exit !(${BAL:-0} < 0.004)}"; then solana transfer -u devnet --allow-unfunded-recipient -k ~/.config/solana/id.json "$B" 0.01 2>&1 | tail -1; fi
       sleep 12
       ( cd "$P2" && P2_NET=devnet ARENA=2 MATCHES=1 MODE=final INTERVAL_MS=11 RUN=p3-n-webgl node --experimental-strip-types tools/keeper/run.ts ) > "$LOG/d-keeper.log" 2>&1
       tail -1 "$LOG/d-keeper.log" | cut -c1-200
       wait $MP ;;
    e) bash scripts/m1-macserver.sh stop >/dev/null 2>&1; bash scripts/m1-macserver.sh start -fallrushBots 12 -fallrushMatchSize 12
       : > .m1-logs/macserver.log
       $M "$U?server=ws://127.0.0.1:7777&bot=match&onchainauto=0" "$LOG/e" 720 --shot=30 --play=585,492 --until=.m1-logs/macserver.log:COMPLETE_MATCH
       bash scripts/m1-macserver.sh stop ;;
    f) bash scripts/m1-macserver.sh stop >/dev/null 2>&1; bash scripts/m1-macserver.sh start -fallrushBots 12 -fallrushMatchSize 12 -fallrushOnchainFinal
       : > .m1-logs/macserver.log
       $M "$U?server=ws://127.0.0.1:7777&bot=match&onchainauto=600" "$LOG/f" 780 --shot=30 --play=585,492 --done="switch done" --until=.m1-logs/macserver.log:COMPLETE_MATCH
       bash scripts/m1-macserver.sh stop ;;
  esac
done
