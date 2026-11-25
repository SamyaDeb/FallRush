// Top-down 2D view of the Lid Flip Final, or (fullchain) of any round framed on its players (the same World the program steps): lids (idle / lit / open / retired),
// sweeper bars, players (humans ringed, bots plain, out = faded), the Chaos Drop telegraph banner.
import type { CoreWorld } from './core';
const SHELL = ['#FF3EA5', '#3FA9F5', '#FFD21F', '#5BE3B5', '#7B2FF7', '#FF8A3D', '#2EC4B6', '#E84855', '#FF9BD2', '#3A2E8C', '#FFE45C', '#B79CFF'];
const LID = ['#F9EDC2', '#FFD43B', '#2A1250', '#b9b2cc', '#FF8FB8'];
export function draw(cv: HTMLCanvasElement, w: CoreWorld, opts: { humans: number; label?: string; banner?: string }) {
  const dpr = Math.min(2, window.devicePixelRatio || 1);
  const W = cv.clientWidth * dpr, H = cv.clientHeight * dpr;
  if (cv.width !== W || cv.height !== H) { cv.width = W; cv.height = H; }
  const g = cv.getContext('2d')!;
  g.fillStyle = '#DDEFFB'; g.fillRect(0, 0, W, H);
  const lids = w.lids();
  let minX = -8000, maxX = 8000, minZ = -8000, maxZ = 8000;
  if (lids.length) { minX = Math.min(...lids.map((l) => l.x)) - 1500; maxX = Math.max(...lids.map((l) => l.x)) + 1500; minZ = Math.min(...lids.map((l) => l.z)) - 1500; maxZ = Math.max(...lids.map((l) => l.z)) + 1500; }
  else {
    // fullchain rounds 1–3 (no lid grid): frame the players still in the round (a race map is long: follow the pack)
    const ps = Array.from({ length: 12 }, (_, i) => w.player(i)).filter((p) => p && p.flags & 1 && !(p.flags & 4) && p.y > -4000) as { x: number; z: number }[];
    if (ps.length) { minX = Math.min(...ps.map((p) => p.x)) - 6000; maxX = Math.max(...ps.map((p) => p.x)) + 6000; minZ = Math.min(...ps.map((p) => p.z)) - 6000; maxZ = Math.max(...ps.map((p) => p.z)) + 6000; }
  }
  const span = Math.max(maxX - minX, maxZ - minZ), k = Math.min(W, H) / span;
  const cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
  const X = (x: number) => W / 2 + (x - cx) * k, Z = (z: number) => H / 2 - (z - cz) * k;
  const pitch = lids.length > 1 ? Math.min(...lids.slice(1).map((l) => Math.hypot(l.x - lids[0].x, l.z - lids[0].z)).filter((d) => d > 0)) : 1200;
  const sz = pitch * k * 0.88;
  for (const l of lids) {
    g.fillStyle = LID[l.s] ?? '#eee';
    g.beginPath(); g.roundRect(X(l.x) - sz / 2, Z(l.z) - sz / 2, sz, sz, sz * 0.18); g.fill();
    if (l.s === 1 || l.s === 4) { g.strokeStyle = '#FF2D8A'; g.lineWidth = 2 * dpr; g.stroke(); }
  }
  for (const s of w.sweepers()) {
    g.strokeStyle = '#FF2D8A'; g.lineWidth = Math.max(4, 260 * k); g.lineCap = 'round';
    g.beginPath(); g.moveTo(X(s.px - s.c * s.arm), Z(s.pz - s.s * s.arm)); g.lineTo(X(s.px + s.c * s.arm), Z(s.pz + s.s * s.arm)); g.stroke();
    g.fillStyle = '#2A1250'; g.beginPath(); g.arc(X(s.px), Z(s.pz), 5 * dpr, 0, 7); g.fill();
  }
  for (let slot = 0; slot < 12; slot++) {
    const p = w.player(slot);
    if (!p || !(p.flags & 1)) continue;
    const out = (p.flags & 4) !== 0 || p.y < -4000;
    const r = Math.max(7 * dpr, 420 * k);
    g.globalAlpha = out ? 0.3 : 1;
    g.fillStyle = SHELL[slot]; g.beginPath(); g.arc(X(p.x), Z(p.z), r, 0, 7); g.fill();
    g.lineWidth = (opts.humans >> slot & 1 ? 4 : 2) * dpr; g.strokeStyle = opts.humans >> slot & 1 ? '#FFD21F' : '#2A1250'; g.stroke();
    g.fillStyle = '#fff'; g.font = `${Math.round(11 * dpr)}px Varela Round`; g.textAlign = 'center'; g.fillText(String(slot + 1), X(p.x), Z(p.z) + 4 * dpr);
    g.globalAlpha = 1;
  }
  if (opts.banner) { g.fillStyle = 'rgba(255,62,165,.92)'; g.fillRect(0, 0, W, 34 * dpr); g.fillStyle = '#fff'; g.font = `${Math.round(18 * dpr)}px Lilita One`; g.textAlign = 'center'; g.fillText(opts.banner, W / 2, 24 * dpr); }
  if (opts.label) { g.fillStyle = '#2A1250'; g.font = `${Math.round(12 * dpr)}px Varela Round`; g.textAlign = 'left'; g.fillText(opts.label, 8 * dpr, H - 8 * dpr); }
}
