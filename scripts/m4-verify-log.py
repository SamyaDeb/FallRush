#!/usr/bin/env python3
"""M4: independently re-verify match event logs (match-<id>.jsonl, written by the game server).

For each log: recompute the roster hash (M4 proposal, doc 06 notes) from the roster, the transcript
(init = sha256("fallrush-transcript-v1" ‖ match_id ‖ roster_hash); next = sha256(prev ‖ tag ‖ seq_le32 ‖ round ‖ slot ‖ arg))
from the events and compare it with the server's value after every event; recompute the deal from the seed (deal.ts
port) and the round targets; check the program's ordering rules (doc 06): seq contiguous from 0 = DEAL, per round
START_ROUND → QUALIFIED with placements 1..k (k ≤ target) → ELIMINATED → COMPLETE_ROUND(k), no alive slot left, then
COMPLETE_MATCH with exactly one alive slot as champion (or ABORT).

usage: scripts/m4-verify-log.py <log dir or files...>    exit 0 = every log valid
"""
import hashlib, json, os, struct, sys

TAGS = {1: "DEAL", 2: "START_ROUND", 3: "QUALIFIED", 4: "ELIMINATED", 5: "COMPLETE_ROUND", 6: "COMPLETE_MATCH", 7: "ABORT"}
POOL = {  # map_pool_v1.json (variants, modifier weights) and the rounds table
    "maps": {0: (2, [(0, 40), (1, 15), (2, 15), (3, 15), (5, 15)]), 1: (2, [(0, 40)]), 2: (2, [(0, 40), (1, 15), (3, 15), (4, 15)]), 3: (2, [(0, 40), (1, 15), (2, 15)])},
    "rounds": [[0], [1], [2], [3]],
}


def sha(*parts):
    h = hashlib.sha256()
    for p in parts:
        h.update(p)
    return h.digest()


def rnd(r, label, n):
    zone = (2**32 // n) * n
    d = sha(r, label)
    while True:
        for i in range(8):
            x = struct.unpack_from("<I", d, i * 4)[0]
            if x < zone:
                return x % n
        d = sha(d)


def deal(r):
    out = {"maps": [], "variants": [], "modifiers": []}
    for i in range(4):
        rd = i + 1
        choices = POOL["rounds"][i]
        m = choices[rnd(r, f"map{rd}".encode(), len(choices))]
        variants, weights = POOL["maps"][m]
        out["maps"].append(m)
        out["variants"].append(rnd(r, f"var{rd}".encode(), variants))
        k = rnd(r, f"mod{rd}".encode(), sum(w for _, w in weights))
        for mod, w in weights:
            if k < w:
                out["modifiers"].append(mod)
                break
            k -= w
    return out


def targets(p):
    r1 = (p * 66 + 99) // 100
    r2 = (r1 * 62 + 99) // 100
    return [r1, r2, min(3, r2 - 1), 1]


def roster_hash(roster):
    parts = [b"fallrush-roster-v1", bytes([len(roster)])]
    for i, e in enumerate(roster):
        assert e["slot"] == i, "roster slots must be 0..P-1"
        parts += [bytes([i, 1 if e["bot"] else 0]), sha(e["userId"].encode()), bytes([0])]
    return sha(*parts)


def verify(path):
    lines = [json.loads(l) for l in open(path) if l.strip()]
    errs = []
    head = lines[0]
    if head.get("type") != "match":
        return [f"first line is not the match header"], None
    events = [l for l in lines if l.get("type") == "event"]
    end = next((l for l in lines if l.get("type") == "end"), None)
    mid = bytes.fromhex(head["matchId"])
    seed = bytes.fromhex(head["seed"])
    rh = roster_hash(head["roster"])
    if rh.hex() != head["rosterHash"]:
        errs.append(f"roster hash {rh.hex()[:16]} != server {head['rosterHash'][:16]}")
    if sha(b"fallrush-match-id", seed)[:16] != mid:
        errs.append("match id is not derived from the seed")
    d = deal(seed)
    if d != head["deal"]:
        errs.append(f"deal {d} != server {head['deal']}")
    tg = targets(head["players"])
    plan = head["plan"]
    if len(plan) < 4:
        tg[len(plan) - 1] = 1
    if tg != head["targets"]:
        errs.append(f"targets {tg} != server {head['targets']}")
    t = sha(b"fallrush-transcript-v1", mid, rh)
    if t.hex() != head["transcriptInit"]:
        errs.append("transcript init differs")
    alive = set(range(head["players"]))
    rnd_no, qualified, resolved, champion = 0, 0, set(), None
    for i, e in enumerate(events):
        if e["seq"] != i:
            errs.append(f"seq gap at {i}: {e['seq']}")
        arg = bytes.fromhex(e["arg"])
        t = sha(t, bytes([e["tag"]]), struct.pack("<I", e["seq"]), bytes([e["round"], e["slot"]]), arg)
        if t.hex() != e["transcript"]:
            errs.append(f"transcript differs at seq {e['seq']} ({TAGS.get(e['tag'])})")
        tag = e["tag"]
        if i == 0 and tag != 1:
            errs.append("seq 0 is not DEAL")
        if tag == 1 and arg != seed:
            errs.append("DEAL arg is not the seed")
        if tag == 2:
            if e["round"] != rnd_no + 1:
                errs.append(f"START_ROUND {e['round']} after round {rnd_no}")
            rnd_no, qualified, resolved = e["round"], 0, set()
        if tag == 3:
            if e["slot"] not in alive or e["slot"] in resolved:
                errs.append(f"QUALIFIED slot {e['slot']} not alive")
            qualified += 1
            if arg[0] != qualified:
                errs.append(f"placement {arg[0]} != {qualified}")
            if qualified > tg[rnd_no - 1]:
                errs.append(f"round {rnd_no}: qualified over target")
            resolved.add(e["slot"])
        if tag == 4:
            if e["slot"] not in alive or e["slot"] in resolved:
                errs.append(f"ELIMINATED slot {e['slot']} not alive")
            alive.discard(e["slot"])
            resolved.add(e["slot"])
        if tag == 5:
            if alive - resolved:
                errs.append(f"COMPLETE_ROUND {rnd_no} with alive unresolved slots {sorted(alive - resolved)}")
            if arg[0] != qualified or qualified < 1:
                errs.append(f"COMPLETE_ROUND arg {arg[0]} != qualified {qualified}")
        if tag == 6:
            if len(alive) != 1 or e["slot"] not in alive:
                errs.append(f"COMPLETE_MATCH champion {e['slot']} but alive {sorted(alive)}")
            champion = e["slot"]
    if end is not None and end["transcript"] != t.hex():
        errs.append("end line transcript differs")
    kind = "complete" if champion is not None else ("abort" if events and events[-1]["tag"] == 7 else "incomplete")
    return errs, {"kind": kind, "champion": champion, "rounds": rnd_no, "events": len(events), "transcript": t.hex(), "durationTicks": end["durationTicks"] if end else None}


def main():
    paths = []
    for a in sys.argv[1:]:
        if os.path.isdir(a):
            paths += sorted(os.path.join(a, f) for f in os.listdir(a) if f.endswith(".jsonl"))
        else:
            paths.append(a)
    bad = 0
    for p in paths:
        errs, info = verify(p)
        status = "OK " if not errs else "BAD"
        print(f"{status} {os.path.basename(p)} {info}")
        for e in errs:
            print("    " + e)
        bad += 1 if errs else 0
    print(f"{len(paths) - bad}/{len(paths)} logs valid")
    sys.exit(1 if bad or not paths else 0)


if __name__ == "__main__":
    main()
