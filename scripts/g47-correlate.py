#!/usr/bin/env python3
"""G4.7 (C3 / C4): line up each probe client's 10 s windows with the dev server's 10 s input / push counters.

For every [probe] window: the server tick (from the snapHz line just before it), non-push / push corrections, max
correction so far, phase, position; next to it the server's counters for the window that covers that tick: late
(input arrived after its tick was simulated), repeated / neutral (ticks the server filled because no command had
arrived: all humans together; per slot from fillsBySlot when the log has it), burst / backlog (TCP catch-up), pushes (all players).

usage: scripts/g47-correlate.py <server.log> <probe-client.log> [more probe logs...]
"""
import re, sys

def kv(line, k):
    m = re.search(r"(?:^|\s)" + re.escape(k) + r"=([-\w.()/,]+)", line)
    return m.group(1) if m else None

srv = []  # (tick, dict)
prev = {}
for line in open(sys.argv[1], errors="replace"):
    if line.startswith("[gs] sim tick="):
        t = int(kv(line, "tick"))
        cur = {k: int(kv(line, k) or 0) for k in ("late", "repeated", "neutral", "burst", "backlog")}
        pt = kv(line, "pushesTotal")
        cur["pushesTotal"] = int(pt) if pt else 0
        d = dict(cur)
        d["pushes"] = cur["pushesTotal"] - prev.get("pushesTotal", 0)
        fb = re.search(r"fillsBySlot=((?:\d+:\d+/\d+/\d+ )*)", line)
        cur["slots"] = {int(a): tuple(map(int, b.split("/"))) for a, b in (x.split(":") for x in fb.group(1).split())} if fb else {}
        d["slots"] = {k: tuple(v - w for v, w in zip(cur["slots"][k], prev.get("slots", {}).get(k, (0, 0, 0)))) for k in cur["slots"]}
        prev = cur
        srv.append((t, d))

def srv_at(tick):
    for t, d in srv:
        if t >= tick:
            return t, d
    return None, None

for path in sys.argv[2:]:
    print(f"== {path}")
    print("serverTick | corr | push | maxCorr | phase | pos | srv window ≤ tick | late | repeated | neutral | burst | backlog | pushes | own slot late/rep/neu")
    tick = None; slot = None
    for line in open(path, errors="replace"):
        if line.startswith("[probe] snapHz"):
            tick = int(kv(line, "serverTick") or 0); slot = int(kv(line, "slot") or -1)
        elif line.startswith("[probe] pred") and tick:
            c, pc = int(kv(line, "corrections")), int(kv(line, "pushCorrections"))
            ph = kv(line, "phase")
            if ph != "Rush" and c == 0:
                continue
            st, d = srv_at(tick)
            mark = " <==" if c else ""
            ds = f"{st} | {d['late']} | {d['repeated']} | {d['neutral']} | {d['burst']} | {d['backlog']} | {d['pushes']} | {'/'.join(map(str, d['slots'].get(slot, ('-',)))) if d['slots'] else '-'}" if d else "- | | | | | |"
            print(f"{tick} | {c} | {pc} | {kv(line, 'maxCorr')} | {ph} | {kv(line, 'pos')} | {ds}{mark}")
