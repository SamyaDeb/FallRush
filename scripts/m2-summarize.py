#!/usr/bin/env python3
"""LOCAL MEASUREMENT: summarise [probe] logs (.m1-logs/probe-<label>.log) into one gate line per client."""
import re, sys
def g(line, k):
    m = re.search(k + r'=([-\d.]+|NaN)', line)
    return m.group(1) if m else None
for label in sys.argv[1:]:
    t = dict(corr=0, push=0, snaps=0, match=0, silent=0, haz=0, resp=0, w=0)
    sumcorr = maxcorr = 0.0; maxrep = 0; late = []; extrap = []; frozen = []; st = []; fps = []; hits = laps = falls = 0; rtt = lead = None
    for line in open(f'.m1-logs/probe-{label}.log'):
        if line.startswith('[probe] pred'):
            c = int(g(line, 'corrections')); t['corr'] += c; t['push'] += int(g(line, 'pushCorrections'))
            t['snaps'] += int(g(line, 'snaps')); t['match'] += int(g(line, 'match')); t['silent'] += int(g(line, 'silent'))
            t['haz'] += int(g(line, 'hazMismatch')); t['resp'] += int(g(line, 'respawns')); t['w'] += 1
            sumcorr += c * float(g(line, 'meanCorr')); maxcorr = max(maxcorr, float(g(line, 'maxCorr'))); maxrep = max(maxrep, int(g(line, ' max')))
            late.append(float(g(line, 'lateP95'))); extrap.append(float(g(line, 'extrap'))); frozen.append(float(g(line, 'frozen')))
            s = re.search(r'remoteStutter p50=\S+ p95=([\d.]+)', line); st += [float(s.group(1))] if s else []
            hits = int(g(line, 'predHits')); laps = int(g(line, 'laps')); falls = int(g(line, 'falls'))
        if line.startswith('[probe] snapHz'):
            rtt = (g(line, 'rtt p50'), g(line, 'p95')); lead = (g(line, 'lead'), g(line, 'target')); fps.append(float(g(line, 'fps')))
    secs = t['w'] * 10
    print(f"{label}: {secs}s corrections={t['corr']} ({t['corr'] * 5 / max(1, secs):.2f}/5s) push={t['push']} meanCorr={sumcorr / max(1, t['corr']):.1f}cm "
          f"maxCorr={maxcorr:.1f}cm bitIdentical={t['match']}/{t['snaps']} silent={t['silent']} hazardMismatch={t['haz']} predictedHits={hits} respawns={t['resp']} "
          f"replayMax={maxrep} lateP95max={max(late):.0f}ms extrap={sum(extrap) / len(extrap):.1f}% frozenMax={max(frozen):.1f}% remoteStutterP95max={max(st) if st else float('nan'):.2f}m/s "
          f"rtt p50/p95={rtt} lead/target={lead} laps={laps} falls={falls} fpsMin={min(fps) if fps else 'n/a'}")
