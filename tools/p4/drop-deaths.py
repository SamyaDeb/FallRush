#!/usr/bin/env python3
"""P4 task 7.4 (feel, automated): from a keeper World recording (docs/proof/p2/runs/<run>-m1/world.bin: [u32 tick]
[u8 part][u16 len][World bytes]) — every Chaos Drop (queue at World offset 1720: 8 × {tick u32, kind, arg}), the tick it
first appeared (telegraph = trigger − that tick), and every elimination (SlotRules OUT bit at player + 46); counts
deaths during a drop's effect or within 1 s (30 ticks) after it. Usage: drop-deaths.py <world.bin> [...]"""
import json, struct, sys
DUR = {1: 135, 2: 1, 3: 60, 4: 90}
NAMES = {1: 'LID_FRENZY', 2: 'BUMPER_RAIN', 3: 'WIND_GUST', 4: 'SWEEPER_RUSH'}
out = []
for path in sys.argv[1:]:
    b = open(path, 'rb').read()
    o, seen, drops, outs, prev = 0, {}, {}, [], None
    while o + 7 <= len(b):
        tick, part, ln = struct.unpack_from('<IBH', b, o); o += 7
        w = b[o:o + ln]; o += ln
        if part != 0 or ln < 1768:
            continue
        for i in range(8):
            t, k, a = struct.unpack_from('<IBB', w, 1720 + 6 * i)
            if k and i not in seen:
                seen[i] = tick; drops[i] = {'kind': NAMES.get(k, k), 'trigger': t, 'seen_tick': tick, 'telegraph_ticks': t - tick}
        flags = [w[22 + 115 * s + 46] & 2 for s in range(12)]
        if prev is not None:
            for s in range(12):
                if flags[s] and not prev[s]:
                    outs.append((s, tick))
        prev = flags
    near = []
    for s, t in outs:
        for d in drops.values():
            k = [kk for kk, n in NAMES.items() if n == d['kind']][0]
            if d['trigger'] <= t <= d['trigger'] + DUR[k] + 30:
                near.append({'slot': s, 'tick': t, 'drop': d['kind']})
    out.append({'file': path, 'drops': list(drops.values()), 'eliminations': [{'slot': s, 'tick': t} for s, t in outs], 'deaths_during_or_1s_after_a_drop': near})
print(json.dumps(out, indent=1))
