#!/usr/bin/env python3
"""Fall Rush audio, synthesised from scratch (G3.2, doc 17 §6). Every sound is ours: no samples, no third-party audio.

    python3 tools/audio/synth.py <out_dir> [--dizzy-only]

Writes 44.1 kHz 16-bit WAVs: music loops (stereo, seamless, normalised to about -14 LUFS with ffmpeg's ebur128
measurement), a victory sting, SFX, UI sounds and gibberish chirps (mono). Deterministic (fixed seeds).
"""
import json, os, subprocess, sys, wave
import numpy as np

SR = 44100
rng = np.random.default_rng(1234)

def t_(d): return np.arange(int(d * SR)) / SR

def env(n, a=0.005, d=0.1, s=0.0, r=0.05, hold=0.0):
    """ADSR envelope over n samples (seconds)."""
    out = np.zeros(n)
    ai, di, hi, ri = int(a * SR), int(d * SR), int(hold * SR), int(r * SR)
    i = 0
    seg = min(ai, n); out[i:i + seg] = np.linspace(0, 1, seg, endpoint=False); i += seg
    seg = min(di, n - i); out[i:i + seg] = np.linspace(1, s, seg, endpoint=False); i += seg
    seg = min(hi, n - i); out[i:i + seg] = s; i += seg
    seg = min(ri, n - i); out[i:i + seg] = np.linspace(s, 0, seg); i += seg
    return out

def expdecay(n, tau): return np.exp(-np.arange(n) / (tau * SR))

def midi(m): return 440.0 * 2 ** ((m - 69) / 12)

def lowpass(x, cutoff):
    # one-pole low-pass (cutoff in Hz, scalar or array)
    a = np.exp(-2 * np.pi * np.asarray(cutoff) / SR) * np.ones(len(x))
    y = np.zeros_like(x); z = 0.0
    for i in range(len(x)):
        z = (1 - a[i]) * x[i] + a[i] * z; y[i] = z
    return y

def bandpass(x, f, q=4.0):
    # biquad band-pass (constant skirt), fixed f
    w = 2 * np.pi * f / SR; al = np.sin(w) / (2 * q)
    b0, b1, b2 = al, 0, -al; a0, a1, a2 = 1 + al, -2 * np.cos(w), 1 - al
    y = np.zeros_like(x); x1 = x2 = y1 = y2 = 0.0
    for i in range(len(x)):
        y0 = (b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0
        x2, x1, y2, y1 = x1, x[i], y1, y0; y[i] = y0
    return y

def noise(n): return rng.uniform(-1, 1, n)

def sweep(f0, f1, d, shape='exp'):
    n = int(d * SR); k = np.linspace(0, 1, n)
    f = f0 * (f1 / f0) ** k if shape == 'exp' else f0 + (f1 - f0) * k
    return np.cumsum(2 * np.pi * f / SR)

# ------------------------------------------------------------------ instruments
def marimba(f, d=0.6, v=1.0):
    n = int(d * SR); tt = np.arange(n) / SR
    x = np.sin(2 * np.pi * f * tt) * expdecay(n, 0.22) + 0.35 * np.sin(2 * np.pi * f * 4 * tt) * expdecay(n, 0.04)
    x += 0.15 * np.sin(2 * np.pi * f * 10 * tt) * expdecay(n, 0.01)
    return v * x * env(n, 0.002, d, 0, 0.0)

def pluck(f, d=0.5, v=1.0):
    # Karplus-Strong (ukulele-ish)
    n = int(d * SR); p = max(2, int(SR / f)); buf = rng.uniform(-1, 1, p); out = np.zeros(n)
    for i in range(n):
        out[i] = buf[i % p]; buf[i % p] = 0.5 * (buf[i % p] + buf[(i + 1) % p]) * 0.996
    return v * out * env(n, 0.001, d, 0, 0.0)

def kazoo(f, d, v=1.0, vib=5.5):
    n = int(d * SR); tt = np.arange(n) / SR
    ph = np.cumsum(2 * np.pi * f * (1 + 0.012 * np.sin(2 * np.pi * vib * tt)) / SR)
    saw = 2 * ((ph / (2 * np.pi)) % 1.0) - 1
    buzz = bandpass(saw, 900, 2.2) * 2.2 + bandpass(saw, 2300, 3.0) * 1.1 + 0.25 * saw
    return v * buzz * env(n, 0.02, 0.05, 0.8, 0.06, hold=max(0, d - 0.13))

def bass(f, d, v=1.0):
    n = int(d * SR); tt = np.arange(n) / SR
    x = np.sin(2 * np.pi * f * tt) + 0.3 * np.sin(2 * np.pi * 2 * f * tt)
    return v * x * env(n, 0.005, 0.1, 0.7, 0.05, hold=max(0, d - 0.16))

def kick(v=1.0):
    n = int(0.25 * SR); ph = sweep(150, 45, 0.25)
    return v * np.sin(ph[:n]) * expdecay(n, 0.09)

def clap(v=1.0):
    n = int(0.18 * SR); x = bandpass(noise(n), 1500, 1.2)
    e = np.zeros(n)
    for k in (0, 0.012, 0.024):
        i = int(k * SR); e[i:] += expdecay(n - i, 0.012 if k < 0.02 else 0.06)
    return v * x * e * 1.6

def shaker(v=1.0):
    n = int(0.06 * SR); return v * bandpass(noise(n), 7000, 1.5) * expdecay(n, 0.015) * 1.5

def place(buf, x, at, pan=0.0):
    i = int(at * SR)
    end = min(len(buf), i + len(x))
    seg = x[:end - i]
    l = np.cos((pan + 1) * np.pi / 4); r = np.sin((pan + 1) * np.pi / 4)
    buf[i:end, 0] += seg * l; buf[i:end, 1] += seg * r
    over = len(x) - (end - i)
    if over > 0:  # wrap the tail into the start: seamless loop
        buf[:over, 0] += x[end - i:] * l; buf[:over, 1] += x[end - i:] * r

# ------------------------------------------------------------------ music
SCALE_MAJ = [0, 2, 4, 5, 7, 9, 11]
SCALE_MIN = [0, 2, 3, 5, 7, 8, 10]

def song(bpm, bars, root, chords, melody, minor=False, lead_v=0.32, drive=1.0, seed=0):
    """chords: list of scale degrees per bar; melody: list of (beat, degree, beats) per 4-bar phrase (repeated)."""
    beat = 60.0 / bpm; dur = bars * 4 * beat
    buf = np.zeros((int(dur * SR), 2))
    sc = SCALE_MIN if minor else SCALE_MAJ
    def note(deg, octave=0):
        o, d = divmod(deg, 7); return midi(root + 12 * (octave + o) + sc[d])
    for b in range(bars):
        t0 = b * 4 * beat; c = chords[b % len(chords)]
        # bass on 1 and 3 (+ 2.5 push), kick on every beat in drive>1, clap on 2 and 4, shakers 8ths
        for k, deg in ((0, c), (2, c), (2.5, c + 4)):
            place(buf, bass(note(deg, -2), beat * (0.9 if k != 2.5 else 0.45), 0.5), t0 + k * beat)
        for k in range(4):
            if drive > 1 or k in (0, 2):
                place(buf, kick(0.75), t0 + k * beat)
            if k in (1, 3):
                place(buf, clap(0.42), t0 + k * beat, 0.1)
        for k in range(8):
            place(buf, shaker(0.18 if k % 2 else 0.1), t0 + k * beat / 2, 0.4)
        # marimba arpeggio (8ths) and ukulele strums on the off-beats
        arp = [c, c + 2, c + 4, c + 7, c + 4, c + 2, c + 4, c + 7]
        for k, deg in enumerate(arp):
            place(buf, marimba(note(deg, 0), 0.5, 0.34), t0 + k * beat / 2, -0.35)
        for k in (0.5, 1.5, 2.5, 3.5):
            for j, deg in enumerate((c, c + 2, c + 4)):
                place(buf, pluck(note(deg, -1), 0.35, 0.16), t0 + k * beat + j * 0.012, 0.35)
    # kazoo-ish lead melody (phrases of 4 bars)
    for p in range(bars // 4):
        for (bt, deg, ln) in melody:
            place(buf, kazoo(note(deg, 1), ln * beat * 0.95, lead_v), p * 16 * beat + bt * beat, 0.05)
    return buf

def sting():
    beat = 60 / 140; dur = 4.2
    buf = np.zeros((int(dur * SR), 2))
    for i, m in enumerate([60, 64, 67, 72, 76, 79]):
        place(buf, marimba(midi(m), 1.2, 0.5), i * beat / 2.5, -0.3 + 0.12 * i)
    for i, m in enumerate([72, 76, 79, 84]):
        place(buf, kazoo(midi(m), beat * (0.45 if i < 3 else 2.2), 0.4), 0.9 + i * beat / 2)
    for k in range(6):
        place(buf, clap(0.5), 0.9 + k * beat / 2, 0.0)
    n = int(2.5 * SR); cym = bandpass(noise(n), 8000, 0.7) * expdecay(n, 0.8) * 0.35
    place(buf, cym, 0.9 + 1.5 * beat, 0.2)
    place(buf, kick(0.9), 0.9 + 1.5 * beat)
    return buf

# ------------------------------------------------------------------ sfx
def sfx():
    out = {}
    n = int(0.07 * SR)
    out['step'] = (bandpass(noise(n), 2600, 3) * expdecay(n, 0.012) * 1.2 + np.sin(2 * np.pi * 900 * t_(0.07)) * expdecay(n, 0.006) * 0.4)
    ph = sweep(260, 760, 0.28); n = len(ph); tt = np.arange(n) / SR
    out['jump'] = np.sin(ph + 0.8 * np.sin(2 * np.pi * 28 * tt)) * env(n, 0.003, 0.28, 0, 0) * 0.7
    ph = sweep(220, 110, 0.16); n = len(ph)
    out['land'] = np.sin(ph) * expdecay(n, 0.05) * 0.9
    n = int(0.38 * SR); k = np.linspace(0, 1, n)
    out['dive'] = lowpass(noise(n), 400 + 3000 * k) * env(n, 0.02, 0.36, 0, 0) * 2.2
    n = int(0.45 * SR)
    out['slide'] = lowpass(noise(n), 900) * (0.6 + 0.4 * np.sin(2 * np.pi * 30 * t_(0.45))) * env(n, 0.01, 0.1, 0.6, 0.2, 0.15) * 1.6
    ph = sweep(500, 140, 0.45); n = len(ph); tt = np.arange(n) / SR
    out['stumble'] = np.sin(ph + 2.0 * np.sin(2 * np.pi * 14 * tt)) * env(n, 0.005, 0.45, 0, 0) * 0.6
    ph = sweep(160, 60, 0.32); n = len(ph)
    out['bonk'] = (np.sin(ph) * expdecay(n, 0.11) + 0.6 * bandpass(noise(n), 700, 2) * expdecay(n, 0.02)) * 0.95
    n = int(0.3 * SR)
    out['mallet'] = (bandpass(noise(n), 420, 6) * expdecay(n, 0.06) * 3 + np.sin(2 * np.pi * 95 * t_(0.3)) * expdecay(n, 0.08)) * 0.8
    n = int(0.5 * SR); k = np.linspace(0, 1, n)
    out['spinner'] = lowpass(noise(n), 300 + 1800 * np.sin(np.pi * k)) * np.sin(np.pi * k) * 2.0
    ph = sweep(420, 140, 0.35); n = len(ph); tt = np.arange(n) / SR
    out['goo'] = np.sin(ph + 1.5 * np.sin(2 * np.pi * 9 * tt)) * env(n, 0.01, 0.34, 0, 0) * 0.7 + 0.25 * bandpass(noise(n), 300, 4) * expdecay(n, 0.1)
    n = int(0.5 * SR); tt = t_(0.5)
    out['wobble'] = bandpass(noise(n), 1200, 5) * (0.5 + 0.5 * np.sign(np.sin(2 * np.pi * 22 * tt))) * env(n, 0.01, 0.1, 0.6, 0.1, 0.3) * 1.6
    ph = sweep(900, 200, 0.5); n = len(ph)
    out['tiledrop'] = np.sin(ph) * env(n, 0.005, 0.5, 0, 0) * 0.35 + np.concatenate([np.sin(2 * np.pi * 300 * t_(0.04)) * expdecay(int(0.04 * SR), 0.01), np.zeros(n - int(0.04 * SR))]) * 0.8
    def ding(fs, d=0.5, v=0.5):
        n = int(d * SR); x = np.zeros(n)
        for i, f in enumerate(fs):
            o = int(i * 0.05 * SR); m = n - o
            x[o:] += np.sin(2 * np.pi * f * np.arange(m) / SR) * expdecay(m, 0.15) + 0.3 * np.sin(2 * np.pi * f * 3 * np.arange(m) / SR) * expdecay(m, 0.05)
        return x * v
    out['star'] = ding([midi(84), midi(91)], 0.45, 0.45)
    out['stargold'] = ding([midi(79), midi(84), midi(88), midi(91), midi(96)], 0.9, 0.42)
    out['checkpoint'] = ding([midi(76), midi(83)], 0.7, 0.5)
    pop = np.sin(sweep(400, 1400, 0.05)) * expdecay(int(0.05 * SR), 0.02)
    out['capsulepop'] = np.concatenate([pop, np.zeros(int(0.1 * SR))]) * 0.9 + 0.0
    q = ding([midi(72), midi(76), midi(79), midi(84)], 1.0, 0.45)
    q[:len(pop)] += pop * 0.8
    out['qualify'] = q
    el = np.zeros(int(1.3 * SR))
    for i, m in enumerate([67, 65, 62]):
        k = kazoo(midi(m), 0.34 if i < 2 else 0.6, 0.45, vib=4)
        o = int(i * 0.34 * SR); el[o:o + len(k)] += k[:len(el) - o]
    out['eliminated'] = el
    n = int(0.12 * SR)
    out['count'] = np.sin(2 * np.pi * 880 * t_(0.12)) * env(n, 0.002, 0.1, 0.3, 0.02, 0.0) * 0.6
    rush = np.zeros(int(0.9 * SR))
    for m in (72, 76, 79):
        k = kazoo(midi(m), 0.8, 0.3); rush[:len(k)] += k
    out['rush'] = rush
    ph = sweep(900, 1500, 0.12); n = len(ph)
    out['emote'] = np.sin(ph) * env(n, 0.005, 0.12, 0, 0) * 0.5
    ph = sweep(180, 900, 0.4); n = len(ph); tt = np.arange(n) / SR
    out['spring'] = np.sin(ph + 1.2 * np.sin(2 * np.pi * 34 * tt)) * env(n, 0.002, 0.4, 0, 0) * 0.7
    n = int(0.9 * SR); tt = t_(0.9)
    whirr = np.sin(2 * np.pi * (110 + 40 * np.sin(2 * np.pi * 3 * tt)) * tt) * 0.3 + bandpass(noise(n), 500, 3) * 0.3
    clank = np.zeros(n); i = int(0.7 * SR); m = n - i
    clank[i:] = bandpass(noise(m), 2200, 8) * expdecay(m, 0.05) * 3
    out['claw'] = (whirr * env(n, 0.05, 0.1, 0.8, 0.1, 0.5) + clank) * 0.7
    n = int(0.6 * SR); c = np.zeros(n)
    for k in range(18):
        i = int(rng.uniform(0, 0.5) * SR); m = int(0.02 * SR)
        c[i:i + m] += bandpass(noise(m), rng.uniform(3000, 8000), 3) * expdecay(m, 0.005) * 1.5
    out['confetti'] = c
    out['rushhorn'] = out['rush']
    return out

def ui():
    out = {}
    n = int(0.04 * SR); out['click'] = bandpass(noise(n), 3000, 4) * expdecay(n, 0.006) * 1.4 + np.sin(2 * np.pi * 1200 * t_(0.04)) * expdecay(n, 0.008) * 0.3
    out['pop'] = np.sin(sweep(500, 1600, 0.06)) * expdecay(int(0.06 * SR), 0.025) * 0.8
    n = int(0.6 * SR); c = np.zeros(n)
    for k in range(6):
        i = int(k * 0.09 * SR); m = int(0.025 * SR)
        c[i:i + m] += bandpass(noise(m), 1800, 6) * expdecay(m, 0.006) * 2.2
    out['crank'] = c
    n = int(0.4 * SR); tt = t_(0.4)
    out['coin'] = (np.sin(2 * np.pi * 2093 * tt) + 0.6 * np.sin(2 * np.pi * 2637 * tt)) * expdecay(n, 0.09) * 0.35
    n = int(0.05 * SR); out['tick'] = np.sin(2 * np.pi * 1600 * t_(0.05)) * expdecay(n, 0.01) * 0.5
    n = int(0.3 * SR); k = np.linspace(0, 1, n); out['whoosh'] = lowpass(noise(n), 300 + 2500 * k) * np.sin(np.pi * k) * 1.6
    return out

def dizzy_sfx():
    """Dizzy hits (2026-11-24): funny cartoon cues, own seed so the other clips stay byte-identical.
    dizzy = a spring "boing" + tweeting cartoon birds circling the head; wheee = a slide whistle falling off the course."""
    out = {}
    r = np.random.default_rng(2411)
    # boing: a spring twang, pitch gliding up with a fast wobble that dies away (+ a soft octave for body)
    d = 0.5; n = int(d * SR); tt = np.arange(n) / SR
    f = (210 + 120 * (1 - np.exp(-tt / 0.06))) * (1 + 0.28 * np.sin(2 * np.pi * 13 * tt) * np.exp(-tt / 0.22))
    ph = np.cumsum(2 * np.pi * f / SR)
    boing = (np.sin(ph) + 0.35 * np.sin(2 * ph) + 0.12 * np.sin(3 * ph)) * env(n, 0.002, 0.05, 0.75, 0.2, 0.25) * 0.75
    # birds: 7 quick tweets (rising-falling, trilled), louder and softer as they circle
    birds = np.zeros(int(1.45 * SR))
    for i in range(7):
        o = int((0.08 + i * 0.18 + r.uniform(-0.02, 0.02)) * SR)
        d = r.uniform(0.07, 0.11); m = int(d * SR); tt = np.arange(m) / SR
        base = r.uniform(2500, 3300)
        f = base * (1 + 0.22 * np.sin(np.pi * tt / d)) * (1 + 0.04 * np.sin(2 * np.pi * 38 * tt))
        tw = np.sin(np.cumsum(2 * np.pi * f / SR)) * env(m, 0.004, 0.02, 0.7, 0.03, d - 0.06)
        if i % 2 == 0:  # a double tweet now and then
            tw = np.concatenate([tw, np.zeros(int(0.03 * SR)), tw[: m * 2 // 3] * 0.8])
        e = min(len(tw), len(birds) - o)
        birds[o:o + e] += tw[:e] * (0.55 + 0.45 * np.sin(np.pi * 2 * i / 3.5) ** 2) * 0.32
    lead = int(0.18 * SR)
    x = np.zeros(lead + len(birds))
    x[:len(boing)] += boing
    x[lead:] += birds
    out['dizzy'] = x
    # wheee: a slide whistle swooping up a little, then falling a long way (with a breathy edge)
    d = 0.95; n = int(d * SR); tt = np.arange(n) / SR; k = tt / d
    f = 900 + 500 * np.sin(np.pi * np.minimum(k / 0.25, 1) / 2) - 1050 * np.clip((k - 0.25) / 0.75, 0, 1) ** 1.3
    f = f * (1 + 0.012 * np.sin(2 * np.pi * 6 * tt))
    w = np.sin(np.cumsum(2 * np.pi * f / SR)) + 0.08 * bandpass(noise(n), 1800, 2)
    out['wheee'] = w * env(n, 0.03, 0.1, 0.8, 0.25, 0.6) * 0.55
    return out

def chirps():
    """Gibberish (no words): 3-5 syllables of a buzzy voice through vowel formants, pitch contour per mood."""
    out = {}
    vowels = [(700, 1200), (400, 2000), (300, 900), (600, 1700)]
    moods = {0: (1.0, 0.35), 1: (0.9, -0.3), 2: (1.3, 0.6), 3: (0.7, -0.1)}  # happy, sad, surprised, grumpy
    for i, (base, slope) in moods.items():
        parts = []
        r = np.random.default_rng(100 + i)
        for s in range(3 + i % 2 * 2):
            d = r.uniform(0.06, 0.11); n = int(d * SR); tt = np.arange(n) / SR
            f0 = 420 * base * (1 + slope * s / 4) * (1 + 0.15 * np.sin(np.pi * tt / d))
            ph = np.cumsum(2 * np.pi * f0 / SR); src = np.sign(np.sin(ph)) * 0.5 + 0.5 * np.sin(ph)
            f1, f2 = vowels[r.integers(0, 4)]
            v = bandpass(src, f1, 3) + 0.6 * bandpass(src, f2, 4)
            parts.append(v * env(n, 0.008, 0.02, 0.8, 0.02, d - 0.05)); parts.append(np.zeros(int(0.02 * SR)))
        out[str(i)] = np.concatenate(parts) * 1.2
    return out

# ------------------------------------------------------------------ io
def write(path, x):
    x = np.asarray(x, dtype=np.float64)
    peak = np.max(np.abs(x)) or 1.0
    if peak > 0.98:
        x = x / peak * 0.98
    pcm = (x * 32767).astype(np.int16)
    with wave.open(path, 'wb') as w:
        w.setnchannels(1 if pcm.ndim == 1 else 2); w.setsampwidth(2); w.setframerate(SR)
        w.writeframes(pcm.tobytes())

def lufs(path):
    r = subprocess.run(['ffmpeg', '-hide_banner', '-nostats', '-i', path, '-af', 'ebur128', '-f', 'null', '-'], capture_output=True, text=True)
    lines = [l for l in r.stderr.splitlines() if l.strip().startswith('I:')]
    return float(lines[-1].split()[1]) if lines else None

def music(path, buf, target=-14.0):
    write(path, buf * 0.5)
    for _ in range(3):
        l = lufs(path)
        if l is None:
            break
        gain = 10 ** ((target - l) / 20)
        buf = buf * 0.5 * gain / 0.5 if False else buf * gain
        write(path, buf * 0.5)
        if abs(lufs(path) - target) < 0.6:
            break
    return lufs(path)

def main():
    out = sys.argv[1]; os.makedirs(out, exist_ok=True)
    for k, x in dizzy_sfx().items():
        write(os.path.join(out, 'sfx_' + k + '.wav'), x)
    if '--dizzy-only' in sys.argv:  # write only the dizzy-hit clips (the rest stay as they are)
        return
    report = {}
    # Lobby: relaxed, C major, 112 bpm, 8 bars. Race: bouncy, F major, 128 bpm. Final: tense, A minor, 142 bpm.
    mel_lobby = [(0, 4, 1), (1, 5, 1), (2, 4, 0.5), (2.5, 2, 1.5), (4, 0, 1), (5, 2, 1), (6, 4, 2), (8, 5, 1), (9, 4, 1), (10, 2, 1), (11, 0, 1), (12, 1, 1.5), (13.5, 2, 0.5), (14, 0, 2)]
    mel_race = [(0, 0, 0.5), (0.5, 2, 0.5), (1, 4, 1), (2, 7, 1), (3, 6, 0.5), (3.5, 4, 0.5), (4, 5, 1.5), (5.5, 4, 0.5), (6, 2, 2), (8, 4, 0.5), (8.5, 5, 0.5), (9, 7, 1), (10, 9, 1), (11, 7, 1), (12, 8, 1), (13, 7, 1), (14, 4, 2)]
    mel_final = [(0, 0, 0.5), (0.5, 0, 0.5), (1, 2, 0.5), (1.5, 3, 0.5), (2, 4, 1), (3, 3, 1), (4, 2, 0.5), (4.5, 2, 0.5), (5, 1, 0.5), (5.5, 2, 0.5), (6, 4, 2), (8, 7, 1), (9, 6, 0.5), (9.5, 4, 0.5), (10, 3, 1), (11, 2, 1), (12, 1, 1), (13, 2, 1), (14, 0, 2)]
    tracks = {
        'music_lobby': song(112, 8, 60, [0, 5, 3, 4], mel_lobby, lead_v=0.26),
        'music_race': song(128, 8, 65, [0, 3, 4, 0, 5, 3, 1, 4], mel_race, lead_v=0.3, drive=2),
        'music_final': song(142, 8, 57, [0, 0, 5, 6, 0, 3, 4, 4], mel_final, minor=True, lead_v=0.3, drive=2),
    }
    for k, b in tracks.items():
        report[k] = {'lufs': music(os.path.join(out, k + '.wav'), b), 'seconds': round(len(b) / SR, 2)}
    report['music_victory'] = {'lufs': music(os.path.join(out, 'music_victory.wav'), sting(), -13.0), 'seconds': 4.2}
    for k, x in sfx().items():
        if k == 'rushhorn':
            continue
        write(os.path.join(out, 'sfx_' + k + '.wav'), x)
    for k, x in ui().items():
        write(os.path.join(out, 'ui_' + k + '.wav'), x)
    for k, x in chirps().items():
        write(os.path.join(out, 'chirp_' + k + '.wav'), x)
    print(json.dumps(report, indent=1))

if __name__ == '__main__':
    main()
