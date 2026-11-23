#!/usr/bin/env bash
# P4 task 8.2 (locked): one WebGL release build (≤ 25 MB) + Chrome checks, served by the chain emulator (--static):
#   build  Build.WebGL -fallrushRelease (P4 client: economy UI, drop visuals, core v2)
#   a  ?rcselftest=1   browser parity (6 vector files incl. drops_v1)
#   d  ?onchain=devnet Final with the economy: the page's burner gets chips (no popup), the auto-play bets through the
#      in-game economy and buys 2 Chaos Drops (?autodrop=1); the keeper (this worktree, MODE=final, HUMANS=1) runs it
#   e  plain Classic match (Mac dev server, ?bot=match): regression
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p4-webgl -- bash scripts/p4-webgl.sh [build a d e]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
OUT=docs/proof/p4/webgl; LOG=.m1-logs/p4-webgl; mkdir -p "$OUT" "$LOG"
STEPS="${*:-build a d e}"
TSX=node_modules/.bin/tsx
M="node scripts/p3-webgl-measure.mjs"
U=http://localhost:8797/index.html
EMU=""
serve() { [[ -n "$EMU" ]] && return; node tools/chain-emulator/src/main.mjs --port 8797 --static client-unity/Builds/WebGL > "$LOG/emu.out" 2>&1 & EMU=$!; sleep 1; }
trap '[[ -n "$EMU" ]] && kill $EMU 2>/dev/null; bash scripts/m1-macserver.sh stop >/dev/null 2>&1' EXIT
for s in $STEPS; do
  echo "== $s"
  case $s in
    build) T0=$(date +%s); bash scripts/m1-unity.sh p4-webgl -buildTarget WebGL -executeMethod Build.WebGL -fallrushRelease
       echo "webgl build $(( $(date +%s) - T0 )) s, $(du -sk client-unity/Builds/WebGL | cut -f1) KB, $(find client-unity/Builds/WebGL -type f -exec stat -f %z {} + | awk '{s+=$1} END {print s}') B" | tee "$OUT/build.txt" ;;
    a) serve; $M "$U?rcselftest=1" "$LOG/a" 90 --shot=30; grep -E "rcselftest" "$LOG/a/console.log" | tail -8 | tee "$OUT/a-rcselftest.txt" ;;
    d) serve; PROF="$ROOT/.m1-logs/p4-webgl-chrome-devnet"
       $M "$U?onchain=devnet&onchainauto=300&hud=1&autodrop=1" "$LOG/d" 420 --shot=20 --profile="$PROF" &
       MP=$!
       for _ in $(seq 1 60); do B=$(grep -o "burner [1-9A-HJ-NP-Za-km-z]\{32,44\}" "$LOG/d/console.log" 2>/dev/null | head -1 | awk '{print $2}'); [[ -n "$B" ]] && break; sleep 1; done
       echo "browser burner $B"
       BAL=$(solana balance -u devnet "$B" 2>/dev/null | awk '{print $1}')
       if awk "BEGIN{exit !(${BAL:-0} < 0.02)}"; then solana transfer -u devnet --allow-unfunded-recipient -k ~/.config/solana/id.json "$B" 0.025 2>&1 | tail -1; fi
       sleep 40
       env P2_NET=devnet ARENA=2 MATCHES=1 MODE=final INTERVAL_MS=11 HUMANS=1 WAIT_HUMANS_S=60 RUN=p4-webgl-d $TSX tools/keeper/run.ts > "$LOG/d-keeper.log" 2>&1
       tail -1 "$LOG/d-keeper.log" | cut -c1-300
       wait $MP
       grep -E "auto-bet|auto-drop|econ|chips|\[frc\]" "$LOG/d/console.log" | tail -20 | tee "$OUT/d-economy.txt" ;;
    e) bash scripts/m1-macserver.sh stop >/dev/null 2>&1; bash scripts/m1-macserver.sh start -fallrushBots 12 -fallrushMatchSize 12
       : > .m1-logs/macserver.log; serve
       $M "$U?server=ws://127.0.0.1:7777&bot=match&onchainauto=0" "$LOG/e" 720 --shot=30 --play=585,492 --until=.m1-logs/macserver.log:COMPLETE_MATCH
       grep -c COMPLETE_MATCH .m1-logs/macserver.log | tee "$OUT/e-classic.txt"
       bash scripts/m1-macserver.sh stop ;;
  esac
done
cp -R "$LOG"/*/*.jpg "$LOG"/*/*.png "$OUT/" 2>/dev/null
