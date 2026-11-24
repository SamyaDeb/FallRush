#!/usr/bin/env python3
# P7 A3.1: summarises a load run (docs/proof/p7/load/<run>/) into load-report.json + a Markdown table:
# CU per tick (max / p95), tick gaps p95, Final real time, restarts, matches finished vs aborted, sealed book fill,
# bets / drops confirm times + refusals by error, 429s, input confirm p50 / p95 (the WebGL players' [onchain] lines),
# VPS CPU / memory peaks per container, and a payout = hand-calc check on every settled market.
#   python3 tools/p7/load-report.py docs/proof/p7/load/run1
import json, sys, glob, os, re, statistics as st
d = sys.argv[1]
R = {}
ms = [json.load(open(f)) for f in sorted(glob.glob(f'{d}/match-*.json'))]
ms = [m for m in ms if m.get('ok')]
fin = [m['summary'].get('final') or {} for m in ms]
R['matches'] = len(ms)
R['finished'] = sum(1 for m in ms if m['summary'].get('receipt', {}).get('status') == 5 or (m['summary'].get('final') or {}).get('complete'))
R['aborted'] = sum(1 for m in ms if (m['summary'].get('receipt') or {}).get('status') == 6 or m['summary'].get('aborted'))
cu = [f['cu'] for f in fin if f.get('cu')]
R['cu_max'] = max((c['max'] for c in cu), default=None)
R['cu_p95_max'] = max((c['p95'] for c in cu), default=None)
R['cu_p95_median'] = st.median([c['p95'] for c in cu]) if cu else None
R['tick_gap_p95_ms'] = [f.get('gapP95') for f in fin if f]
R['real_time'] = [round(f.get('realTime', 0), 4) for f in fin if f.get('realTime')]
R['restarts'] = sum(f.get('restarts', 0) for f in fin)
R['sealed'] = [(m['summary'].get('sealed') or {}).get('n') for m in ms]
R['sealed_revealed'] = [(m['summary'].get('sealed') or {}).get('revealed') for m in ms]
R['sealed_forfeits'] = [(m['summary'].get('sealed') or {}).get('forfeits') for m in ms]
R['drops'] = [(m['summary'].get('drops') or {}).get('count') for m in ms]
R['humans'] = [bin((m['summary'].get('receipt') or {}).get('humanMask', 0)).count('1') for m in ms]
# payout = hand calc: winners share the pool pro rata to their stake (pari-mutuel, fee 0)
checked = bad = 0
for m in ms:
    s = m['summary']
    for k in range(5):
        mk = s.get(f'market{k}')
        if not mk or mk.get('refund') == 1 or not mk.get('winners'):
            continue
        pool = int(mk['pool']); ws = int(mk.get('winningStake') or 0)
        if ws == 0:
            continue
        exp = sum(pool * int(b['amount']) // ws for b in mk['bets'] if (mk['winners'] >> b['outcome']) & 1)
        paid = int(mk.get('paidTotal') or 0)
        checked += 1
        if abs(exp - paid) > len(mk['bets']):  # integer rounding: at most 1 base unit per bet
            bad += 1
R['payout_markets_checked'] = checked; R['payout_mismatches'] = bad
# bettors
b = None
for name in ('bettors2', 'bettors'):
    p = f'{d}/{name}/events.jsonl'
    if os.path.exists(p):
        b = [json.loads(l) for l in open(p)]
        break
if b:
    def pct(a, q):
        a = sorted(a); return a[min(len(a) - 1, int(q / 100 * len(a)))] if a else None
    for kind in ('bet', 'sealedBet', 'drop'):
        ev = [e for e in b if e['ev'] == kind]
        okms = [e['ms'] for e in ev if e['ok']]
        errs = {}
        for e in ev:
            if not e['ok']:
                m = re.search(r'Custom":(\d+)', e.get('err') or ''); k2 = m.group(1) if m else (e.get('err') or '?')[:40]
                errs[k2] = errs.get(k2, 0) + 1
        R[kind] = {'ok': len(okms), 'refused': len(ev) - len(okms), 'p50_ms': pct(okms, 50), 'p95_ms': pct(okms, 95), 'max_ms': max(okms) if okms else None, 'errors': errs}
    ticks = [e for e in b if e['ev'] == 'tick']
    R['rpc429_bettors'] = ticks[-1]['rpc429'] if ticks else None
    st_ = [e for e in b if e['ev'] == 'setup']
    R['bettor_setup'] = f"{sum(1 for x in st_ if x['session'] and x['chips'])}/{len(st_)}"
    # sealed per match (book fill: 32 entries per match)
    per = {}
    for e in b:
        if e['ev'] == 'sealedBet':
            per.setdefault(e['match'], [0, 0])[0 if e['ok'] else 1] += 1
    R['sealed_by_bettors_per_match'] = per
# WebGL players: input confirm from the [onchain] lines, switch, pass
conf = []
for f in glob.glob(f'{d}/player-*/console.log'):
    for l in open(f, errors='ignore'):
        m = re.search(r'confirm p50 (\d+) p95 (\d+)', l)
        if m and int(m.group(1)) > 0:
            conf.append((int(m.group(1)), int(m.group(2))))
R['input_confirm_p50_ms_median'] = st.median([c[0] for c in conf]) if conf else None
R['input_confirm_p95_ms_max'] = max((c[1] for c in conf), default=None)
pl = []
for f in sorted(glob.glob(f'{d}/player-*/result.json')):
    j = json.load(open(f)); pl.append({'run': f.split('/')[-2], 'pass': j.get('pass'), 'switched': j.get('switched'), 'match': (j.get('match') or {}).get('match')})
R['players'] = pl
# VPS peaks
peak = {}
loads = []
fin_t = []
for l in open(f'{d}/vps.txt'):
    for name, cpu, mem in re.findall(r'(fallrush-[\w-]+)=([\d.]+)%/([\d.]+)MiB', l):
        p = peak.setdefault(name, [0, 0]); p[0] = max(p[0], float(cpu)); p[1] = max(p[1], float(mem))
    m = re.search(r' ([\d.]+) finity=(\d+)/([\d.]+)', l)
    if m:
        loads.append(float(m.group(1))); fin_t.append((m.group(2), float(m.group(3))))
R['vps_peak_cpu_pct_mem_mib'] = peak
R['vps_load1_max'] = max(loads) if loads else None
R['finity'] = {'all_200': all(c == '200' for c, _ in fin_t), 'max_s': max((t for _, t in fin_t), default=None), 'samples': len(fin_t)}
json.dump(R, open(f'{d}/load-report.json', 'w'), indent=1)
print(json.dumps(R, indent=1))
