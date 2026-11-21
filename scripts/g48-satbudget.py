#!/usr/bin/env python3
"""G4.8 saturation budget: share of pixels with HSV saturation > 0.6 per PNG (budget: <= 30 % on spawn + action shots).
usage: scripts/g48-satbudget.py <png> [...]   -> a markdown table on stdout"""
import sys
from PIL import Image

def share(path, thr=0.6):
    im = Image.open(path).convert("RGB")
    im = im.resize((im.width // 2, im.height // 2))  # 640 x 360 is plenty for a share
    n = hi = 0
    for r, g, b in im.getdata():
        mx, mn = max(r, g, b), min(r, g, b)
        s = 0.0 if mx == 0 else (mx - mn) / mx
        n += 1
        hi += s > thr
    return hi / n

print("| shot | S > 0.6 | budget 30 % |\n|---|---|---|")
for p in sys.argv[1:]:
    v = share(p)
    print(f"| {p.split('/')[-1]} | {v * 100:.1f} % | {'ok' if v <= 0.30 else 'OVER'} |")
