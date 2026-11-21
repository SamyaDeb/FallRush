#!/usr/bin/env python3
"""G4.7: one table, map x target x measured x result, from the QA reports.

Reads the reports `scripts/g47-qa-all.sh` writes (docs/proof/g4/g47-qa-*.md) and checks them against the targets of the
G4.x proofs:
  Capsule Run   (G4.2 §2-3): normal median 85-110 s, 8 qualify in every cell, 0 free routes, route ratios <= 1.25
  Goo Tower     (G4.3 §3-4, G4.4-0): goo margin 8-10 s on every route index, normal median 50-65 s, 5 of 8 escape
  Star Fountain (G4.4 §3): exactly 3 qualify, >= 60 % pickups on the table, drops 1-4 (normal) / <= 2 (reference),
                golden >= 50 %, reference spring -> island >= 70 %, 3rd-4th within 2 pts >= 40 %, reference falls <= 2,
                the 60 s timer every run, camper probe (every spot below the 3rd-place median)
  Lid Flip      (G4.5 §2): normal median 60-100 s, p90 < 115 s, timer <= 5 %, last pair same tick <= 2 %, every lid
                fall fair, second chance >= 70 %, lower-shelf stay >= 8 s, inner floor >= 5 (every tier), one champion
  every map:    valid 100 %, 0 soft-locks, deterministic (the report's own check + g1-qa == m4-qa byte for byte apart
                from the wall time), prediction <= 0.4 non-push corrections / 5 s in a round, 0 hazard mismatches,
                3 / 3 probe clients connected; soak N / N complete and logs valid.

Results: PASS, FAIL, ACCEPTED (missed, but a G4.x proof accepted exactly this; the note says which), INFO (reported,
no target), MISSING (report not found / not parsed).

usage: scripts/g47-summarize.py [--dir docs/proof/g4] [--legacy] [--soak-log PATH]
  --legacy   parse the G4.2-G4.5 proof reports instead (route-cost-after.md, goo-qa-100.md, ..., g4x-probe-raw.txt) to
             prove the parser; the soak then comes from .m1-logs/m4/soak/server.log (the last soak's server log).
exit 0 = no FAIL and no MISSING.
"""
import argparse, os, re, statistics, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
MAPS = ["BlockPartyDash", "GooTower", "StarScramble", "CrumbleShelf"]
# The four G4 map content hashes (Tests/Vectors/map_hashes_v1.txt; G4.6 is render only and must not change them).
HASHES = {"BlockPartyDash": "50125d89", "GooTower": "5d5a00be", "StarScramble": "0bde7d75", "CrumbleShelf": "b0684a98"}
NAMES = {"BlockPartyDash": "Capsule Run", "GooTower": "Goo Tower", "StarScramble": "Star Fountain", "CrumbleShelf": "Lid Flip"}

G47_FILES = {
    "routes": {m: f"g47-qa-routes-{m}.md" for m in ("BlockPartyDash", "GooTower")},
    "qa": {m: f"g47-qa-g1-{m}.md" for m in MAPS},
    "qa2": {m: f"g47-qa-m4-{m}.md" for m in MAPS},
    "camp": {"StarScramble": "g47-qa-camp-StarScramble.md"},
    "probe": {m: f"g47-qa-probe-{m}.md" for m in MAPS},
    "soak": "g47-qa-soak.md",
}
LEGACY_FILES = {
    "routes": {"BlockPartyDash": "route-cost-after.md", "GooTower": "goo-route-cost-after.md"},
    "qa": {"BlockPartyDash": "race-qa-both-100.md", "GooTower": "goo-qa-100.md", "StarScramble": "star-fountain-qa-100.md",
           "CrumbleShelf": "lid-flip-qa-100.md"},
    "qa2": {"CrumbleShelf": "lid-flip-qa-m4-100.md"},
    "camp": {"StarScramble": "star-fountain-qa-100.md"},
    "probe": {"BlockPartyDash": "g42-probe-150-raw.txt", "GooTower": "g43-probe-raw.txt", "StarScramble": "g44-probe-raw.txt",
              "CrumbleShelf": "g45-probe-raw.txt"},
    "soak": None,
}

rows = []  # (map, target, measured, result, note)


def add(m, target, measured, result, note=""):
    rows.append((NAMES.get(m, m), target, measured, result, note))


def ok(cond):
    return "PASS" if cond else "FAIL"


def read(path):
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except OSError:
        return None


# ---------------------------------------------------------------- markdown tables

def tables(text):
    """Every markdown table: (header cells, [row dicts]). A table = a '|' line, a '|---' line, then '|' rows."""
    out, lines, i = [], text.splitlines(), 0
    while i < len(lines) - 1:
        a, b = lines[i].strip(), lines[i + 1].strip()
        if a.startswith("|") and b.startswith("|---"):
            head = [c.strip() for c in a.strip("|").split("|")]
            body, j = [], i + 2
            while j < len(lines) and lines[j].strip().startswith("|"):
                cells = [c.strip() for c in lines[j].strip().strip("|").split("|")]
                body.append(dict(zip(head, cells)))
                j += 1
            out.append((head, body))
            i = j
        else:
            i += 1
    return out


def table_with(text, *keys):
    for head, body in tables(text or ""):
        if all(any(k in h for h in head) for k in keys):
            return head, body
    return None, []


def col(row, key):
    for h, v in row.items():
        if key in h:
            return v
    return ""


def num(s, idx=0):
    v = re.findall(r"-?\d+(?:\.\d+)?", (s or "").replace("**", ""))
    return float(v[idx]) if len(v) > idx else None


def pct(s):
    """'85 %' or '254/300 (85 %)' -> 85.0 (the percentage), or None."""
    m = re.search(r"(-?\d+(?:\.\d+)?)\s*%", s or "")
    return float(m.group(1)) if m else None


def rng(vals, fmt="{:.1f}"):
    vals = [v for v in vals if v is not None]
    if not vals:
        return "—"
    lo, hi = min(vals), max(vals)
    return fmt.format(lo) if lo == hi else f"{fmt.format(lo)}–{fmt.format(hi)}"


def bold_value(text, label):
    """'- Free routes (no hazard contact): **0**' -> '0'."""
    m = re.search(re.escape(label) + r"[^*\n]*\*\*([^*]+)\*\*", text or "")
    return m.group(1).strip() if m else None


# ---------------------------------------------------------------- per-report checks

def main_table(m, text):
    head, body = table_with(text, "Soft-locks", "Qualified")
    return body


def check_common(m, text, text2, name2):
    body = main_table(m, text)
    if not body:
        add(m, "QA report", "not found / no table", "MISSING")
        return []
    hdr = re.search(r"### Map QA: (\S+) \((\w+), map hash (\w+)\), (\d+) runs", text)
    if hdr:
        add(m, "map hash = the G4 hash (map_hashes_v1)", f"{hdr.group(3)} · {hdr.group(4)} runs per cell", ok(hdr.group(3) == HASHES[m]),
            f"{len(body)} cells, mode {hdr.group(2)}; expected {HASHES[m]}")
    valid = [col(r, "Valid") for r in body]
    allvalid = all(re.match(r"(\d+)/\1$", v) for v in valid)
    add(m, "valid result every run", f"{sum(1 for v in valid if re.match(r'(\d+)/\1$', v))}/{len(valid)} cells 100 %", ok(allvalid))
    sl = [num(col(r, "Soft-locks")) for r in body]
    add(m, "0 soft-locks", f"{int(sum(s or 0 for s in sl))} over {len(body)} cells", ok(all(s == 0 for s in sl)))
    det = re.search(r"Deterministic for a run seed: \*\*(\w+)\*\*", text)
    add(m, "deterministic (report self-check)", det.group(1) if det else "—", ok(det is not None and det.group(1) == "yes"))
    if text2 is not None:
        strip = lambda t: [l for l in t.strip().splitlines() if l.strip() and not l.startswith("- Wall time") and not l.startswith("# ")]
        same = strip(text) == strip(text2)
        add(m, "deterministic (two runs, same seeds)", f"g1-qa vs {name2}: {'identical' if same else 'DIFFER'}", ok(same),
            "wall-time line and the combined file's title ignored")
    else:
        add(m, "deterministic (two runs, same seeds)", "second run not in this set", "INFO" if LEGACY else "MISSING")
    verdict = re.search(r"\*\*QA (PASS|FAIL)\*\*", text)
    add(m, "harness verdict", f"QA {verdict.group(1)}" if verdict else "—", ok(verdict is not None and verdict.group(1) == "PASS"))
    return body


def tier_rows(body, tier):
    return [r for r in body if col(r, "Tier").lower().startswith(tier)]


def check_routes(m, text):
    if text is None:
        add(m, "route cost (0 free, ratio ≤ 1.25)", "report not found", "MISSING")
        return
    free = bold_value(text, "Free routes (no hazard contact)")
    slow = bold_value(text, "Routes slower than 1.25")
    head, body = table_with(text, "Ratio", "Flag")
    ratios = [num(col(r, "Ratio")) for r in body]
    add(m, "0 free routes", f"{free} (of {len(body)} route × variant rows)", ok(free == "0"))
    add(m, "route ratio ≤ 1.25 × the section's fastest", f"max {max(r for r in ratios if r is not None):.2f}; over 1.25: {slow}" if ratios else str(slow),
        ok(slow == "0" and all(r is None or r <= 1.25 for r in ratios)))


def check_race(m, body, text):
    n = tier_rows(body, "normal")
    med = re.search(r"Normal tier: overall median \*\*([\d.]+) s\*\*", text)
    if med:
        v = float(med.group(1))
        add(m, "normal median 85–110 s", f"{v:.1f} s", ok(85 <= v <= 110))
    cells = [num(col(r, "Median"), 0) for r in n]
    low = [c for c in cells if c is not None and c < 85]
    add(m, "per-cell normal medians", rng(cells) + " s", "ACCEPTED" if low else "PASS",
        f"{len(low)} cell(s) under 85 s: accepted G4.2 §3 (A None / ButterFloors / TinyTilt 77–80 s)" if low else "")
    q = [num(col(r, "Qualified")) for r in body]
    add(m, "8 of 12 qualify (every cell)", rng(q, "{:.2f}") + " mean", ok(all(x == 8.0 for x in q)))
    te = [num(col(r, "Timer expiries")) for r in n]
    add(m, "timer expiries (normal, 150 s cap)", f"{int(sum(t or 0 for t in te))} over {len(n)} cells", "INFO",
        "G4.2 accepted 2 / 100 in B TurboToys")


def check_goo(m, body, text):
    med = re.search(r"Normal tier: overall median \*\*([\d.]+) s\*\*", text)
    if med:
        v = float(med.group(1))
        add(m, "normal median 50–65 s (G4.4-0)", f"{v:.1f} s", ok(50 <= v <= 65), "the harness line still prints the old 90–110 s GDD target")
    q = [num(col(r, "Qualified")) for r in body]
    add(m, "5 of 8 escape", rng(q, "{:.2f}") + " mean per cell", ok(all(x is not None and 4.80 <= x <= 5.0 for x in q)),
        "G4.3 §4 accepted ≈ 5 (5 in nearly every round; band ≥ 4.80 = no regression)")
    head, gb = table_with(text, "Min goo margin", "Escaped alone")
    if gb:
        mg = [num(col(r, "Min goo margin")) for r in gb]
        esc = all(col(r, "Escaped alone").lower().startswith("yes") for r in gb)
        add(m, "every route index escapes alone, goo margin 8–10 s", f"{rng(mg)} s over {len(gb)} indices; escaped {'all' if esc else 'NOT all'}",
            ok(esc and all(x is not None and 8 <= x <= 10 for x in mg)))
    else:
        add(m, "goo margin 8–10 s", "goo probe table not found", "MISSING")


def check_star(m, body, text, camp):
    te = [(num(col(r, "Timer expiries")), num(col(r, "Runs"))) for r in body]
    med = [num(col(r, "Median"), 0) for r in body]
    add(m, "the 60 s timer every run (no early end)", f"timer {int(sum(a or 0 for a, _ in te))}/{int(sum(b or 0 for _, b in te))} runs; median {rng(med)} s",
        ok(all(a == b for a, b in te) and all(x == 60.0 for x in med)))
    sc = [col(r, "Pickups / drops") for r in body]
    add(m, "scores = pickups − drops", f"{sum(1 for s in sc if 'OK' in s)}/{len(sc)} cells OK", ok(all("OK" in s for s in sc)))
    head, st = table_with(text, "Drops / bot", "Pickups on the table")
    if not st:
        add(m, "Star Fountain targets table", "not found", "MISSING")
        return
    ref, nor = tier_rows(st, "reference"), tier_rows(st, "normal")
    ex = [col(r, "Exactly 3") for r in st]
    add(m, "exactly 3 qualify (every run)", f"{sum(1 for e in ex if re.match(r'(\d+)/\1$', e))}/{len(ex)} cells", ok(all(re.match(r"(\d+)/\1$", e) for e in ex)))
    t = [pct(col(r, "on the table")) for r in st]
    add(m, "≥ 60 % of pickups on the turntable", rng(t, "{:.0f}") + " %", ok(all(x is not None and x >= 60 for x in t)))
    dn = [num(col(r, "Drops")) for r in nor]
    dr = [num(col(r, "Drops")) for r in ref]
    add(m, "drops per bot: normal 1–4", rng(dn, "{:.2f}"), ok(all(x is not None and 1 <= x <= 4 for x in dn)))
    add(m, "drops per bot: reference ≤ 2", rng(dr, "{:.2f}"), ok(all(x is not None and x <= 2 for x in dr)))
    g = [pct(col(r, "Golden")) for r in st]
    add(m, "golden ≥ 50 % collected", rng(g, "{:.0f}") + " %", ok(all(x is not None and x >= 50 for x in g)))
    sp = [pct(col(r, "Spring")) for r in ref]
    add(m, "reference spring → island ≥ 70 %", rng(sp, "{:.0f}") + " %", ok(all(x is not None and x >= 70 for x in sp)))
    cl = [pct(col(r, "3rd–4th")) for r in st]
    add(m, "3rd and 4th within 2 pts in ≥ 40 % of runs", rng(cl, "{:.0f}") + " %", ok(all(x is not None and x >= 40 for x in cl)))
    fr = [num(col(r, "Falls")) for r in ref]
    add(m, "reference ≤ 2 falls per round (mean per bot)", rng(fr, "{:.2f}"), ok(all(x is not None and x <= 2 for x in fr)))
    if camp is None:
        add(m, "camper probe (every spot below 3rd)", "report not found", "MISSING")
    else:
        head, cb = table_with(camp, "Camper score", "Below 3rd")
        if not cb:
            add(m, "camper probe (every spot below 3rd)", "camper table not found (run with -fallrushQaCamp)", "MISSING")
        else:
            below = [col(r, "Below 3rd").lower().startswith("yes") for r in cb]
            cm = [num(col(r, "Camper score")) for r in cb]
            add(m, "camper probe (every spot below 3rd)", f"{sum(below)}/{len(below)} spot × variant below; camper medians {rng(cm, '{:.0f}')}", ok(all(below)))


def check_lid(m, body, text):
    ch = [col(r, "One champion") for r in body]
    add(m, "one champion every run", f"{sum(1 for c in ch if re.match(r'(\d+)/\1\b', c))}/{len(ch)} cells", ok(all(re.match(r"(\d+)/\1\b", c) for c in ch)))
    med = re.search(r"Normal tier: overall median \*\*([\d.]+) s\*\*", text)
    if med:
        v = float(med.group(1))
        add(m, "normal median 60–100 s", f"{v:.1f} s", ok(60 <= v <= 100))
    head, lt = table_with(text, "Second chance", "Inner floor")
    if not lt:
        add(m, "Lid Flip targets table", "not found", "MISSING")
        return
    nor, ref = tier_rows(lt, "normal"), tier_rows(lt, "reference")
    md = [num(col(r, "Median / p90"), 0) for r in nor]
    p9 = [num(col(r, "Median / p90"), 1) for r in nor]
    add(m, "normal per-cell median 60–100 s", rng(md) + " s", ok(all(x is not None and 60 <= x <= 100 for x in md)))
    add(m, "normal p90 < 115 s", rng(p9) + " s", ok(all(x is not None and x < 115 for x in p9)))
    tm = [pct(col(r, "Timer")) for r in nor]
    add(m, "normal timer ≤ 5 %", rng(tm, "{:.0f}") + " %", ok(all(x is not None and x <= 5 for x in tm)))
    lp = [num(col(r, "Same-tick"), 1) for r in nor]
    add(m, "last pair out in the same tick ≤ 2 % (≥ 98 % apart)", rng(lp, "{:.0f}") + " %", ok(all(x is not None and x <= 2 for x in lp)))
    fair_ok, fair_txt = True, []
    for r in nor + ref:
        f = re.match(r"(\d+)/(\d+)", col(r, "Lid falls fair"))
        if f:
            fair_txt.append((int(f.group(1)), int(f.group(2))))
            fair_ok &= f.group(1) == f.group(2)
    a, b = sum(x for x, _ in fair_txt), sum(y for _, y in fair_txt)
    add(m, "every lid fall fair (≥ 1 s yellow / magenta), every tier", f"{a}/{b}", ok(fair_ok and fair_txt))
    sc = [pct(col(r, "Second chance")) for r in nor]
    add(m, "normal second chance ≥ 70 %", rng(sc, "{:.0f}") + " %", ok(all(x is not None and x >= 70 for x in sc)))
    ls = [num(col(r, "Lower-shelf stay")) for r in nor]
    add(m, "normal lower-shelf stay ≥ 8 s", rng(ls) + " s", ok(all(x is not None and x >= 8 for x in ls)))
    fl = [(num(col(r, "Inner floor"), 0), num(col(r, "Inner floor"), 1)) for r in lt]
    add(m, "inner floor ≥ 5 solid at every tick, every tier", f"violations {int(sum(v or 0 for v, _ in fl))}, min solid {rng([s for _, s in fl], '{:.0f}')}",
        ok(all(v == 0 and s is not None and s >= 5 for v, s in fl)))
    rt = [pct(col(r, "Timer")) for r in ref]
    add(m, "reference (flawless) bots reaching the timer", rng(rt, "{:.0f}") + " %", "INFO", "G4.5-8 tiebreak (C1)")


# ---------------------------------------------------------------- prediction probe

PROBE = re.compile(r"^(?P<label>[\w.-]+?)-(?P<n>\d+): (?P<secs>\d+)s corrections=(?P<corr>\d+).*?hazardMismatch=(?P<haz>\d+)"
                   r"(?:.*?racing windows=(?P<rw>\d+) \((?P<rs>\d+)s\): corrections=(?P<rc>\d+) \((?P<rp5>[\d.]+)/5s\)"
                   r" push=(?P<rpush>\d+) \((?P<rpush5>[\d.]+)/5s\)(?: hazardCorr=(?P<hc>\d+))?)?", re.M)
KNOWN = {("BlockPartyDash", "150 ms"): "C4: G4.2/G4.3 client 2 at 0.42", ("GooTower", "150 ms"): "G4.3 §6: a 40 s racing window holds few samples",
         ("StarScramble", "local"): "G4.4 §5: re-run ×3 (g44-probe-local3) median 0.00, max 0.33"}
MAXC = re.compile(r"maxCorr=([\d.]+)cm")


def check_probe(m, text):
    if text is None:
        add(m, "prediction probe", "report not found", "MISSING")
        return
    legs = {}
    for line in text.splitlines():
        p = PROBE.match(line.strip())
        if not p:
            continue
        lab = p.group("label")
        leg = "local" if "-local" in lab else "150 ms" if "-150" in lab else lab
        legs.setdefault(leg, []).append((p, MAXC.search(line)))
    for leg in ("local", "150 ms"):
        cl = legs.get(leg, [])
        name = "localhost" if leg == "local" else "150 ms ± 15 + 2 % loss"
        if not cl:
            add(m, f"probe {name}", "no client lines", "MISSING")
            continue
        runs = max(1, len({p.group("label") for p, _ in cl}))
        add(m, f"probe {name}: clients connected", f"{len(cl)}/{3 * runs}", ok(len(cl) >= 3 * runs),
            "a missing client = connect failure (R22)" if len(cl) < 3 * runs else "")
        rp = [float(p.group("rp5")) if p.group("rp5") else None for p, _ in cl]
        add(m, f"probe {name}: non-push corrections in a round ≤ 0.4 / 5 s",
            " / ".join("—" if v is None else f"{v:.2f}" for v in rp) + (f" (max corr {rng([float(x.group(1)) for _, x in cl if x], '{:.0f}')} cm)" if cl else ""),
            ok(all(v is not None and v <= 0.4 for v in rp)), KNOWN.get((m, leg), "") if not all(v is not None and v <= 0.4 for v in rp) else "")
        hz = [int(p.group("haz")) for p, _ in cl]
        hc = [p.group("hc") for p, _ in cl]
        add(m, f"probe {name}: 0 hazard mismatches", "whole run " + " / ".join(map(str, hz)) + "; in a round " + " / ".join(h if h is not None else "?" for h in hc),
            ok(all(h == 0 for h in hz)), "C2: Star Fountain whole-run counter" if m == "StarScramble" and any(hz) else "")


# ---------------------------------------------------------------- soak

def check_soak(text, src):
    m = "all maps"
    if text is None:
        add(m, "soak N/N complete, logs valid", f"{src or 'soak report'} not found", "MISSING")
        return
    durs = list({int(k): float(v) for k, v in re.findall(r"\[gs\] soak match (\d+) COMPLETE .*?duration=([\d.]+)s", text)}.values())  # a log may print a line twice
    done = re.search(r"\[gs\] soak done: (\d+) complete, (\d+) aborted of (\d+)", text)
    valid = re.search(r"(\d+)/(\d+) logs valid", text)
    if done:
        c, a, n = map(int, done.groups())
        add(m, "soak: every match complete", f"{c}/{n} complete, {a} aborted", ok(c == n and a == 0))
    else:
        add(m, "soak: every match complete", f"{len(durs)} COMPLETE lines, no 'soak done' line", "FAIL" if durs else "MISSING")
    if valid:
        add(m, "soak: every event log valid (m4-verify-log)", f"{valid.group(1)}/{valid.group(2)}", ok(valid.group(1) == valid.group(2)))
    else:
        add(m, "soak: every event log valid (m4-verify-log)", "not in this file", "INFO" if LEGACY else "MISSING")
    rss = re.search(r"RSS \(first / max / last sample, KB\): (\d+) / (\d+) / (\d+)", text)
    rssv = [int(x) for x in re.findall(r"rss=(\d+)MB", text)]
    if rss:
        add(m, "soak: max RSS", f"{int(rss.group(2)) / 1024:.0f} MB", "INFO")
    elif rssv:
        add(m, "soak: max RSS", f"{max(rssv)} MB (server's own counter)", "INFO")
    if durs:
        med = statistics.median(durs)
        add(m, "C6 match length (soak median game time)", f"{med:.1f} s = {med / 60:.1f} min (range {min(durs):.0f}–{max(durs):.0f} s, {len(durs)} matches)",
            "INFO", "GDD 7–9 min: ≈ +0.7 min lobby → receipt (G4.4-0); user's call, no change")


# ---------------------------------------------------------------- main

def main():
    global LEGACY
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=os.path.join(ROOT, "docs", "proof", "g4"))
    ap.add_argument("--legacy", action="store_true")
    ap.add_argument("--soak-log", default=None)
    a = ap.parse_args()
    LEGACY = a.legacy
    files = LEGACY_FILES if LEGACY else G47_FILES
    f = lambda kind, m: read(os.path.join(a.dir, files[kind][m])) if m in files[kind] else None

    for m in MAPS:
        text = f("qa", m)
        text2 = f("qa2", m)
        body = check_common(m, text, text2, "m4-qa") if text else (add(m, "QA report", f"{files['qa'].get(m)} not found", "MISSING") or [])
        if m in files["routes"]:
            check_routes(m, f("routes", m))
        if body:
            {"BlockPartyDash": lambda: check_race(m, body, text), "GooTower": lambda: check_goo(m, body, text),
             "StarScramble": lambda: check_star(m, body, text, f("camp", m)), "CrumbleShelf": lambda: check_lid(m, body, text)}[m]()
        check_probe(m, f("probe", m))

    soak_src = a.soak_log or (os.path.join(ROOT, ".m1-logs", "m4", "soak", "server.log") if LEGACY else os.path.join(a.dir, files["soak"]))
    check_soak(read(soak_src), os.path.relpath(soak_src, ROOT))

    counts = {}
    for r in rows:
        counts[r[3]] = counts.get(r[3], 0) + 1
    src = "G4.2–G4.5 proof reports (--legacy parse check)" if LEGACY else "scripts/g47-qa-all.sh reports"
    print(f"### G4.7 QA summary ({src}; `{os.path.relpath(a.dir, ROOT)}`)\n")
    print("| Map | Target | Measured | Result | Note |\n|---|---|---|---|---|")
    for r in rows:
        res = r[3] if r[3] in ("INFO", "ACCEPTED") else f"**{r[3]}**"
        print(f"| {r[0]} | {r[1]} | {r[2]} | {res} | {r[4]} |")
    print("\n" + " · ".join(f"{k} {v}" for k, v in sorted(counts.items())))
    bad = counts.get("FAIL", 0) + counts.get("MISSING", 0)
    print(f"\n**{'ALL TARGETS PASS' if bad == 0 else f'{bad} target(s) FAIL or MISSING'}** (ACCEPTED = missed, accepted by a G4.x proof; INFO = no target)")
    return 0 if bad == 0 else 1


LEGACY = False
if __name__ == "__main__":
    sys.exit(main())
