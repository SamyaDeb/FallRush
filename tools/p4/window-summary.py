#!/usr/bin/env python3
"""P4 0.4: corrections per 5 s on devnet Finals (Mac player, auto-play) per input window, from the client's
[onchain] status lines (every ~5 s): mean / max of corr/5s while playing (tick > 0), plus late inputs, rejected,
confirm p95. Usage: tools/p4/window-summary.py docs/proof/p4/devnet-window"""
import json, os, re, sys
root = sys.argv[1]
rows = {}
for d in sorted(os.listdir(root)):
    p = os.path.join(root, d, 'client.txt')
    if not os.path.isfile(p):
        continue
    win = d.split('-')[1] if d.startswith('p4-') else d
    vals, late, rej, p95 = [], 0, 0, []
    for l in open(p):
        m = re.search(r'tick (\d+) .*corr/5s (\d+) corr (\d+) match (\d+) late (\d+).*confirm p50 (\d+) p95 (\d+).*rejected (\d+)', l)
        if not m or int(m.group(1)) == 0:
            continue
        vals.append(int(m.group(2))); late = int(m.group(5)); rej = int(m.group(8)); p95.append(int(m.group(7)))
    if not vals:
        continue
    r = {'run': d, 'windows': len(vals), 'corr5_mean': round(sum(vals) / len(vals), 2), 'corr5_max': max(vals), 'late': late, 'rejected': rej, 'confirm_p95_max': max(p95) if p95 else None}
    rows.setdefault(win, []).append(r)
out = {}
for w, rs in rows.items():
    n = sum(r['windows'] for r in rs)
    out[w] = {'runs': rs, 'corr5_mean_all': round(sum(r['corr5_mean'] * r['windows'] for r in rs) / max(1, n), 2), 'late_total': sum(r['late'] for r in rs)}
print(json.dumps(out, indent=1))
