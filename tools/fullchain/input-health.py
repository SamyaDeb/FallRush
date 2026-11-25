#!/usr/bin/env python3
"""Input health of a full on-chain match from its world2.bin archive (input run, 2026-11-25).

For every human slot and round: the share of RUSH ticks whose input the arena applied (WorldAcc.cur_some bit set
for the tick), the gaps (consecutive ticks without an input: the arena repeated the slot's last command, so the
player moved on-chain with a stale input), and the hazard hits (LastStep & HAZARD_HIT).

  python3 tools/fullchain/input-health.py <world2.bin | match pubkey> [--base https://fallrush.fun]

world2.bin record: [u32 tick][u8 part][u16 len][80 B WorldAcc header @128..208][World bytes]; header: round @11,
cur_some u16 @64, serial @66, input_lead @70 (0 = 8), round_phase @71 (3 = RUSH). World: players @22, stride 115,
slot flags @+33 (1 active, 2 bot), last step @+35.
"""
import argparse, collections, os, struct, sys, urllib.request

PO, PS = 22, 115


def load(src, base):
    if os.path.exists(src):
        return open(src, 'rb').read()
    url = f"{base.rstrip('/')}/api/replay/{src}/world2.bin"
    req = urllib.request.Request(url, headers={'User-Agent': 'fallrush-input-health'})
    return urllib.request.urlopen(req, timeout=120).read()


def parse(b):
    recs, o, serial0 = {}, 0, None
    while o + 87 <= len(b):
        tick, part, ln = struct.unpack_from('<IBH', b, o)
        hdr, w = b[o + 7:o + 87], b[o + 87:o + 87 + ln]
        if len(w) < ln:
            break
        o += 87 + ln
        ser = hdr[66:70]
        if serial0 is None:
            serial0 = ser
        if ser != serial0:
            break  # the archive's next match
        if part == 0:
            recs[tick] = (hdr, w)
    return recs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('src')
    ap.add_argument('--base', default='https://fallrush.fun')
    ap.add_argument('--gap', type=int, default=8, help='report gaps at least this long (ticks)')
    a = ap.parse_args()
    recs = parse(load(a.src, a.base))
    if not recs:
        print('no complete ticks'); return 1
    ticks = sorted(recs)
    mid = recs[ticks[len(ticks) // 2]][1]
    humans = [s for s in range(12) if mid[PO + s * PS + 33] & 1 and not mid[PO + s * PS + 33] & 2]
    lead = recs[ticks[-1]][0][70]
    print(f"ticks {ticks[0]}..{ticks[-1]} ({len(ticks)} complete)  input window {lead or 8}  humans {humans}")
    for h in humans:
        rounds = collections.OrderedDict()
        run = 0
        for t in ticks:
            hdr, w = recs[t]
            if hdr[71] != 3:
                continue
            rnd = hdr[11]
            r = rounds.setdefault(rnd, {'n': 0, 'miss': 0, 'hits': 0, 'gaps': [], 'tail': 0})
            some = struct.unpack_from('<H', hdr, 64)[0] >> h & 1
            r['n'] += 1
            if not some:
                r['miss'] += 1
                run += 1
            else:
                if run >= a.gap:
                    r['gaps'].append(run)
                run = 0
            if w[PO + h * PS + 35] & 1:
                r['hits'] += 1
        for rnd, r in rounds.items():
            pct = 100 * (r['n'] - r['miss']) / max(1, r['n'])
            note = '  (no inputs at all: out of the round / left)' if r['miss'] == r['n'] else ''
            g = sorted(r['gaps'])
            print(f"slot {h} round {rnd}: inputs applied {pct:5.1f}%  missing {r['miss']}/{r['n']}  gaps>={a.gap}: {len(g)} longest {g[-3:] if g else '-'}  hits {r['hits']}{note}")
    return 0


if __name__ == '__main__':
    sys.exit(main())
