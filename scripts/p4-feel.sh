#!/usr/bin/env bash
# P4 (optional, for you): play 2 on-chain Finals on devnet with Chaos Drops. The Mac player (no auto-play: you play,
# WASD + mouse, space jump), 2 bots; a bundle spectator buys Lid Frenzy, Wind Gust and Bumper Rain during each Final;
# the input window is +12. Tell Claude how the drops felt (fair? readable? too much?); the run lands in
# docs/proof/p4/devnet-window/p4-feel-user-*/ (client log, drop telegraphs seen, keeper summary).
#   bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p4-feel -- bash scripts/p4-feel.sh
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
HUMAN=1 DROPS=1 DROP_KINDS=1,3,2 WINDOW=12 LEAD=12 bash scripts/p4-devnet-final.sh 2 feel-user
