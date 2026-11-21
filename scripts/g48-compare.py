#!/usr/bin/env python3
"""G4.8 proof sheets: per map and shot, G2/G3 (palette-*-before) -> G4.6 candy (palette-*-after) -> G4.8 soft (soft-*-soft)
side by side, labelled. usage: scripts/g48-compare.py <docs/proof/g4>"""
import os, sys
from PIL import Image, ImageDraw

d = sys.argv[1]
maps = ["capsule-run", "goo-tower", "star-fountain", "lid-flip", "lobby"]
for m in maps:
    for kind in ["spawn", "action", "overhead"]:
        srcs = [("G2/G3", f"palette-{m}-{kind}-before.png"), ("G4.6 candy", f"palette-{m}-{kind}-after.png"), ("G4.8 soft", f"soft-{m}-{kind}-soft.png")]
        ims = []
        for label, f in srcs:
            p = os.path.join(d, f)
            if not os.path.exists(p):
                continue
            im = Image.open(p).convert("RGB")
            w = 640
            h = round(im.height * w / im.width)
            im = im.resize((w, h))
            ImageDraw.Draw(im).rectangle([0, 0, 150, 22], fill=(42, 18, 80))
            ImageDraw.Draw(im).text((6, 5), label, fill=(255, 255, 255))
            ims.append(im)
        if len(ims) < 2:
            continue
        sheet = Image.new("RGB", (sum(i.width for i in ims) + 8 * (len(ims) - 1), max(i.height for i in ims)), (255, 255, 255))
        x = 0
        for im in ims:
            sheet.paste(im, (x, 0))
            x += im.width + 8
        out = os.path.join(d, f"soft-compare-{m}-{kind}.png")
        sheet.save(out, optimize=True)
        print(out)
