#!/usr/bin/env bash
# G4.7 QA, all four maps, ONE heavy job at a time (8 GB M2 Air): every step runs in sequence, never in parallel.
#
# Wall time estimate (from the G4.2–G4.5 proofs, Mac dev server, M2 Air): ≈ 85 min in total
#   routes      Capsule Run 100 (128 s) + Goo Tower 100 (45 s)                                     ≈  3 min
#   g1-qa 100   4 maps: Capsule Run normal tier (≈ 340 s, half of the 675 s both-tier run), Goo 45 s,
#               Star Fountain ≈ 250 s, Lid Flip 151 s                                              ≈ 13 min
#   m4-qa 100   the same binary and arguments again (= the determinism pair)                       ≈ 13 min
#   camp        Star Fountain 100 runs + the camper probe (-fallrushQaCamp; 329 s in G4.4)         ≈  6 min
#   soak 20     full 4-round 12-bot matches at sim speed 8 (1 170 s in G4.2)                       ≈ 20 min
#   probes      4 maps × (localhost + 150 ms) × (150 s + server start/stop + 3 clients 2 s apart)  ≈ 25 min
#               + Docker Desktop start ≈ 1 min, netem relay start ≈ 0.5 min
#
# Steps (G47_STEPS, comma list; default all, in this order):
#   routes       scripts/g4-route-qa.sh 100 on BlockPartyDash and GooTower
#   g1           scripts/g1-qa.sh 100 (all four maps; tier both, Capsule Run normal)
#   m4           scripts/m4-qa.sh 100 (all four maps; same seeds → the determinism check)
#   camp         Star Fountain 100 runs with the camper probe (the dev-server QA binary, as g1-qa.sh + -fallrushQaCamp)
#   soak         scripts/m4-soak.sh 20
#   probe-local  the prediction probe per map on localhost (Docker off)
#   probe-150    the prediction probe per map through the 150 ms netem relay (Docker on only for these legs)
#   summary      scripts/g47-summarize.py → docs/proof/g4/g47-qa-summary.md
# The probe legs are the g42…g45-probe.sh legs, command for command (m1-macserver.sh start -fallrushMap <key>
# -fallrushBump 0; m3-gate.sh <url> 150 <label> -fallrushBump 0; stop), grouped so all localhost legs run with
# Docker off and Docker Desktop runs only for the four 150 ms legs (then it is quit again if this script started it).
#
# Writes docs/proof/g4/g47-qa-*.md:
#   g47-qa-routes-<map>.md, g47-qa-g1-<map>.md, g47-qa-m4-<map>.md, g47-qa-camp-StarScramble.md, g47-qa-soak.md,
#   g47-qa-probe-<map>.md, g47-qa-run.md (what ran, when, how long), g47-qa-summary.md (the table)
# Side effects: g1-qa.sh / m4-qa.sh rewrite docs/proof/g1-map-qa.md and docs/proof/m4-map-qa.md (as in every G4.x run);
# raw logs (probe clients, the dev server per probe leg, QA logs) stay in .m1-logs/ (ignored).
#
# usage: scripts/g47-qa-all.sh                     # everything (≈ 85 min)
#        G47_STEPS=probe-local,probe-150,summary scripts/g47-qa-all.sh
#        G47_RUNS=100 G47_SOAK=20 G47_PROBE_SECS=150 (defaults)
# Refuses to start if a Unity process, a dev server / QA binary or Docker is already running (one heavy job at a time).
# LOCAL ONLY: no VPS, no SSH, nothing pushed.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
RUNS="${G47_RUNS:-100}"; SOAK="${G47_SOAK:-20}"; SECS="${G47_PROBE_SECS:-150}"
STEPS=",${G47_STEPS:-routes,g1,m4,camp,soak,probe-local,probe-150,summary},"
MAPS=(BlockPartyDash GooTower StarScramble CrumbleShelf)
PROOF="$ROOT/docs/proof/g4"; LOGS="$ROOT/.m1-logs/g47"; mkdir -p "$PROOF" "$LOGS"
RUNMD="$PROOF/g47-qa-run.md"
BIN="$(ls "$ROOT/client-unity/Builds/MacDevServer/FallRushDevServer.app/Contents/MacOS/"* 2>/dev/null | head -1 || true)"
T0=$(date +%s)
STARTED_DOCKER=0

say() { echo "[g47 $(date -u +%H:%M:%SZ)] $*" | tee -a "$LOGS/qa-all.log"; }
want() { [[ "$STEPS" == *",$1,"* ]]; }
row() { echo "| $1 | $2 | $3 | $4 |" >> "$RUNMD"; }   # step | started (UTC) | wall | result

docker_up() { docker info >/dev/null 2>&1; }

guard() { # one heavy job at a time: no Unity, no other dev server / QA binary; Docker only where allowed
  if pgrep -x Unity >/dev/null; then say "ABORT: a Unity process is running (pgrep -x Unity). Close it first."; exit 3; fi
  if pgrep -f "FallRushDevServer.app/Contents/MacOS" >/dev/null; then say "ABORT: a FallRushDevServer process is already running (dev server / QA / soak)."; exit 3; fi
  if [[ "${1:-}" != docker-ok ]] && docker_up; then say "ABORT: Docker is running. Quit Docker Desktop first (this script starts it for the 150 ms legs only)."; exit 3; fi
}

step() { # <name> <command...>: runs one step, records its wall time; G47_RESUME=1 skips steps already in $LOGS/done
  local name="$1"; shift
  if [[ -n "${G47_RESUME:-}" ]] && grep -qx "$name" "$LOGS/done" 2>/dev/null; then say "step $name: done earlier, skipped (G47_RESUME)"; return 0; fi
  guard "${DOCKER_OK:-}"
  local s=$(date +%s) at=$(date -u +%H:%M:%SZ)
  say "step $name: start"
  "$@"; local code=$?
  local w=$(( $(date +%s) - s ))
  say "step $name: exit $code in ${w} s"
  row "$name" "$at" "${w} s" "exit $code"
  echo "$name" >> "$LOGS/done"
}

fence() { echo '```text'; cat; echo '```'; }

# ------------------------------------------------------------------ preflight
[[ -x "$BIN" ]] || { echo "build the Mac dev server first (Editor closed): pnpm m1:build:macserver"; exit 2; }
guard
[[ -n "${G47_RESUME:-}" ]] || : > "$LOGS/done"
[[ -f "$RUNMD" && -n "${G47_RESUME:-}" ]] || : > "$RUNMD"
{
  echo "# G4.7 QA run (scripts/g47-qa-all.sh)"
  echo
  echo "- Started $(date -u +%Y-%m-%dT%H:%MZ); HEAD \`$(git rev-parse --short HEAD)\`$(git diff --quiet -- client-unity/Assets || echo ' (+ uncommitted changes under client-unity/Assets)')"
  echo "- Dev server binary: \`${BIN#$ROOT/}\`, built $(date -u -r "$BIN" +%Y-%m-%dT%H:%MZ)"
  echo "- Runs per cell $RUNS, soak $SOAK matches, probe $SECS s per leg; steps:${STEPS//,/ }"
  echo
  echo "| Step | Started (UTC) | Wall | Result |"
  echo "|---|---|---|---|"
} >> "$RUNMD"

# ------------------------------------------------------------------ steps
routes() {
  for m in BlockPartyDash GooTower; do
    bash scripts/g4-route-qa.sh "$RUNS" "$m" "$PROOF/g47-qa-routes-$m.md"
  done
}

qa_pair() { # <g1|m4>
  local kind="$1"
  bash "scripts/$kind-qa.sh" "$RUNS"
  for m in "${MAPS[@]}"; do
    if [[ -f ".m1-logs/$kind/qa-$m.md" ]]; then cp ".m1-logs/$kind/qa-$m.md" "$PROOF/g47-qa-$kind-$m.md"
    else echo "[g47] no .m1-logs/$kind/qa-$m.md (the QA binary failed: see .m1-logs/$kind/qa-$m.log)"; fi
  done
}

camp() { # as g1-qa.sh for one map, plus the camper probe (G4.4 §3.1)
  "$BIN" -batchmode -nographics -fallrushQA "$RUNS" -fallrushQATier both -fallrushMap StarScramble -fallrushQaCamp \
    -fallrushQAOut "$PROOF/g47-qa-camp-StarScramble.md" -logFile "$LOGS/qa-camp-StarScramble.log" > /dev/null 2>&1 || true
  grep -o 'Camper probe [A-Z]*\|QA [A-Z]*' "$PROOF/g47-qa-camp-StarScramble.md" 2>/dev/null | tail -2
}

soak() {
  { echo "# G4.7 soak (scripts/m4-soak.sh $SOAK, $(date -u +%Y-%m-%dT%H:%MZ))"; echo
    bash scripts/m4-soak.sh "$SOAK" 2>&1 | fence
    echo; echo "Per match (server log):"; grep -E "\[gs\] soak (match|done)" .m1-logs/m4/soak/server.log | awk '!seen[$0]++' | fence
  } > "$PROOF/g47-qa-soak.md"
  grep -E "soak done|logs valid|RSS" "$PROOF/g47-qa-soak.md"
}

probe_leg() { # <map> <local|150> <url>: one g4x-probe.sh leg
  local m="$1" leg="$2" url="$3" label="g47-$1-$2"
  bash scripts/m1-macserver.sh stop >/dev/null 2>&1; : > .m1-logs/macserver.log
  if ! bash scripts/m1-macserver.sh start -fallrushMap "$m" -fallrushBump 0 >/dev/null; then
    echo "$label: dev server did not start (see .m1-logs/macserver.log)"; return 1
  fi
  bash scripts/m3-gate.sh "$url" "$SECS" "$label" -fallrushBump 0 2>&1 || true
  bash scripts/m1-macserver.sh stop >/dev/null 2>&1
  cp .m1-logs/macserver.log "$LOGS/server-$label.log"   # C3: the server's late / repeated / neutral input counters per 10 s
  for n in 1 2 3; do [[ -f ".m1-logs/probe-$label-$n.log" ]] && cp ".m1-logs/probe-$label-$n.log" "$LOGS/"; done
}

probe_local() {
  for m in "${MAPS[@]}"; do
    { echo "# G4.7 prediction probe: $m (3 probe clients (BotBrain on the predicted state) + 9 server bots, bump off, $SECS s per leg)"; echo
      echo "## localhost ($(date -u +%Y-%m-%dT%H:%MZ))"; probe_leg "$m" local ws://127.0.0.1:7777 | fence
    } > "$PROOF/g47-qa-probe-$m.md"
    grep -E "^g47-$m-local-[0-9]:" "$PROOF/g47-qa-probe-$m.md" | cut -c1-120
  done
}

probe_150() {
  if ! docker_up; then
    say "starting Docker Desktop for the 150 ms legs"; open -a Docker; STARTED_DOCKER=1
    for _ in $(seq 1 90); do docker_up && break; sleep 2; done
    docker_up || { say "Docker did not come up in 180 s; 150 ms legs skipped"; return 1; }
  fi
  bash scripts/m2-netem.sh start 75 15 2 | tee -a "$LOGS/qa-all.log"
  for m in "${MAPS[@]}"; do
    [[ -n "$(bash scripts/m2-netem.sh status)" ]] || bash scripts/m2-netem.sh start 75 15 2 | tee -a "$LOGS/qa-all.log"
    { echo; echo "## 150 ms ± 15 + 2 % loss ($(date -u +%Y-%m-%dT%H:%MZ), local Docker netem relay ws://127.0.0.1:7778)"
      probe_leg "$m" 150 ws://127.0.0.1:7778 | fence
    } >> "$PROOF/g47-qa-probe-$m.md"
    grep -E "^g47-$m-150-[0-9]:" "$PROOF/g47-qa-probe-$m.md" | cut -c1-120
  done
  bash scripts/m2-netem.sh stop
  if [[ "$STARTED_DOCKER" == 1 && -z "${G47_KEEP_DOCKER:-}" ]]; then
    say "quitting Docker Desktop"
    osascript -e 'quit app "Docker Desktop"' >/dev/null 2>&1 || osascript -e 'quit app "Docker"' >/dev/null 2>&1 || true
    for _ in $(seq 1 45); do docker_up || break; sleep 2; done
    docker_up && say "WARNING: Docker is still up after the quit request; quit Docker Desktop by hand"
  fi
}

summary() {
  python3 scripts/g47-summarize.py > "$PROOF/g47-qa-summary.md"; local code=$?
  tail -3 "$PROOF/g47-qa-summary.md"
  return $code
}

want routes      && step routes routes
want g1          && step g1-qa qa_pair g1
want m4          && step m4-qa qa_pair m4
want camp        && step camp camp
want soak        && step soak soak
want probe-local && step probe-local probe_local
want probe-150   && DOCKER_OK=docker-ok step probe-150 probe_150
want summary     && DOCKER_OK=docker-ok step summary summary
bash scripts/m1-macserver.sh stop >/dev/null 2>&1 || true

echo >> "$RUNMD"
echo "- Finished $(date -u +%Y-%m-%dT%H:%MZ), total $(( ($(date +%s) - T0) / 60 )) min. Summary: \`docs/proof/g4/g47-qa-summary.md\`." >> "$RUNMD"
say "done in $(( ($(date +%s) - T0) / 60 )) min → docs/proof/g4/g47-qa-summary.md"
