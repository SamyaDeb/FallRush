// Live board: bakes each round map's static geometry (maps/<Map>.mapjson, the same source the mapblob is built from)
// into a small top-down outline for the 2D board (public/maps/<Map>.board.json). Moving / reactive hazards come from
// rush_core.wasm (rc_hazards) at run time; this file holds only what never moves.
//   { name, killY, bounds: [minX, minZ, maxX, maxZ], top: maxY,
//     shapes: [{ p: [[x, z]…], y0, y1, k: 's' static | 'r' rail (thin wall) }],   // sorted low → high (draw order)
//     checkpoints: [{ x, y, z, hx, hz }], course: [[x, y, z]…], spawns: [[x, z]…], anchors: [[x, y, z]…] (stars) }
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const app = join(dirname(fileURLToPath(import.meta.url)), '..');
const root = join(app, '../..');
const Q = 32768;

function hull(pts) {
  const p = [...pts].sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  if (p.length < 3) return p;
  const cross = (o, a, b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const lo = [], hi = [];
  for (const q of p) { while (lo.length >= 2 && cross(lo[lo.length - 2], lo[lo.length - 1], q) <= 0) lo.pop(); lo.push(q); }
  for (const q of p.reverse()) { while (hi.length >= 2 && cross(hi[hi.length - 2], hi[hi.length - 1], q) <= 0) hi.pop(); hi.push(q); }
  return lo.slice(0, -1).concat(hi.slice(0, -1));
}
const r = (v) => Math.round(v);

export function bake(name) {
  const m = JSON.parse(readFileSync(join(root, `maps/${name}.mapjson`), 'utf8'));
  const shapes = [];
  for (const c of m.colliders) {
    if (c.dyn !== 'static') continue; // platforms / doors / blocks / lids move: drawn from rc_hazards / rc_lids
    if (c.shape === 'obb') {
      const [cx, cy, cz] = c.center, [hx, hy, hz] = c.half, A = c.axes;
      // the 8 corners projected on x/z, then their hull (a tilted ramp projects to its real footprint)
      const pts = [];
      let y0 = Infinity, y1 = -Infinity;
      for (const sx of [-1, 1]) for (const sy of [-1, 1]) for (const sz of [-1, 1]) {
        const x = cx + (sx * hx * A[0][0] + sy * hy * A[1][0] + sz * hz * A[2][0]) / Q;
        const y = cy + (sx * hx * A[0][1] + sy * hy * A[1][1] + sz * hz * A[2][1]) / Q;
        const z = cz + (sx * hx * A[0][2] + sy * hy * A[1][2] + sz * hz * A[2][2]) / Q;
        pts.push([r(x), r(z)]); y0 = Math.min(y0, y); y1 = Math.max(y1, y);
      }
      const thin = Math.min(hx, hz) <= 400 && hy >= 300; // a rail / post / wall: drawn as an edge
      shapes.push({ p: hull(pts), y0: r(y0), y1: r(y1), k: thin ? 'r' : 's', n: c.name.split('/').pop() });
    } else if (c.shape === 'prism') {
      shapes.push({ p: hull(c.verts.map(([x, z]) => [r(x), r(z)])), y0: r(c.y0), y1: r(c.y1), k: 's', n: c.name.split('/').pop() });
    } else if (c.shape === 'sphere') {
      const [cx, cy, cz] = c.center, rad = c.radius;
      shapes.push({ p: Array.from({ length: 16 }, (_, i) => [r(cx + rad * Math.cos(i * Math.PI / 8)), r(cz + rad * Math.sin(i * Math.PI / 8))]), y0: r(cy - rad), y1: r(cy + rad), k: 's', n: c.name.split('/').pop() });
    }
  }
  // tiny shapes (< 0.25 m²) add noise at phone scale; huge underground floors stay
  const area = (p) => Math.abs(p.reduce((s, a, i) => { const b = p[(i + 1) % p.length]; return s + a[0] * b[1] - b[0] * a[1]; }, 0)) / 2;
  const keep = shapes.filter((s) => s.p.length >= 3 && area(s.p) >= 250_000 && s.y1 > m.meta.killY);
  keep.sort((a, b) => a.y1 - b.y1);
  const all = keep.flatMap((s) => s.p);
  const bounds = [Math.min(...all.map((p) => p[0])), Math.min(...all.map((p) => p[1])), Math.max(...all.map((p) => p[0])), Math.max(...all.map((p) => p[1]))];
  // star anchors: STAR_PICKUP (type 11) def = [anchor_count, anchors × 3 …] (crates/rush-core/src/hazards/defs.rs)
  const sf = (m.hazards[0] ?? []).find((h) => h.type === 11);
  const anchors = sf ? Array.from({ length: sf.def[0] }, (_, i) => sf.def.slice(1 + 3 * i, 4 + 3 * i)) : [];
  return {
    name, killY: m.meta.killY, bounds, top: Math.max(...keep.map((s) => s.y1)),
    shapes: keep.map(({ p, y0, y1, k }) => ({ p, y0, y1, k })),
    checkpoints: (m.checkpoints ?? []).map((c) => ({ x: c.center[0], y: c.center[1], z: c.center[2], hx: c.half[0], hz: c.half[2] })),
    course: m.course ?? [], spawns: (m.spawns ?? []).map((s) => [s[0], s[2]]), anchors,
  };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  mkdirSync(join(app, 'public/maps'), { recursive: true });
  for (const n of ['BlockPartyDash', 'GooTower', 'StarScramble']) {
    const b = bake(n);
    writeFileSync(join(app, `public/maps/${n}.board.json`), JSON.stringify(b));
    console.log(`board ${n}: ${b.shapes.length} shapes, bounds ${b.bounds.join(',')}, top ${b.top}`);
  }
}
