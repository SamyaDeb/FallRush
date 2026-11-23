/**
 * Landing sound: the game's own synthesised SFX + race loop (tools/audio/synth.py output, converted to mp3 in
 * public/sfx). Off by default; the header toggle turns it on (that click is the user gesture browsers need).
 * Nothing downloads until sound is switched on.
 */
import { useSyncExternalStore } from 'react';

export type Sfx =
  | 'bonk' | 'capsulepop' | 'confetti' | 'jump' | 'land' | 'mallet' | 'qualify' | 'goo' | 'eliminated' | 'count' | 'rush'
  | 'chirp';

const FILES: Record<Exclude<Sfx, 'chirp'>, string> = {
  bonk: 'sfx_bonk', capsulepop: 'sfx_capsulepop', confetti: 'sfx_confetti', jump: 'sfx_jump', land: 'sfx_land',
  mallet: 'sfx_mallet', qualify: 'sfx_qualify', goo: 'sfx_goo', eliminated: 'sfx_eliminated', count: 'sfx_count', rush: 'sfx_rush',
};
const CHIRPS = ['chirp_0', 'chirp_1', 'chirp_2', 'chirp_3'];
const KEY = 'fr_sound';

let ctx: AudioContext | null = null;
let master: GainNode | null = null;
let musicGain: GainNode | null = null;
let music: AudioBufferSourceNode | null = null;
const buffers = new Map<string, AudioBuffer>();
const loading = new Map<string, Promise<AudioBuffer | null>>();
let on = false;
const subs = new Set<() => void>();
const last = new Map<string, number>();

function load(name: string) {
  if (!ctx) return Promise.resolve(null);
  if (buffers.has(name)) return Promise.resolve(buffers.get(name)!);
  if (!loading.has(name)) {
    const c = ctx;
    loading.set(
      name,
      fetch(`/sfx/${name}.mp3`)
        .then((r) => r.arrayBuffer())
        .then((b) => c.decodeAudioData(b))
        .then((buf) => (buffers.set(name, buf), buf))
        .catch(() => null),
    );
  }
  return loading.get(name)!;
}

function ensure() {
  if (ctx) return;
  const AC = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
  if (!AC) return;
  ctx = new AC();
  master = ctx.createGain();
  master.gain.value = 0.7;
  master.connect(ctx.destination);
  musicGain = ctx.createGain();
  musicGain.gain.value = 0.32;
  musicGain.connect(master);
  [...Object.values(FILES), ...CHIRPS].forEach(load);
}

async function startMusic() {
  if (!ctx || !musicGain || music) return;
  const buf = await load('music_race');
  if (!buf || !on || music) return;
  music = ctx.createBufferSource();
  music.buffer = buf;
  music.loop = true;
  music.connect(musicGain);
  music.start();
}

function stopMusic() {
  try {
    music?.stop();
  } catch {
    /* already stopped */
  }
  music = null;
}

export function setSound(v: boolean) {
  on = v;
  try {
    localStorage.setItem(KEY, v ? '1' : '0');
  } catch {
    /* private mode */
  }
  if (v) {
    ensure();
    ctx?.resume();
    startMusic();
  } else {
    stopMusic();
    ctx?.suspend();
  }
  subs.forEach((f) => f());
}

/** Play a sound if sound is on. `rate` shifts pitch; repeats of the same sound closer than `gapMs` are dropped. */
export function play(s: Sfx, opts: { vol?: number; rate?: number; gapMs?: number } = {}) {
  if (!on || !ctx || !master) return;
  const now = performance.now();
  if (now - (last.get(s) ?? 0) < (opts.gapMs ?? 60)) return;
  last.set(s, now);
  const name = s === 'chirp' ? CHIRPS[Math.floor(Math.random() * CHIRPS.length)] : FILES[s];
  const buf = buffers.get(name);
  if (!buf) return void load(name);
  const src = ctx.createBufferSource();
  src.buffer = buf;
  src.playbackRate.value = opts.rate ?? 0.92 + Math.random() * 0.16;
  const g = ctx.createGain();
  g.gain.value = opts.vol ?? 0.8;
  src.connect(g).connect(master);
  src.start();
}

export const soundOn = () => on;

export function useSound(): [boolean, (v: boolean) => void] {
  const v = useSyncExternalStore(
    (f) => (subs.add(f), () => subs.delete(f)),
    () => on,
    () => false,
  );
  return [v, setSound];
}

/** Sound was on last visit: it comes back on at the first click / key anywhere (browsers need a gesture). */
export function restoreSound() {
  let want = false;
  try {
    want = localStorage.getItem(KEY) === '1';
  } catch {
    /* ignore */
  }
  if (!want) return;
  const go = () => {
    window.removeEventListener('pointerdown', go);
    window.removeEventListener('keydown', go);
    if (!on) setSound(true);
  };
  window.addEventListener('pointerdown', go);
  window.addEventListener('keydown', go);
}
