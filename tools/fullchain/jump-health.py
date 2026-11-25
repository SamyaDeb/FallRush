#!/usr/bin/env python3
"""Jump health of a full on-chain match from its world2.bin archive (jump run, 2026-11-25).

For every human slot: each latched Space press (JUMP_PRESSED in the inputs the arena used) and whether the motor
jumped (else why: stunned, dive landing, diving, airborne = buffered), plus Space holds (JUMP_HELD runs) that reached
the arena WITHOUT a press — a press lost before the chain (client side) — and whether a missing tick was near.

  python3 tools/fullchain/jump-health.py <match pubkey>
"""
import struct, sys, collections, urllib.request
PO, PS = 22, 115
src = sys.argv[1]
req = urllib.request.Request(f"https://fallrush.fun/api/replay/{src}/world2.bin", headers={'User-Agent': 'fallrush-jumps'})
b = urllib.request.urlopen(req, timeout=180).read()
o = 0; recs = {}; ser0 = None
while o + 87 <= len(b):
    t, p, ln = struct.unpack_from('<IBH', b, o); h = b[o+7:o+87]; w = b[o+87:o+87+ln]
    if len(w) < ln: break
    o += 87 + ln
    if ser0 is None: ser0 = h[66:70]
    if h[66:70] != ser0: break
    if p == 0: recs[t] = (h, w)
ticks = sorted(recs)
mid = recs[ticks[len(ticks)//2]][1]
hs = [s for s in range(12) if mid[PO+s*PS+33] & 1 and not mid[PO+s*PS+33] & 2]
def st(w, s):
    q = PO + s*PS
    vx, vy, vz = struct.unpack_from('<iii', w, q+12)
    return dict(vy=vy, flags=w[q+25], stun=w[q+26], coyote=w[q+27], buf=w[q+28], land=w[q+30], y=struct.unpack_from('<i', w, q+4)[0])
for h in hs:
    res = collections.Counter(); presses = 0; heldmiss = 0; detail = []
    prev = None
    for t in ticks:
        hdr, w = recs[t]
        if hdr[71] != 3: prev = None; continue
        if prev is None or prev[0] != t - 1: prev = (t, w); continue
        some = struct.unpack_from('<H', hdr, 64)[0] >> h & 1
        btn = hdr[16 + 4*h + 2] if some else None
        a, z = st(prev[1], h), st(w, h)
        if some and btn & 2:
            presses += 1
            jumped = z['vy'] > 3000 and not (z['flags'] & 1)
            if jumped: why = 'jumped'
            elif a['stun'] > 0: why = 'stunned'
            elif a['flags'] & 4 and a['land'] > 0: why = 'dive-landing'
            elif a['flags'] & 2: why = 'diving'
            elif not (a['flags'] & 1) and a['coyote'] == 0: why = 'airborne (buffered)' if z['buf'] > 0 else 'airborne'
            else: why = 'other'
            res[why] += 1
            if why in ('other',) and len(detail) < 6: detail.append((t, a, z))
        prev = (t, w)
    # buffered presses that later turned into jumps: count airborne presses followed by a jump within buffer window
    print(f"{src[:8]} slot {h}: jump presses latched {presses}: " + ', '.join(f"{k} {v}" for k, v in res.most_common()))
    for d in detail: print('   other:', d)
# held runs (JUMP_HELD) whose start has no JUMP_PRESSED within 2 ticks = a press lost before the chain
for h in hs:
    rt = [t for t in ticks if recs[t][0][71] == 3]
    held = {}; pressed = set(); missing = set()
    for t in rt:
        hdr = recs[t][0]
        if struct.unpack_from('<H', hdr, 64)[0] >> h & 1:
            bt = hdr[16 + 4*h + 2]; held[t] = bool(bt & 1)
            if bt & 2: pressed.add(t)
        else:
            missing.add(t)
    runs = []; cur = None
    for t in rt:
        if held.get(t):
            if cur is None: cur = t
        else:
            if cur is not None: runs.append((cur, t - 1)); cur = None
    lost = []; ok = 0
    for s, e in runs:
        if any(x in pressed for x in range(s - 2, s + 2)): ok += 1
        else: lost.append((s, e - s + 1, 'missing-before' if any(x in missing for x in range(s - 3, s + 1)) else 'no-missing'))
    print(f"   held runs {len(runs)}: with a press {ok}, WITHOUT a press {len(lost)} -> {lost[:12]}")
    # presses: are they always at the start of a held run?
    starts = set(s for s, e in runs)
    stray = [t for t in pressed if not any(abs(t - s) <= 1 for s in starts)]
    print(f"   presses not at a held-run start: {len(stray)} {sorted(stray)[:10]}")
