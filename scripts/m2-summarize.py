#!/usr/bin/env python3
"""LOCAL MEASUREMENT: summarise [probe] logs (.m1-logs/probe-<label>.log) into one gate line per client."""
import re, sys
def g(line, k):
    m = re.search(k + r'=([-\d.]+|NaN)', line)
    return m.group(1) if m else None
for label in sys.argv[1:]:
    t = dict(corr=0, push=0, snaps=0, match=0, silent=0, haz=0, resp=0, w=0); rounds = 0
    sumcorr = maxcorr = 0.0; maxrep = 0; late = []; extrap = []; frozen = []; st = []; fps = []; hits = laps = falls = 0; rtt = lead = None
    for line in open(f'.m1-logs/probe-{label}.log'):
        if line.startswith('[probe] pred'):
            c = int(g(line, 'corrections')); t['corr'] += c; t['push'] += int(g(line, 'pushCorrections'))
            t['snaps'] += int(g(line, 'snaps')); t['match'] += int(g(line, 'match')); t['silent'] += int(g(line, 'silent'))
            t['haz'] += int(g(line, 'hazMismatch')); t['resp'] += int(g(line, 'respawns')); t['w'] += 1
            sumcorr += c * float(g(line, 'meanCorr')); maxcorr = max(maxcorr, float(g(line, 'maxCorr'))); maxrep = max(maxrep, int(g(line, ' max')))
            late.append(float(g(line, 'lateP95'))); extrap.append(float(g(line, 'extrap'))); frozen.append(float(g(line, 'frozen')))
            s = re.search(r'remoteStutter p50=\S+ p95=([\d.]+)', line); st += [float(s.group(1))] if s else []
            hits = int(g(line, 'predHits')); laps = int(g(line, 'laps') or 0); falls = int(g(line, 'falls') or 0)
            he = g(line, 'hazExplained'); t['hexp'] = t.get('hexp', 0) + (int(he) if he else 0)
            hc = g(line, 'hazardCorr'); t['hazc'] = t.get('hazc', 0) + (int(hc) if hc else 0)
            if 'phase=Rush' in line:
                t['rw'] = t.get('rw', 0) + 1; t['rc'] = t.get('rc', 0) + c; t['rp'] = t.get('rp', 0) + int(g(line, 'pushCorrections'))
            m2 = re.search(r'rounds=(\d+)', line); rounds = int(m2.group(1)) if m2 else 0
        if line.startswith('[probe] snapHz'):
            rtt = (g(line, 'rtt p50'), g(line, 'p95')); lead = (g(line, 'lead'), g(line, 'target')); fps.append(float(g(line, 'fps')))
    secs = t['w'] * 10
    print(f"{label}: {secs}s corrections={t['corr']} ({t['corr'] * 5 / max(1, secs):.2f}/5s) push={t['push']} meanCorr={sumcorr / max(1, t['corr']):.1f}cm "
          f"maxCorr={maxcorr:.1f}cm bitIdentical={t['match']}/{t['snaps']} silent={t['silent']} hazardMismatch={t['haz']} hazardExplained={t.get('hexp', 0)} predictedHits={hits} respawns={t['resp']} "
          f"replayMax={maxrep} lateP95max={max(late, default=float('nan')):.0f}ms extrap={sum(extrap) / max(1, len(extrap)):.1f}% frozenMax={max(frozen, default=float('nan')):.1f}% remoteStutterP95max={max(st) if st else float('nan'):.2f}m/s "
          f"rtt p50/p95={rtt} lead/target={lead} laps={laps} falls={falls} fpsMin={min(fps) if fps else 'n/a'} "
          f"| racing windows={t.get('rw', 0)} ({t.get('rw', 0) * 10}s): corrections={t.get('rc', 0)} ({t.get('rc', 0) * 5 / max(1, t.get('rw', 0) * 10):.2f}/5s) push={t.get('rp', 0)} ({t.get('rp', 0) * 5 / max(1, t.get('rw', 0) * 10):.2f}/5s) hazardCorr={t.get('hazc', 0)} raceRounds={rounds}")
