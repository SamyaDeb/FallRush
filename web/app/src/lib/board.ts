// Live board: a candy-coloured 2D view of every on-chain round, drawn from the same World the program steps.
//   static map art   ← /maps/<Map>.board.json (baked from maps/*.mapjson by scripts/bake-boards.mjs)
//   moving hazards   ← rush_core.wasm rc_hazards (platforms, doors, mallets, balls, blocks, goo, turn table, stars)
//   lids / sweepers  ← rc_lids / rc_sweepers (Lid Flip)
//   players + rules  ← rc_player + the World bytes (SlotRules: out / qualified / place / stars / height)
// Capsule Run follows the pack up a vertical track with a progress rail; Goo Tower shows the spiral from above with
// a height gauge and the rising goo; Star Fountain shows the arena, its stars and a star scoreboard; Lid Flip as before.
// Moments (OUT!, QUALIFIED, +1 ★, BONK) pop on the board; `onMoment` lets the page buzz the phone for your pick.
import { CoreWorld, CHAIN_MAPS, HZ, SF, worldInfo, type Hz, type WorldInfo } from './core';

export const SHELL = ['#FF3EA5', '#3FA9F5', '#FFD21F', '#5BE3B5', '#7B2FF7', '#FF8A3D', '#2EC4B6', '#E84855', '#FF9BD2', '#3A2E8C', '#FFE45C', '#B79CFF'];
const INK = '#2A1250';
const LID = ['#F9EDC2', '#FFD43B', '#2A1250', '#b9b2cc', '#FF8FB8'];
// pastel floor bands low → high, per round (Capsule Run, Goo Tower, Star Fountain, Lid Flip)
const BANDS = [
  ['#C9F2E3', '#FFF1B8', '#FFD6EC', '#D9CCFF'],
  ['#E4D9FF', '#D2C2FF', '#FFD6EC', '#FFF1B8'],
  ['#FFE2C6', '#FFF1B8', '#C9F2E3', '#D6ECFF'],
  ['#F9EDC2', '#F9EDC2', '#F9EDC2', '#F9EDC2'],
];
const RIM = (c: string) => shade(c, -0.28);
export const ROUND_NAMES = ['Capsule Run', 'Goo Tower', 'Star Fountain', 'Lid Flip'];

interface Board { name: string; killY: number; bounds: [number, number, number, number]; top: number; shapes: { p: [number, number][]; y0: number; y1: number; k: string }[]; checkpoints: { x: number; y: number; z: number; hx: number; hz: number }[]; course: number[][]; anchors: number[][] }
const boards: Record<string, Promise<Board | null>> = {};
const loaded: Record<string, Board | null> = {};
const onLoad = new Set<() => void>();
function board(name: string): Board | null | undefined {
  if (!(name in boards)) boards[name] = fetch(`/maps/${name}.board.json`).then((r) => (r.ok ? r.json() : null)).catch(() => null).then((b) => { loaded[name] = b; onLoad.forEach((f) => f()); return b; });
  return loaded[name];
}

export interface RankRow { slot: number; metric: string; state: 'in' | 'q' | 'out' | 'done'; human: boolean }
export interface BoardOpts {
  humans: number; label?: string; banner?: string;
  /** the viewer's pick (glows, the camera follows it in Capsule Run) */
  pick?: number | null;
  /** short odds text for a slot ("x3.2"), shown on the pick tag and the rank strip */
  odds?: (slot: number) => string | null;
  onMoment?: (slot: number, kind: 'out' | 'qualified' | 'star' | 'bonk') => void;
  /** between matches: the last World dimmed under this line */
  idle?: string;
}
interface Pop { x: number; z: number; text: string; color: string; t0: number; big?: boolean }
interface Confetto { x: number; y: number; vx: number; vy: number; c: string; r: number; t0: number }

export class LiveBoard {
  private shown: ({ x: number; z: number } | null)[] = Array(12).fill(null);
  private screen: ({ x: number; y: number } | null)[] = Array(12).fill(null);
  private prev: { tick: number; slots: WorldInfo['slots']; phase: number; mapKey: number } | null = null;
  private pops: Pop[] = [];
  private confetti: Confetto[] = [];
  private cam = { x: 0, z: 0, k: 0, key: -1 };
  private last = performance.now();
  private pattern: CanvasPattern | null = null;
  rank: RankRow[] = [];
  info: WorldInfo | null = null;
  round = -1;
  private again: (() => void) | null = null;
  constructor(private cv: HTMLCanvasElement) {
    for (const n of CHAIN_MAPS.slice(0, 3)) board(n); // preload the round art
    onLoad.add(() => this.again?.());
  }

  /** The slot drawn under a tap / click (CSS pixels relative to the canvas), or null. */
  hit(px: number, py: number): number | null {
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    let best: number | null = null, bd = (30 * dpr) ** 2;
    this.screen.forEach((p, s) => { if (!p) return; const d = (p.x - px * dpr) ** 2 + (p.y - py * dpr) ** 2; if (d < bd) { bd = d; best = s; } });
    return best;
  }

  draw(w: CoreWorld, o: BoardOpts) {
    this.again = () => { if (this.cv.isConnected) this.draw(w, o); };
    const now = performance.now(), dt = Math.min(0.1, (now - this.last) / 1000);
    this.last = now;
    const cv = this.cv, dpr = Math.min(2, window.devicePixelRatio || 1);
    const W = Math.round(cv.clientWidth * dpr), H = Math.round(cv.clientHeight * dpr);
    if (!W || !H) return;
    if (cv.width !== W || cv.height !== H) { cv.width = W; cv.height = H; }
    const g = cv.getContext('2d')!;
    const bytes = w.bytes();
    const info = worldInfo(bytes);
    this.info = info;
    const mk = info.mapKey < 4 ? info.mapKey : -1;
    this.round = mk;
    const players = Array.from({ length: 12 }, (_, i) => w.player(i));
    const lids = w.lids();
    const isLid = lids.length > 0;
    const round = isLid ? 3 : mk;
    const bd = (round >= 0 && round < 3 ? board(CHAIN_MAPS[round]) : null) ?? null;
    const hz = round >= 0 ? w.hazards() : [];

    // who is still playing this round (rules flags; the lobby / an older World: active + above the kill plane)
    const killY = bd?.killY ?? (isLid ? -6000 : -10000);
    const st = (s: number): RankRow['state'] => {
      const sl = info.slots[s], p = players[s];
      if (!p || !(p.flags & 1)) return 'out';
      if (sl.rf & (SF.OUT | SF.ELIMINATED)) return 'out';
      if (sl.rf & SF.QUALIFIED) return 'q';
      if (sl.rf & SF.DONE) return 'done';
      // a round under way: a slot with no round flags is not in this round (knocked out earlier, or an empty seat)
      if (round >= 0 && info.phase >= 1 && info.phase <= 4 && !(sl.rf & (SF.IN | SF.QUALIFIED | SF.DONE))) return 'out';
      if (p.flags & 4 || p.y < killY + 2000) return 'out';
      return 'in';
    };
    const states = players.map((_, s) => st(s));

    this.moments(info, players, states, o);

    // ---- camera
    const goo = hz.find((h) => h.k === HZ.GOO);
    let view = { minX: -8000, maxX: 8000, minZ: -8000, maxZ: 8000 };
    const live = players.map((p, s) => (p && states[s] !== 'out' ? p : null));
    if (isLid) {
      const xs = lids.map((l) => l.x), zs = lids.map((l) => l.z);
      view = { minX: Math.min(...xs) - 1500, maxX: Math.max(...xs) + 1500, minZ: Math.min(...zs) - 1500, maxZ: Math.max(...zs) + 1500 };
    } else if (bd && round !== 0) {
      const [a, b, c, d] = bd.bounds, pad = round === 2 ? 6500 : 1500;
      view = { minX: a - pad, maxX: c + pad, minZ: b - pad, maxZ: d + pad };
    } else if (!bd) {
      const ps = live.filter(Boolean) as { x: number; z: number }[];
      if (ps.length) view = { minX: Math.min(...ps.map((p) => p.x)) - 5000, maxX: Math.max(...ps.map((p) => p.x)) + 5000, minZ: Math.min(...ps.map((p) => p.z)) - 5000, maxZ: Math.max(...ps.map((p) => p.z)) + 5000 };
    }
    const railW = round === 0 || round === 1 ? 46 * dpr : 0; // the progress rail (left) / height gauge (right)
    const topH = round === 2 || round === 3 ? 40 * dpr : 0; // the star scoreboard / survivors strip
    const vw = W - railW, vh = H - topH;
    let k: number, cx: number, cz: number;
    if (round === 0 && bd) {
      // the race: the track's width fills the view; follow the pick, else the leader, eased
      k = vw / (bd.bounds[2] - bd.bounds[0] + 3000);
      const fol = o.pick != null && live[o.pick] ? live[o.pick]! : (live.filter(Boolean) as { z: number }[]).sort((a, b) => b.z - a.z)[0];
      const span = vh / k;
      const tz = fol ? fol.z + span * 0.18 : bd.bounds[1] + span / 2;
      cx = (bd.bounds[0] + bd.bounds[2]) / 2;
      cz = Math.max(bd.bounds[1] + span / 2 - 2000, Math.min(bd.bounds[3] - span / 2 + 3000, tz));
    } else {
      k = Math.min(vw / (view.maxX - view.minX), vh / (view.maxZ - view.minZ));
      cx = (view.minX + view.maxX) / 2; cz = (view.minZ + view.maxZ) / 2;
    }
    if (this.cam.key !== round || !this.cam.k) this.cam = { x: cx, z: cz, k, key: round };
    else { const e = 1 - Math.exp(-dt * 4); this.cam.x += (cx - this.cam.x) * e; this.cam.z += (cz - this.cam.z) * e; this.cam.k += (k - this.cam.k) * e; }
    const K = this.cam.k, ox = (round === 0 ? railW : 0) + vw / 2, oy = topH + vh / 2;
    const X = (x: number) => ox + (x - this.cam.x) * K, Z = (z: number) => oy - (z - this.cam.z) * K;

    // ---- background: the fall zone (soft sky + candy dots)
    if (!this.pattern) this.pattern = dots(g, dpr);
    const sky = g.createLinearGradient(0, 0, 0, H);
    sky.addColorStop(0, '#BEE3FF'); sky.addColorStop(1, '#E9F5FF');
    g.fillStyle = sky; g.fillRect(0, 0, W, H);
    if (this.pattern) { g.fillStyle = this.pattern; g.fillRect(0, 0, W, H); }

    const band = BANDS[Math.max(0, round)];
    const gooY = goo ? goo.y : -1e9;
    // ---- static map art (low → high, each with a soft drop shadow and a coloured rim)
    if (bd) {
      const lo = Math.min(...bd.shapes.map((s) => s.y1)), span = Math.max(1, bd.top - lo);
      for (const pass of [0, 1]) for (const s of bd.shapes) {
        const t = (s.y1 - lo) / span;
        const c = band[Math.min(band.length - 1, Math.floor(t * band.length))];
        g.beginPath();
        s.p.forEach(([x, z], i) => (i ? g.lineTo(X(x) + (pass ? 0 : 3 * dpr), Z(z) + (pass ? 0 : 4 * dpr)) : g.moveTo(X(x) + (pass ? 0 : 3 * dpr), Z(z) + (pass ? 0 : 4 * dpr))));
        g.closePath();
        if (!pass) { g.fillStyle = 'rgba(42,18,80,.13)'; g.fill(); continue; }
        const drowned = s.y1 < gooY;
        g.fillStyle = s.k === 'r' ? '#8C6CF0' : drowned ? '#FF8FC8' : c; g.fill();
        g.lineWidth = Math.max(1, 1.5 * dpr); g.strokeStyle = s.k === 'r' ? '#5B3BC4' : drowned ? '#E8559F' : RIM(c); g.stroke();
      }
      // race checkpoints / the finish; the tower's goal flag
      bd.checkpoints.forEach((c, i) => {
        const last = i === bd.checkpoints.length - 1;
        if (round === 0) {
          const x0 = X(c.x - c.hx - 2500), x1 = X(c.x + c.hx + 2500), y = Z(c.z), hh = Math.max(4 * dpr, 700 * K);
          if (last) checker(g, x0, y - hh, x1 - x0, hh * 2, Math.max(6, hh));
          else { g.fillStyle = 'rgba(255,62,165,.55)'; g.fillRect(x0, y - hh / 2, x1 - x0, hh); }
          label(g, last ? 'FINISH' : `CP ${i + 1}`, (x0 + x1) / 2, y - hh - 6 * dpr, dpr, last ? '#FF3EA5' : INK);
        } else if (round === 1 && last) {
          flag(g, X(c.x), Z(c.z), dpr);
        }
      });
    }

    // ---- moving hazards
    const tnow = now / 1000;
    for (const h of hz) drawHazard(g, h, X, Z, K, dpr, tnow);
    // ---- Lid Flip: the low grid first (faded), then the top grid
    if (isLid) {
      const top = splitGrids(lids.length);
      const pitch = lids.length > 1 ? Math.min(...lids.slice(1, Math.max(2, top)).map((l) => Math.hypot(l.x - lids[0].x, l.z - lids[0].z)).filter((d) => d > 0)) : 1200;
      const sz = pitch * K * 0.88;
      lids.forEach((l, i) => {
        if (i < top) return;
        g.globalAlpha = 0.35; g.fillStyle = LID[l.s] ?? '#eee'; g.beginPath(); g.roundRect(X(l.x) - sz / 2, Z(l.z) - sz / 2, sz, sz, sz * 0.18); g.fill(); g.globalAlpha = 1;
      });
      lids.forEach((l, i) => {
        if (i >= top) return;
        g.fillStyle = 'rgba(42,18,80,.15)'; g.beginPath(); g.roundRect(X(l.x) - sz / 2 + 2 * dpr, Z(l.z) - sz / 2 + 3 * dpr, sz, sz, sz * 0.18); g.fill();
        g.fillStyle = LID[l.s] ?? '#eee'; g.beginPath(); g.roundRect(X(l.x) - sz / 2, Z(l.z) - sz / 2, sz, sz, sz * 0.18); g.fill();
        g.lineWidth = (l.s === 1 || l.s === 4 ? 2.5 : 1.2) * dpr; g.strokeStyle = l.s === 1 || l.s === 4 ? '#FF2D8A' : l.s === 2 ? '#2A1250' : '#E4C978'; g.stroke();
      });
    }
    for (const s of w.sweepers()) {
      g.strokeStyle = '#FF2D8A'; g.lineWidth = Math.max(5 * dpr, 300 * K); g.lineCap = 'round';
      g.beginPath(); g.moveTo(X(s.px - s.c * s.arm), Z(s.pz - s.s * s.arm)); g.lineTo(X(s.px + s.c * s.arm), Z(s.pz + s.s * s.arm)); g.stroke();
      g.strokeStyle = 'rgba(255,255,255,.55)'; g.lineWidth = Math.max(1.5 * dpr, 80 * K);
      g.beginPath(); g.moveTo(X(s.px - s.c * s.arm), Z(s.pz - s.s * s.arm)); g.lineTo(X(s.px + s.c * s.arm), Z(s.pz + s.s * s.arm)); g.stroke();
      g.fillStyle = INK; g.beginPath(); g.arc(X(s.px), Z(s.pz), 5 * dpr, 0, 7); g.fill();
    }

    // ---- players (eased toward the World position; a jump > 4 m = a respawn: snap)
    const ease = 1 - Math.exp(-dt * 16);
    const leader = this.leaderOf(round, info, players, states);
    const order = players.map((_, s) => s).sort((a, b) => (players[a]?.y ?? 0) - (players[b]?.y ?? 0));
    this.screen = Array(12).fill(null);
    for (const s of order) {
      const p = players[s];
      if (!p || !(p.flags & 1)) { this.shown[s] = null; continue; }
      const sh = this.shown[s];
      if (!sh || Math.hypot(sh.x - p.x, sh.z - p.z) > 4000) this.shown[s] = { x: p.x, z: p.z };
      else { sh.x += (p.x - sh.x) * ease; sh.z += (p.z - sh.z) * ease; }
      const at = this.shown[s]!;
      if (states[s] === 'out') continue;
      const sx = X(at.x), sy = Z(at.z);
      if (sx < -40 * dpr || sx > W + 40 * dpr || sy < -40 * dpr || sy > H + 40 * dpr) continue;
      const lift = bd ? Math.max(0, Math.min(1, p.y / Math.max(1, bd.top))) : 0;
      const r = Math.max(9 * dpr, 430 * K) * (1 + lift * 0.25);
      token(g, sx, sy, r, s, p.yaw, dpr, { human: (o.humans >> s & 1) === 1, pick: o.pick === s, crown: leader === s, done: states[s] === 'q' || states[s] === 'done', stun: p.stun > 0, t: tnow, odds: o.pick === s ? o.odds?.(s) ?? null : null });
      this.screen[s] = { x: sx, y: sy };
    }

    // ---- pops + confetti
    this.pops = this.pops.filter((p) => now - p.t0 < 1300);
    for (const p of this.pops) {
      const a = (now - p.t0) / 1300, x = X(p.x), y = Z(p.z) - (24 + 40 * a) * dpr;
      g.globalAlpha = 1 - a * a;
      g.font = `${Math.round((p.big ? 22 : 16) * dpr * (1 + 0.25 * Math.sin(Math.min(1, a * 4) * Math.PI)))}px Lilita One`;
      g.textAlign = 'center'; g.lineWidth = 4 * dpr; g.strokeStyle = '#fff'; g.strokeText(p.text, x, y); g.fillStyle = p.color; g.fillText(p.text, x, y);
      g.globalAlpha = 1;
    }
    this.confetti = this.confetti.filter((c) => now - c.t0 < 2200);
    for (const c of this.confetti) {
      c.vy += 900 * dpr * dt; c.x += c.vx * dt; c.y += c.vy * dt;
      g.fillStyle = c.c; g.save(); g.translate(c.x, c.y); g.rotate(c.x * 0.05); g.fillRect(-c.r, -c.r / 2, c.r * 2, c.r); g.restore();
    }

    // ---- rails, gauges, scoreboards
    this.rank = this.ranking(round, info, players, states, o.humans, bd, goo);
    if (round === 0 && bd) this.progressRail(g, bd, players, states, H, railW, dpr, o);
    if (round === 1 && bd) this.heightGauge(g, bd, players, states, info, W, H, railW, dpr, o, goo);
    if (round === 2 || round === 3) this.topStrip(g, W, topH, dpr, round, info, o);

    // ---- countdown / banner / label
    if (o.idle) {
      g.fillStyle = 'rgba(255,255,255,.55)'; g.fillRect(0, 0, W, H);
      big(g, o.idle, W / 2, H / 2 - 14 * dpr, dpr, 30);
      g.font = `${Math.round(13 * dpr)}px Varela Round`; g.textAlign = 'center'; g.fillStyle = INK;
      g.fillText('the last match is shown · join from Play', W / 2, H / 2 + 26 * dpr);
    } else if (info.phase === 1 || info.phase === 2) {
      const left = Math.ceil((info.rushTick - info.tick) / 30);
      const txt = info.phase === 1 ? (round >= 0 ? ROUND_NAMES[round].toUpperCase() : 'GET READY') : left > 0 ? String(left) : 'GO!';
      big(g, txt, W / 2, H / 2, dpr, info.phase === 1 ? 34 : 64);
    } else if (info.phase === 4 && round >= 0) big(g, 'ROUND OVER', W / 2, H / 2, dpr, 30);
    if (o.banner) { g.fillStyle = 'rgba(255,62,165,.94)'; g.fillRect(0, topH, W, 34 * dpr); g.fillStyle = '#fff'; g.font = `${Math.round(18 * dpr)}px Lilita One`; g.textAlign = 'center'; g.fillText(o.banner, W / 2, topH + 24 * dpr); }
    if (o.label) {
      g.font = `${Math.round(11.5 * dpr)}px Varela Round`; g.textAlign = 'left';
      const tw = g.measureText(o.label).width, lx = (round === 0 ? railW : 0) + 8 * dpr;
      g.fillStyle = 'rgba(255,255,255,.8)'; g.beginPath(); g.roundRect(lx - 4 * dpr, H - 24 * dpr, tw + 8 * dpr, 18 * dpr, 8 * dpr); g.fill();
      g.fillStyle = INK; g.fillText(o.label, lx, H - 11 * dpr);
    }
  }

  /** OUT / QUALIFIED / +1 ★ / BONK pops from the change of the World since the last frame (skipped on a scrub). */
  private moments(info: WorldInfo, players: ReturnType<CoreWorld['player']>[], states: RankRow['state'][], o: BoardOpts) {
    const pv = this.prev;
    this.prev = { tick: info.tick, slots: info.slots, phase: info.phase, mapKey: info.mapKey };
    if (!pv || pv.mapKey !== info.mapKey || info.tick < pv.tick || info.tick - pv.tick > 90) return;
    const now = performance.now();
    if (pv.phase !== 4 && info.phase === 4 && info.mapKey < 4) this.burst(60);
    if (pv.phase !== 3 && info.phase === 3) this.burst(24);
    info.slots.forEach((s, i) => {
      const a = pv.slots[i], p = players[i];
      if (!p || !s.active) return;
      const at = this.shown[i] ?? { x: p.x, z: p.z };
      const add = (text: string, color: string, kind: 'out' | 'qualified' | 'star' | 'bonk', big = false) => { this.pops.push({ x: at.x, z: at.z, text, color, t0: now, big }); o.onMoment?.(i, kind); };
      if (!(a.rf & (SF.OUT | SF.ELIMINATED)) && s.rf & (SF.OUT | SF.ELIMINATED)) add(`#${i + 1} OUT!`, '#E84855', 'out', true);
      else if (!(a.rf & SF.QUALIFIED) && s.rf & SF.QUALIFIED) { add(`#${i + 1} QUALIFIED`, '#16A37F', 'qualified', true); if (o.pick === i) this.burst(40); }
      if (s.score > a.score) add(`+${s.score - a.score} ★`, '#E0A800', 'star');
      if (a.stun === 0 && s.stun > 0 && states[i] !== 'out') add('BONK!', '#7B2FF7', 'bonk');
    });
  }
  private burst(n: number) {
    const W = this.cv.width, now = performance.now(), dpr = Math.min(2, window.devicePixelRatio || 1);
    for (let i = 0; i < n; i++) this.confetti.push({ x: Math.random() * W, y: -10, vx: (Math.random() - 0.5) * 300 * dpr, vy: Math.random() * 200 * dpr, c: SHELL[i % SHELL.length], r: (3 + Math.random() * 3) * dpr, t0: now });
  }

  private leaderOf(round: number, info: WorldInfo, players: ReturnType<CoreWorld['player']>[], states: RankRow['state'][]): number | null {
    const r = this.ranking(round, info, players, states, 0, round >= 0 && round < 3 ? loaded[CHAIN_MAPS[round]] ?? null : null, undefined);
    return r.length && r[0].state !== 'out' && round >= 0 ? r[0].slot : null;
  }

  /** Live order of the round: race progress, tower height, stars, Final survivors. */
  private ranking(round: number, info: WorldInfo, players: ReturnType<CoreWorld['player']>[], states: RankRow['state'][], humans: number, bd: Board | null, goo: Hz | undefined): RankRow[] {
    const rows: (RankRow & { v: number })[] = [];
    players.forEach((p, s) => {
      if (!p || !(p.flags & 1)) return;
      const sl = info.slots[s], state = states[s];
      let v = 0, metric = '';
      if (round === 0) {
        const fin = (bd ? bd.checkpoints[bd.checkpoints.length - 1] : undefined)?.z ?? 222000;
        v = sl.place ? 1e9 - sl.place : p.z; metric = sl.place ? `#${sl.place} ✓` : `${Math.max(0, Math.min(100, Math.round((p.z / fin) * 100)))}%`;
      } else if (round === 1) { v = Math.max(sl.height, p.y); metric = `${(Math.max(0, v) / 1000).toFixed(1)} m`; }
      else if (round === 2) { v = sl.score; metric = `${sl.score} ★`; }
      else { v = state === 'out' ? -1 : 1; metric = state === 'out' ? 'out' : 'alive'; }
      if (state === 'out') v -= 1e12;
      rows.push({ slot: s, metric, state, human: (humans >> s & 1) === 1, v });
    });
    rows.sort((a, b) => b.v - a.v || a.slot - b.slot);
    void goo;
    return rows.map(({ v, ...r }) => (void v, r));
  }

  private progressRail(g: CanvasRenderingContext2D, bd: Board, players: ReturnType<CoreWorld['player']>[], states: RankRow['state'][], H: number, railW: number, dpr: number, o: BoardOpts) {
    const fin = bd.checkpoints[bd.checkpoints.length - 1]?.z ?? 222000, y0 = H - 20 * dpr, y1 = 26 * dpr, x = railW / 2;
    g.fillStyle = 'rgba(255,255,255,.88)'; g.fillRect(0, 0, railW, H);
    g.strokeStyle = '#E4DAF8'; g.lineWidth = 1 * dpr; g.beginPath(); g.moveTo(railW, 0); g.lineTo(railW, H); g.stroke();
    g.lineCap = 'round'; g.strokeStyle = '#D9CCFF'; g.lineWidth = 8 * dpr; g.beginPath(); g.moveTo(x, y0); g.lineTo(x, y1); g.stroke();
    const Y = (z: number) => y0 + (y1 - y0) * Math.max(0, Math.min(1, z / fin));
    bd.checkpoints.forEach((c, i) => { const y = Y(c.z), last = i === bd.checkpoints.length - 1; g.fillStyle = last ? '#FF3EA5' : '#B79CFF'; g.fillRect(x - 12 * dpr, y - 1.5 * dpr, 24 * dpr, 3 * dpr); });
    flag(g, x + 4 * dpr, y1 - 2 * dpr, dpr * 0.7);
    players.forEach((p, s) => {
      if (!p || !(p.flags & 1) || states[s] === 'out') return;
      const y = Y(p.z), r = (o.pick === s ? 9 : 7) * dpr;
      g.fillStyle = SHELL[s]; g.beginPath(); g.arc(x, y, r, 0, 7); g.fill();
      g.lineWidth = (o.pick === s ? 3 : 1.5) * dpr; g.strokeStyle = o.pick === s ? '#FFD21F' : INK; g.stroke();
      g.fillStyle = '#fff'; g.font = `${Math.round(8.5 * dpr)}px Varela Round`; g.textAlign = 'center'; g.fillText(String(s + 1), x, y + 3 * dpr);
      this.screen[s] ??= { x, y };
    });
  }

  private heightGauge(g: CanvasRenderingContext2D, bd: Board, players: ReturnType<CoreWorld['player']>[], states: RankRow['state'][], info: WorldInfo, W: number, H: number, railW: number, dpr: number, o: BoardOpts, goo: Hz | undefined) {
    const goal = bd.checkpoints[bd.checkpoints.length - 1]?.y ?? bd.top, x0 = W - railW, x = x0 + railW / 2, y0 = H - 20 * dpr, y1 = 30 * dpr;
    const Y = (y: number) => y0 + (y1 - y0) * Math.max(0, Math.min(1, y / goal));
    g.fillStyle = 'rgba(255,255,255,.9)'; g.fillRect(x0, 0, railW, H);
    g.strokeStyle = '#E4DAF8'; g.lineWidth = 1 * dpr; g.beginPath(); g.moveTo(x0, 0); g.lineTo(x0, H); g.stroke();
    g.fillStyle = '#EDE6FF'; g.beginPath(); g.roundRect(x - 7 * dpr, y1, 14 * dpr, y0 - y1, 7 * dpr); g.fill();
    if (goo) {
      const gy = Y(goo.y);
      const grad = g.createLinearGradient(0, gy, 0, y0); grad.addColorStop(0, '#FF6FB5'); grad.addColorStop(1, '#E8559F');
      g.fillStyle = grad; g.beginPath(); g.roundRect(x - 7 * dpr, gy, 14 * dpr, Math.max(0, y0 - gy), 7 * dpr); g.fill();
      g.fillStyle = '#E8559F'; g.font = `${Math.round(9 * dpr)}px Varela Round`; g.textAlign = 'center'; g.fillText('goo', x, Math.min(y0 + 13 * dpr, gy + 12 * dpr));
    }
    flag(g, x + 2 * dpr, y1 - 4 * dpr, dpr * 0.7);
    players.forEach((p, s) => {
      if (!p || !(p.flags & 1) || states[s] === 'out') return;
      const y = Y(Math.max(info.slots[s].height, p.y)), r = (o.pick === s ? 9 : 7) * dpr, xx = x + ((s % 2) * 2 - 1) * 9 * dpr;
      g.fillStyle = SHELL[s]; g.beginPath(); g.arc(xx, y, r, 0, 7); g.fill();
      g.lineWidth = (o.pick === s ? 3 : 1.5) * dpr; g.strokeStyle = o.pick === s ? '#FFD21F' : INK; g.stroke();
      g.fillStyle = '#fff'; g.font = `${Math.round(8.5 * dpr)}px Varela Round`; g.textAlign = 'center'; g.fillText(String(s + 1), xx, y + 3 * dpr);
    });
  }

  private topStrip(g: CanvasRenderingContext2D, W: number, h: number, dpr: number, round: number, info: WorldInfo, o: BoardOpts) {
    g.fillStyle = 'rgba(255,255,255,.92)'; g.fillRect(0, 0, W, h);
    g.strokeStyle = '#E4DAF8'; g.lineWidth = 1 * dpr; g.beginPath(); g.moveTo(0, h); g.lineTo(W, h); g.stroke();
    const rows = this.rank.filter((r) => r.state !== 'out');
    const q = info.qualifyCount || (round === 3 ? 1 : 0);
    const cw = Math.min(58 * dpr, (W - 8 * dpr) / Math.max(1, rows.length)), cr = Math.min(9 * dpr, cw * 0.2);
    rows.forEach((r, i) => {
      const x = 4 * dpr + i * cw + cw / 2, y = h / 2;
      if (round === 2 && q && i === q) { g.strokeStyle = '#FF3EA5'; g.setLineDash([3 * dpr, 3 * dpr]); g.beginPath(); g.moveTo(x - cw / 2, 4 * dpr); g.lineTo(x - cw / 2, h - 4 * dpr); g.stroke(); g.setLineDash([]); }
      g.fillStyle = SHELL[r.slot]; g.beginPath(); g.arc(x - cw * 0.18, y, cr, 0, 7); g.fill();
      g.lineWidth = (o.pick === r.slot ? 3 : 1.5) * dpr; g.strokeStyle = o.pick === r.slot ? '#FFD21F' : INK; g.stroke();
      g.fillStyle = '#fff'; g.font = `${Math.round(cr * 0.95)}px Varela Round`; g.textAlign = 'center'; g.fillText(String(r.slot + 1), x - cw * 0.18, y + cr * 0.33);
      if (round === 2) { g.fillStyle = INK; g.font = `${Math.round(Math.min(13 * dpr, cw * 0.3))}px Lilita One`; g.textAlign = 'left'; g.fillText(String(info.slots[r.slot].score), x - cw * 0.18 + cr + 2 * dpr, y + 4 * dpr); }
    });
    if (round === 3) { g.fillStyle = INK; g.font = `${Math.round(12 * dpr)}px Lilita One`; g.textAlign = 'right'; g.fillText(`${rows.length} ALIVE`, W - 8 * dpr, h / 2 + 4 * dpr); }
  }
}

// ------------------------------------------------------------------ drawing helpers
function drawHazard(g: CanvasRenderingContext2D, h: Hz, X: (x: number) => number, Z: (z: number) => number, K: number, dpr: number, t: number) {
  const x = X(h.x), y = Z(h.z);
  const rect = (hx: number, hz: number, fill: string, rim: string, r = 0.25) => {
    const w = hx * 2 * K, d = hz * 2 * K;
    g.fillStyle = 'rgba(42,18,80,.16)'; g.beginPath(); g.roundRect(x - w / 2 + 3 * dpr, y - d / 2 + 4 * dpr, w, d, Math.min(w, d) * r); g.fill();
    g.fillStyle = fill; g.beginPath(); g.roundRect(x - w / 2, y - d / 2, w, d, Math.min(w, d) * r); g.fill();
    g.lineWidth = 1.8 * dpr; g.strokeStyle = rim; g.stroke();
  };
  switch (h.k) {
    case HZ.PLATFORM: rect(h.a, h.b, '#FFC58A', '#E08A3C'); break;
    case HZ.SPRING: rect(h.a, h.b, '#7CEBC4', '#16A37F'); zig(g, x, y, Math.min(h.a, h.b) * K, dpr); break;
    case HZ.CONVEYOR: {
      rect(h.a, h.b, '#C9D3E8', '#7D8BB0', 0.1);
      const along = h.b > h.a, n = Math.max(2, Math.floor((along ? h.b : h.a) / 1500));
      g.strokeStyle = '#7D8BB0'; g.lineWidth = 2 * dpr;
      for (let i = 0; i < n; i++) { const f = ((i + (t * 0.8) % 1) / n - 0.5) * 2 * (along ? h.b : h.a) * K, s = Math.min(h.a, h.b) * K * 0.5; g.beginPath(); if (along) { g.moveTo(x - s, y - f + s * 0.5); g.lineTo(x, y - f - s * 0.3); g.lineTo(x + s, y - f + s * 0.5); } else { g.moveTo(x + f - s * 0.5, y - s); g.lineTo(x + f + s * 0.3, y); g.lineTo(x + f - s * 0.5, y + s); } g.stroke(); }
      break;
    }
    case HZ.DOOR: if (h.s) { g.setLineDash([4 * dpr, 4 * dpr]); g.strokeStyle = 'rgba(232,72,85,.6)'; g.lineWidth = 2 * dpr; g.strokeRect(x - h.a * K, y - h.b * K, h.a * 2 * K, h.b * 2 * K); g.setLineDash([]); } else rect(Math.max(h.a, 120), Math.max(h.b, 120), '#FF6F7F', '#B8283A', 0.4); break;
    case HZ.BLOCK: if (h.s) { g.setLineDash([4 * dpr, 4 * dpr]); g.strokeStyle = 'rgba(42,18,80,.4)'; g.lineWidth = 2 * dpr; g.strokeRect(x - h.a * K, y - h.b * K, h.a * 2 * K, h.b * 2 * K); g.setLineDash([]); } else rect(h.a, h.b, '#B79CFF', '#6A4BD8', 0.15); break;
    case HZ.TABLE: {
      const r = h.a * K, ang = (h.b / 65536) * Math.PI * 2;
      g.fillStyle = 'rgba(42,18,80,.15)'; g.beginPath(); g.arc(x + 3 * dpr, y + 4 * dpr, r, 0, 7); g.fill();
      for (let i = 0; i < 8; i++) { g.fillStyle = i % 2 ? '#FFD6EC' : '#FFF1B8'; g.beginPath(); g.moveTo(x, y); g.arc(x, y, r, -ang + (i * Math.PI) / 4, -ang + ((i + 1) * Math.PI) / 4); g.closePath(); g.fill(); }
      g.lineWidth = 2.5 * dpr; g.strokeStyle = '#E58FB8'; g.beginPath(); g.arc(x, y, r, 0, 7); g.stroke();
      break;
    }
    case HZ.BUMPER: { const r = Math.max(5 * dpr, h.a * K); g.fillStyle = '#FFE45C'; g.beginPath(); g.arc(x, y, r, 0, 7); g.fill(); g.lineWidth = 3 * dpr; g.strokeStyle = '#FF3EA5'; g.stroke(); break; }
    case HZ.MALLET: { const r = Math.max(6 * dpr, h.a * K); ball(g, x, y, r, '#FF5FA2', '#B8236A', dpr); break; }
    case HZ.BALL: { const r = Math.max(6 * dpr, h.a * K); ball(g, x, y, r, '#8E6CFF', '#4B2BB8', dpr); break; }
    case HZ.STAR: { const tw = 1 + 0.12 * Math.sin(t * 6 + h.i), r = Math.max(7 * dpr, 420 * K) * (h.s ? 1.5 : 1) * tw; if (h.s) { g.fillStyle = 'rgba(255,214,0,.35)'; g.beginPath(); g.arc(x, y, r * 1.6, 0, 7); g.fill(); } star(g, x, y, r, h.s ? '#FFB800' : '#FFD21F', dpr, t + h.i); break; }
    case HZ.DROPPED: star(g, x, y - Math.abs(Math.sin(t * 7 + h.i)) * 6 * dpr, Math.max(5 * dpr, 300 * K), '#FFE45C', dpr, t); break;
    case HZ.GOO: break; // drawn as the drowned floors + the height gauge
  }
}
function token(g: CanvasRenderingContext2D, x: number, y: number, r: number, s: number, yaw: number, dpr: number, f: { human: boolean; pick: boolean; crown: boolean; done: boolean; stun: boolean; t: number; odds: string | null }) {
  if (f.pick) { const pr = r * (1.55 + 0.18 * Math.sin(f.t * 5)); g.fillStyle = 'rgba(255,210,31,.35)'; g.beginPath(); g.arc(x, y, pr, 0, 7); g.fill(); g.lineWidth = 3 * dpr; g.strokeStyle = '#FFD21F'; g.stroke(); }
  g.fillStyle = 'rgba(42,18,80,.22)'; g.beginPath(); g.ellipse(x + 2 * dpr, y + r * 0.55, r * 0.95, r * 0.5, 0, 0, 7); g.fill();
  // facing nub (yaw 0 = +Z = up the screen, growing toward +X = right)
  const a = (yaw / 256) * Math.PI * 2, nx = Math.sin(a), ny = -Math.cos(a);
  g.fillStyle = shade(SHELL[s], -0.25); g.beginPath(); g.moveTo(x + nx * r * 1.45, y + ny * r * 1.45); g.lineTo(x + ny * r * 0.6, y - nx * r * 0.6); g.lineTo(x - ny * r * 0.6, y + nx * r * 0.6); g.closePath(); g.fill();
  g.fillStyle = SHELL[s]; g.beginPath(); g.arc(x, y, r, 0, 7); g.fill();
  g.fillStyle = 'rgba(255,255,255,.45)'; g.beginPath(); g.ellipse(x - r * 0.3, y - r * 0.35, r * 0.42, r * 0.26, -0.5, 0, 7); g.fill();
  g.lineWidth = (f.human ? 3.5 : 2) * dpr; g.strokeStyle = f.human ? '#FFD21F' : INK; g.beginPath(); g.arc(x, y, r, 0, 7); g.stroke();
  g.fillStyle = '#fff'; g.font = `${Math.round(Math.max(10 * dpr, r * 0.95))}px Lilita One`; g.textAlign = 'center'; g.lineWidth = 2.5 * dpr; g.strokeStyle = 'rgba(42,18,80,.6)'; g.strokeText(String(s + 1), x, y + r * 0.34); g.fillText(String(s + 1), x, y + r * 0.34);
  if (f.done) { g.fillStyle = '#16A37F'; g.beginPath(); g.arc(x + r * 0.8, y - r * 0.8, r * 0.45, 0, 7); g.fill(); g.strokeStyle = '#fff'; g.lineWidth = 2 * dpr; g.beginPath(); g.moveTo(x + r * 0.6, y - r * 0.8); g.lineTo(x + r * 0.76, y - r * 0.62); g.lineTo(x + r * 1.02, y - r * 0.98); g.stroke(); }
  if (f.crown) crown(g, x, y - r * 1.15, r * 0.75, dpr);
  if (f.stun) for (let i = 0; i < 3; i++) { const a2 = f.t * 5 + (i * Math.PI * 2) / 3; star(g, x + Math.cos(a2) * r * 1.1, y - r * 0.9 + Math.sin(a2) * r * 0.35, r * 0.32, '#FFD21F', dpr, 0); }
  if (f.pick) {
    const txt = f.odds ? `YOUR PICK ${f.odds}` : 'YOUR PICK';
    g.font = `${Math.round(10.5 * dpr)}px Lilita One`; const tw = g.measureText(txt).width + 12 * dpr, ty = y + r * 1.4 + 12 * dpr;
    g.fillStyle = '#FFD21F'; g.beginPath(); g.roundRect(x - tw / 2, ty - 11 * dpr, tw, 16 * dpr, 8 * dpr); g.fill(); g.lineWidth = 1.5 * dpr; g.strokeStyle = INK; g.stroke();
    g.fillStyle = INK; g.textAlign = 'center'; g.fillText(txt, x, ty + 1 * dpr);
  }
}
function ball(g: CanvasRenderingContext2D, x: number, y: number, r: number, c: string, rim: string, dpr: number) {
  g.fillStyle = 'rgba(42,18,80,.2)'; g.beginPath(); g.arc(x + 2 * dpr, y + 3 * dpr, r, 0, 7); g.fill();
  g.fillStyle = c; g.beginPath(); g.arc(x, y, r, 0, 7); g.fill(); g.lineWidth = 2 * dpr; g.strokeStyle = rim; g.stroke();
  g.fillStyle = 'rgba(255,255,255,.5)'; g.beginPath(); g.arc(x - r * 0.35, y - r * 0.35, r * 0.3, 0, 7); g.fill();
}
function star(g: CanvasRenderingContext2D, x: number, y: number, r: number, c: string, dpr: number, rot: number) {
  g.beginPath();
  for (let i = 0; i < 10; i++) { const a = rot * 0.6 - Math.PI / 2 + (i * Math.PI) / 5, rr = i % 2 ? r * 0.45 : r; i ? g.lineTo(x + Math.cos(a) * rr, y + Math.sin(a) * rr) : g.moveTo(x + Math.cos(a) * rr, y + Math.sin(a) * rr); }
  g.closePath(); g.fillStyle = c; g.fill(); g.lineWidth = Math.max(1, 1.4 * dpr); g.strokeStyle = '#B07800'; g.stroke();
}
function crown(g: CanvasRenderingContext2D, x: number, y: number, s: number, dpr: number) {
  g.beginPath(); g.moveTo(x - s, y + s * 0.5); g.lineTo(x - s, y - s * 0.3); g.lineTo(x - s * 0.5, y + s * 0.1); g.lineTo(x, y - s * 0.6); g.lineTo(x + s * 0.5, y + s * 0.1); g.lineTo(x + s, y - s * 0.3); g.lineTo(x + s, y + s * 0.5); g.closePath();
  g.fillStyle = '#FFD21F'; g.fill(); g.lineWidth = 1.5 * dpr; g.strokeStyle = INK; g.stroke();
}
function flag(g: CanvasRenderingContext2D, x: number, y: number, dpr: number) {
  g.strokeStyle = INK; g.lineWidth = 2 * dpr; g.beginPath(); g.moveTo(x, y + 16 * dpr); g.lineTo(x, y - 14 * dpr); g.stroke();
  g.fillStyle = '#FF3EA5'; g.beginPath(); g.moveTo(x, y - 14 * dpr); g.lineTo(x + 14 * dpr, y - 9 * dpr); g.lineTo(x, y - 4 * dpr); g.closePath(); g.fill();
}
function zig(g: CanvasRenderingContext2D, x: number, y: number, s: number, dpr: number) {
  g.strokeStyle = '#0E7A5C'; g.lineWidth = 1.6 * dpr; g.beginPath(); for (let i = 0; i <= 4; i++) { const px = x - s * 0.6 + (i * s * 1.2) / 4, py = y + (i % 2 ? -s * 0.35 : s * 0.35); i ? g.lineTo(px, py) : g.moveTo(px, py); } g.stroke();
}
function checker(g: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, c: number) {
  for (let i = 0; i * c < w; i++) for (let j = 0; j * c < h; j++) { g.fillStyle = (i + j) % 2 ? '#fff' : INK; g.fillRect(x + i * c, y + j * c, Math.min(c, w - i * c), Math.min(c, h - j * c)); }
}
function label(g: CanvasRenderingContext2D, t: string, x: number, y: number, dpr: number, c: string) {
  g.font = `${Math.round(11 * dpr)}px Lilita One`; g.textAlign = 'center'; g.lineWidth = 3 * dpr; g.strokeStyle = '#fff'; g.strokeText(t, x, y); g.fillStyle = c; g.fillText(t, x, y);
}
function big(g: CanvasRenderingContext2D, t: string, x: number, y: number, dpr: number, size: number) {
  g.font = `${Math.round(size * dpr)}px Lilita One`; g.textAlign = 'center'; g.lineJoin = 'round';
  g.lineWidth = 8 * dpr; g.strokeStyle = INK; g.strokeText(t, x, y + size * dpr * 0.35); g.fillStyle = '#FFD21F'; g.fillText(t, x, y + size * dpr * 0.35);
}
function dots(g: CanvasRenderingContext2D, dpr: number): CanvasPattern | null {
  const c = document.createElement('canvas'), s = Math.round(28 * dpr); c.width = c.height = s;
  const d = c.getContext('2d')!; d.fillStyle = 'rgba(255,255,255,.55)'; d.beginPath(); d.arc(s / 4, s / 4, 2.2 * dpr, 0, 7); d.arc((3 * s) / 4, (3 * s) / 4, 2.2 * dpr, 0, 7); d.fill();
  return g.createPattern(c, 'repeat');
}
/** Lid Flip has two grids (9 × 9 on top, 5 × 5 below): the first grid's count, both square. */
function splitGrids(n: number): number {
  for (let a = Math.floor(Math.sqrt(n)); a >= 1; a--) { const rest = n - a * a, b = Math.round(Math.sqrt(rest)); if (b * b === rest) return a * a; }
  return n;
}
export function shade(hex: string, f: number): string {
  const n = parseInt(hex.slice(1), 16), ch = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map((v) => Math.round(f < 0 ? v * (1 + f) : v + (255 - v) * f));
  return `rgb(${ch[0]},${ch[1]},${ch[2]})`;
}

/** The old one-call API (replay): one board per canvas. */
const perCanvas = new WeakMap<HTMLCanvasElement, LiveBoard>();
export function draw(cv: HTMLCanvasElement, w: CoreWorld, opts: BoardOpts): LiveBoard {
  let b = perCanvas.get(cv);
  if (!b) perCanvas.set(cv, (b = new LiveBoard(cv)));
  b.draw(w, opts);
  return b;
}
