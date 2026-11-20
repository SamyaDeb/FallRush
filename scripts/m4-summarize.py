#!/usr/bin/env python3
"""M4 §5.4: summarise a real-client match run (scripts/m4-match-run.sh): per client × round (map), over the 10 s
windows spent in RoundActive / Rush: corrections per 5 s (non-push), push corrections, hazard corrections, hazard-contact
mismatches, reactive changes that arrived after their effective tick, RTT, snapshot payload; plus the server's sim step p95.
usage: scripts/m4-summarize.py <run dir>"""
import re, sys, os, glob, collections

d = sys.argv[1]
rows = collections.OrderedDict()
num = lambda k, l, cast=float: cast(re.search(k + r"=(-?[0-9.]+)", l).group(1)) if re.search(k + r"=(-?[0-9.]+)", l) else None
for path in sorted(glob.glob(os.path.join(d, "probe-*.log"))):
    client = os.path.basename(path)[6:-4]
    lines = open(path, errors="replace").read().splitlines()
    last_rx_late = 0
    snap = None
    for i, l in enumerate(lines):
        if "[probe] snapHz=" in l:
            snap = l
        if "[probe] pred corrections=" not in l:
            continue
        m = re.search(r"m4 match=(\w+) round=(\d+) map=(\w+) slot=(\d+) out=(\w+)", l)
        if not m:
            continue
        phase, rnd, mp, out = m.group(1), int(m.group(2)), m.group(3), m.group(5) == "True"
        rx_late = num("rxLate", l, int) or 0
        new_late = rx_late - last_rx_late
        last_rx_late = rx_late
        if phase != "RoundActive" or " phase=Rush" not in l or out:
            continue
        frame = next((x for x in lines[i + 1:i + 4] if "[probe] frame" in x), "")
        key = (client, rnd, mp)
        r = rows.setdefault(key, dict(win=0, corr=0, push=0, haz=0, hazmis=0, late=0, rtt=[], rtt95=[], payload=[], maxc=0.0))
        r["win"] += 1
        r["corr"] += num("pred corrections", l, int) or 0
        r["push"] += num("pushCorrections", l, int) or 0
        r["haz"] += num("hazardCorr", l, int) or 0
        r["hazmis"] += num("hazMismatch", l, int) or 0
        r["late"] += max(0, new_late)
        r["maxc"] = max(r["maxc"], num("maxCorr", l) or 0)
        if frame:
            r["rtt"].append(num("ping p50", frame) or 0)
            r["rtt95"].append(num("p95", frame.split("ping")[1]) or 0)
        if snap:
            r["payload"].append(num("payloadIn", snap) or 0)
print(f"| Client | Round | Map | 10 s windows racing | Corrections / 5 s (non-push) | Push corr. / 5 s | Hazard corr. | Hazard mismatches | Reactive late | Max corr. (cm) | RTT p50 / p95 (ms) | Snapshot payload (B/s) |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|")
tot = collections.Counter()
for (c, rnd, mp), r in rows.items():
    w = max(1, r["win"])
    secs = w * 10
    rtt = sorted(r["rtt"]); r95 = sorted(r["rtt95"]); pay = sorted(r["payload"])
    med = lambda a: a[len(a) // 2] if a else float("nan")
    print(f"| {c} | {rnd} | {mp} | {r['win']} | {r['corr'] * 5 / secs:.2f} | {r['push'] * 5 / secs:.2f} | {r['haz']} | {r['hazmis']} | {r['late']} | {r['maxc']:.1f} | {med(rtt):.0f} / {med(r95):.0f} | {med(pay):.0f} |")
    for k in ("win", "corr", "push", "haz", "hazmis", "late"):
        tot[k] += r[k]
if tot["win"]:
    print(f"\nall: {tot['win']} windows, non-push corrections {tot['corr'] * 5 / (tot['win'] * 10):.2f} / 5 s, push {tot['push'] * 5 / (tot['win'] * 10):.2f} / 5 s, hazard mismatches {tot['hazmis']}, reactive late {tot['late']}")
srv = os.path.join(d, "server.log")
if os.path.exists(srv):
    p95 = [float(x) for x in re.findall(r"\[gs\] sim tick=\d+ humans=\d+ bots=\d+ step p50=[0-9.]+ms p95=([0-9.]+)ms", open(srv, errors="replace").read())]
    if p95:
        print(f"server sim step p95 (10 s windows): median {sorted(p95)[len(p95)//2]:.3f} ms, max {max(p95):.3f} ms over {len(p95)} windows")
    m = re.findall(r"\[gs\] match \d+ (COMPLETE|ABORT).*", open(srv, errors="replace").read())
    for line in re.findall(r"\[gs\] match \d+ (?:COMPLETE|ABORT)[^\n]*", open(srv, errors="replace").read()):
        print(line)
