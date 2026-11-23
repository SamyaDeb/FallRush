/**
 * The six story chapters. Everything that tells the story is a pure function of the chapter's local progress `lp`
 * (scroll up and it rewinds); only ambient motion (spin, bob, ripples) uses time. Each chapter writes the 12-Rushling
 * cast (positions, yaw, tumble, run, visibility) and its camera (position + look target).
 */
import * as THREE from 'three';
import type { RushlingRig } from '../Rushling';
import { Confetti, cloudMaterial, makeCloud } from '../common';
import { QUALITY } from '../quality';
import { checkerTexture } from '../textures';
import { propMaterial, type PropName } from '../props';
import { play } from '../../lib/sfx';
import { backOut, ease, easeIn, easeOut, span } from '../../lib/scroll';
import { C, Merge, RIMS, hash, makeCrown, paintSplit, rbox, softMaterial } from './kit';
import { KNOCKOUTS, type ChapterId } from './timeline';

export interface CastMember {
  rig: RushlingRig;
  pos: THREE.Vector3;
  yaw: number;
  rx: number;
  rz: number;
  run: number;
  vis: boolean;
  scale: number;
  celebrate: boolean;
}

export interface Ctx {
  t: number;
  dt: number;
  /** |d lp / dt| of this chapter: the cast runs while the visitor scrolls */
  speed: number;
  cast: CastMember[];
  props: Record<PropName, THREE.BufferGeometry>;
  kick: (amount: number) => void;
  reduced: boolean;
}

export interface CamPose {
  pos: THREE.Vector3;
  look: THREE.Vector3;
}

export interface Chapter {
  id: ChapterId;
  group: THREE.Group;
  /** where the sun / shadow camera centres */
  focus: THREE.Vector3;
  update(lp: number, ctx: Ctx): void;
  cam(lp: number, out: CamPose): void;
  /** 0..1 how "on-chain night" the sky is in this chapter */
  digital?(lp: number): number;
  dispose(): void;
}

const V = (x = 0, y = 0, z = 0) => new THREE.Vector3(x, y, z);
const G = 22; // story gravity
const SHADOWS = QUALITY.shadows;

/** ballistic offset after a knockout */
function launch(out: THREE.Vector3, from: THREE.Vector3, v: THREE.Vector3, tau: number) {
  out.set(from.x + v.x * tau, from.y + v.y * tau - 0.5 * G * tau * tau, from.z + v.z * tau);
  return out;
}

function prop(ctx: { props: Record<PropName, THREE.BufferGeometry> }, name: PropName, at: THREE.Vector3 | [number, number, number], scale = 1, rotY = 0) {
  const m = new THREE.Mesh(ctx.props[name], propMaterial);
  if (Array.isArray(at)) m.position.set(at[0], at[1], at[2]);
  else m.position.copy(at);
  m.scale.setScalar(scale);
  m.rotation.y = rotY;
  m.castShadow = SHADOWS;
  m.receiveShadow = SHADOWS;
  return m;
}

/** Instanced rounded tiles with a white top / lavender side (instance colour tints both: pastel top, deeper side). */
function tileMesh(n: number, w: number, h: number, d: number) {
  const geo = paintSplit(rbox(w, h, d, 0.12), '#FFFFFF', '#C9BEEA', h / 2 - 0.1);
  const mesh = new THREE.InstancedMesh(geo, new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.5 }), n);
  mesh.castShadow = SHADOWS;
  mesh.receiveShadow = SHADOWS;
  mesh.frustumCulled = false;
  return mesh;
}

function clouds(group: THREE.Group, n: number, centre: THREE.Vector3, spread: THREE.Vector3, size: [number, number], seed: number) {
  const mat = cloudMaterial();
  const list: { o: THREE.Object3D; y: number; ph: number }[] = [];
  for (let i = 0; i < n; i++) {
    const cl = makeCloud(mat, size[0] + hash(seed + i) * (size[1] - size[0]));
    const y = centre.y + (hash(seed + i * 3.1) - 0.5) * spread.y;
    cl.position.set(centre.x + (hash(seed + i * 1.7) - 0.5) * spread.x, y, centre.z + (hash(seed + i * 2.3) - 0.5) * spread.z);
    group.add(cl);
    list.push({ o: cl, y, ph: hash(seed + i * 5.3) * 6 });
  }
  return list;
}

const tmpV = V(), tmpV2 = V();
const dummy = new THREE.Object3D();
const col = new THREE.Color();

// ---------------------------------------------------------------------------------------------------- 1. the fall

/** Where each Rushling lands on Capsule Run's start deck (fall end = race start). */
export const RUN_START = Array.from({ length: 12 }, (_, i) => V(-3.3 + (i % 6) * 1.32, 0, 1.0 + Math.floor(i / 6) * 1.7));

export function fallChapter(): Chapter {
  const group = new THREE.Group();
  const F = V(0, 46, 0);
  const N = 36;
  const tiles = tileMesh(N, 1.62, 0.42, 1.62);
  group.add(tiles);
  const tileAt: THREE.Vector3[] = [], tileT: number[] = [], tileCol: THREE.Color[] = [];
  for (let k = 0; k < N; k++) {
    const ix = k % 6, iz = Math.floor(k / 6);
    tileAt.push(V(F.x + (ix - 2.5) * 1.7, F.y, F.z + (iz - 2.5) * 1.7));
    const ring = Math.floor(Math.max(Math.abs(ix - 2.5), Math.abs(iz - 2.5)));
    tileCol.push(new THREE.Color([C.soda, C.tangerine, C.bubblegum][ring]));
    tileT.push(0.2 + hash(k * 7.7) * 0.16 + (2 - ring) * 0.03);
  }
  // each Rushling stands on a central tile; it drops with the tile
  const standTile = Array.from({ length: 12 }, (_, i) => 6 * (1 + Math.floor(i / 4)) + 1 + (i % 4));
  const startAt = standTile.map((k) => tileAt[k].clone().setY(F.y + 0.21));

  // a pennant ring round the platform + a rainbow ribbon
  const deco = new THREE.Group();
  group.add(deco);
  const cloudList = clouds(group, QUALITY.tier === 'high' ? 22 : 12, V(0, 26, 0), V(44, 22, 44), [1.4, 2.8], 11);
  // clouds under the platform (look-down shot)
  cloudList.push(...clouds(group, 8, V(0, 38, 0), V(30, 3, 30), [1.6, 2.6], 41));

  // wind streaks around the camera while falling
  const SN = 60;
  const streaks = new THREE.InstancedMesh(
    new THREE.BoxGeometry(0.035, 1.8, 0.035),
    new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.55, depthWrite: false }),
    SN,
  );
  streaks.frustumCulled = false;
  group.add(streaks);

  const centroid = V();
  let prev = 0;

  const fallU = (i: number, lp: number) => span(lp, tileT[standTile[i]] + 0.02, 0.9);
  const castPos = (i: number, lp: number, out: THREE.Vector3) => {
    const u = fallU(i, lp);
    const s = startAt[i], e = RUN_START[i];
    const sw = Math.sin(Math.PI * u) * 2.4;
    // swirl out and back in on the way down; vertical eases off the tile and into the landing
    return out.set(
      s.x + (e.x - s.x) * ease(u) + Math.cos(i * 1.7 + u * 5) * sw,
      s.y + (e.y - s.y) * ease(u),
      s.z + (e.z - s.z) * ease(u) + Math.sin(i * 2.3 + u * 4) * sw,
    );
  };

  return {
    id: 'fall',
    group,
    focus: centroid,
    update(lp, ctx) {
      const { t } = ctx;
      // tiles: light up, then drop and tumble
      for (let k = 0; k < N; k++) {
        const tt = tileT[k];
        const lit = span(lp, tt - 0.07, tt);
        const u = span(lp, tt, tt + 0.3);
        const p = tileAt[k];
        const shake = lit > 0 && u === 0 ? Math.sin(t * 50 + k) * 0.03 * lit : 0;
        dummy.position.set(p.x + shake, p.y - 0.21 - easeIn(u) * 46, p.z);
        dummy.rotation.set(u * (hash(k) - 0.5) * 7, 0, u * (hash(k + 9) - 0.5) * 7);
        dummy.scale.setScalar(u >= 1 ? 0.0001 : 1);
        dummy.updateMatrix();
        tiles.setMatrixAt(k, dummy.matrix);
        col.copy(tileCol[k]).lerp(new THREE.Color('#FFE14D'), lit * (u > 0 ? 1 - u : 1));
        tiles.setColorAt(k, col);
      }
      tiles.instanceMatrix.needsUpdate = true;
      if (tiles.instanceColor) tiles.instanceColor.needsUpdate = true;

      // cast
      centroid.set(0, 0, 0);
      ctx.cast.forEach((m, i) => {
        castPos(i, lp, m.pos);
        centroid.add(m.pos);
        const u = fallU(i, lp);
        const spins = 1 + (i % 2);
        m.rx = ease(u) * Math.PI * 2 * spins * (i % 3 === 0 ? -1 : 1);
        m.rz = Math.sin(Math.PI * u) * 0.6 * (i % 2 ? 1 : -1);
        // face the camera on the platform, turn to face down the course by the landing
        m.yaw = Math.PI * ease(u) + Math.sin(i + u * 3) * 0.4 * Math.sin(Math.PI * u) + (u === 0 ? Math.sin(ctx.t * 0.8 + i) * 0.25 : 0);
        m.run = 0;
        m.vis = true;
        m.celebrate = false;
      });
      centroid.multiplyScalar(1 / 12);
      if (prev < 0.2 && lp >= 0.2) play('rush', { vol: 0.6 });
      if (prev < 0.9 && lp >= 0.9) {
        play('land');
        ctx.cast.forEach((m) => m.rig.cheer(0.9));
        ctx.kick(0.6);
      }
      prev = lp;

      // streaks follow the camera while falling
      const fallMix = span(lp, 0.22, 0.3) * (1 - span(lp, 0.8, 0.88));
      streaks.visible = fallMix > 0.01 && !ctx.reduced;
      if (streaks.visible) {
        for (let k = 0; k < SN; k++) {
          const yk = ((hash(k * 3.3) * 40 + lp * 1400) % 40) - 20;
          dummy.position.set(centroid.x + (hash(k) - 0.5) * 18, centroid.y + yk, centroid.z + (hash(k * 1.9) - 0.5) * 18);
          dummy.rotation.set(0, 0, 0);
          dummy.scale.set(1, 0.5 + fallMix * 1.4, 1);
          dummy.updateMatrix();
          streaks.setMatrixAt(k, dummy.matrix);
        }
        streaks.instanceMatrix.needsUpdate = true;
      }
      for (const c of cloudList) c.o.position.y = c.y + Math.sin(t * 0.5 + c.ph) * 0.3;
    },
    cam(lp, out) {
      // A: overhead on the sky platform, slow push in. B: falling with the pack, orbiting, clouds whipping past.
      const a = span(lp, 0, 0.2);
      const posA = V(F.x + 7 - a * 2, F.y + 8 - a * 1.5, F.z + 12 - a * 3);
      const lookA = V(F.x, F.y + 0.6, F.z);
      tmpV2.set(0, 0, 0);
      for (let i = 0; i < 12; i++) tmpV2.add(castPos(i, lp, tmpV));
      tmpV2.multiplyScalar(1 / 12);
      const ang = 0.6 + lp * 2.6;
      const r = 8.5 - span(lp, 0.75, 1) * 1.5;
      const posB = V(tmpV2.x + Math.sin(ang) * r, tmpV2.y + 4.5 + span(lp, 0.7, 1) * 0.3, tmpV2.z + Math.cos(ang) * r);
      const lookB = V(tmpV2.x, tmpV2.y - 1.2 + span(lp, 0.7, 1) * 1.8, tmpV2.z);
      const w = ease(span(lp, 0.16, 0.3));
      out.pos.copy(posA).lerp(posB, w);
      out.look.copy(lookA).lerp(lookB, w);
    },
    dispose() {},
  };
}

// ---------------------------------------------------------------------------------------------------- 2. Capsule Run

export function runChapter(props: Record<PropName, THREE.BufferGeometry>): Chapter {
  const group = new THREE.Group();
  const P = { props };
  const mat = softMaterial();
  const mg = new Merge();
  // start deck, gumball slope, hop field, sweep pad, finish deck (z decreasing); pillars underneath
  mg.block(9.4, 1.4, 11, [0, 0, -0.5], C.soda);
  mg.block(8.4, 1.2, 17, [0, 0, -14], C.tangerine);
  for (const s of [-1, 1]) mg.block(0.5, 0.5, 17, [s * 4.45, 0.45, -14], C.tangerine, C.cream);
  for (let r = 0; r < 5; r++)
    for (let c = -1; c <= 1; c++) mg.block(2.3, 0.9, 2.3, [c * 3, 0, -24.4 - r * 3.7], r % 2 ? C.apple : C.grape);
  mg.block(9, 1, 6, [0, 0, -44], C.apple);
  mg.add(paintSplit(new THREE.CylinderGeometry(4.8, 4.8, 1, 40), C.walkTop, C.bubblegum, 0.4), [0, -0.5, -51.5]);
  mg.block(10, 1.4, 14, [0, 0, -61], C.mint);
  for (const z of [-2, -16, -44, -60]) for (const s of [-1, 1]) mg.add(paintSplit(new THREE.CylinderGeometry(0.9, 0.6, 18, 12), '#8F6FD6', '#8F6FD6', 99), [s * 2.6, -10, z]);
  // mallet gantry posts + beam
  for (const s of [-1, 1]) mg.block(0.7, 7, 0.7, [s * 5.2, 7, -33], C.grape, C.grape);
  mg.block(11.2, 0.7, 0.7, [0, 7.35, -33], C.grape, C.lemon);
  const course = mg.build(mat, SHADOWS);
  group.add(course);

  // finish deck checker
  const chk = new THREE.Mesh(new THREE.PlaneGeometry(9.6, 13.6), new THREE.MeshStandardMaterial({ map: checkerTexture(6, 8), roughness: 0.5 }));
  chk.rotation.x = -Math.PI / 2;
  chk.position.set(0, 0.012, -61);
  chk.receiveShadow = SHADOWS;
  group.add(chk);
  // start arch over the finish + bunting
  group.add(prop(P, 'start_arch', [0, 1.2, -58], 0.44));
  // bunting strung along the gantry beam
  group.add(prop(P, 'bunting', [-5.4, 7.0, -33.2], 0.9), prop(P, 'bunting', [0.2, 7.0, -33.2], 0.9));
  // rolling gumballs (ambient) + the one that bonks slot 11
  const gumballs: THREE.Mesh[] = [];
  for (let k = 0; k < 3; k++) {
    const g = prop(P, 'gumball', [0, 0, 0], 0.85);
    group.add(g);
    gumballs.push(g);
  }
  const bonkBall = prop(P, 'gumball', [0, 0, 0], 0.95);
  group.add(bonkBall);
  // mallets
  const mallets: THREE.Group[] = [];
  for (let k = 0; k < 3; k++) {
    const piv = new THREE.Group();
    piv.position.set((k - 1) * 3, 7, -33);
    const handle = new THREE.Mesh(new THREE.CylinderGeometry(0.08, 0.08, 4.6, 10), new THREE.MeshStandardMaterial({ color: C.sun, roughness: 0.3 }));
    handle.position.y = -2.3;
    piv.add(handle);
    const head = prop(P, 'mallet_head', [0, -5.0, 0], 1.25, Math.PI / 2);
    piv.add(head);
    group.add(piv);
    mallets.push(piv);
  }
  const malletAngle = (k: number, lp: number) => Math.sin(lp * Math.PI * 2 * 2.4 + k * 2.1) * 1.05;
  // sweeper on the round pad
  const sweep = new THREE.Group();
  sweep.position.set(0, 0, -51.5);
  sweep.add(prop(P, 'spinner_hub', [0, 0.25, 0], 1.6));
  const bar = new THREE.Mesh(paintSplit(rbox(8.6, 0.38, 0.38, 0.17), C.magenta, C.magenta, 99), mat);
  bar.position.y = 0.55;
  bar.castShadow = SHADOWS;
  sweep.add(bar);
  for (const s of [-1, 1]) sweep.add(prop(P, 'spinner_cap', [s * 4.3, 0.55, 0], 1.6));
  group.add(sweep);
  const sweepAngle = (lp: number) => lp * Math.PI * 2 * 2.6;

  const cl = clouds(group, QUALITY.tier === 'high' ? 14 : 8, V(0, -9, -30), V(60, 6, 90), [1.6, 3], 77);

  const speedOf = (i: number) => (i === 0 ? 1.035 : 0.93 + hash(i * 4.1) * 0.09);
  const runPos = (i: number, lp: number, out: THREE.Vector3) => {
    const s = RUN_START[i];
    let z = s.z - lp * 70 * speedOf(i);
    const zFin = -59 - (i % 4) * 1.4 - Math.floor(i / 4) * 0.6;
    z = Math.max(z, zFin);
    const weave = Math.sin(lp * 9 + i * 1.3) * 0.7;
    const x = s.x * (z < -22 ? 0.95 : 0.85) + weave * (z > zFin + 0.5 ? 1 : 0.2);
    // hop field arcs
    let y = 0;
    if (z < -23 && z > -42) y = Math.abs(Math.sin(((z + 23) * Math.PI) / 3.7)) * 1.1;
    return out.set(x, y, z);
  };
  const KO: Record<number, { lp: number; v: THREE.Vector3; at: THREE.Vector3 }> = {};
  for (const [k, [ch, at]] of Object.entries(KNOCKOUTS)) {
    if (ch !== 'run') continue;
    const i = +k;
    const p = runPos(i, at, V());
    const side = p.x >= 0 ? 1 : -1;
    KO[i] = { lp: at, at: p, v: V(side * (i === 8 ? 11 : 8), 9 + hash(i) * 3, -2 + hash(i * 2) * 3) };
  }
  let prev = 0;
  const lead = V();

  return {
    id: 'run',
    group,
    focus: lead,
    update(lp, ctx) {
      const { t, cast } = ctx;
      // ambient gumballs roll down the slope toward the start (+z)
      gumballs.forEach((g, k) => {
        const z = -22 + ((t * 3.2 + k * 5.5) % 16);
        g.position.set((k - 1) * 2.7, 0.85, z);
        g.rotation.x = z / 0.85;
      });
      const ko11 = KO[11];
      const db = (lp - ko11.lp) * 30;
      bonkBall.position.set(ko11.at.x, 0.95, ko11.at.z - 0.9 + db * 1.2);
      bonkBall.rotation.x = bonkBall.position.z / 0.95;
      bonkBall.visible = db < 14 && db > -18;
      mallets.forEach((m, k) => (m.rotation.z = malletAngle(k, lp)));
      sweep.rotation.y = sweepAngle(lp);
      for (const c of cl) c.o.position.y = c.y + Math.sin(t * 0.4 + c.ph) * 0.3;

      const run = Math.min(1, ctx.speed * 5);
      cast.forEach((m, i) => {
        const ko = KO[i];
        m.vis = true;
        m.celebrate = false;
        if (ko && lp >= ko.lp) {
          const tau = (lp - ko.lp) * 7;
          launch(m.pos, ko.at, ko.v, tau);
          m.rx = -tau * 9;
          m.rz = tau * 6 * Math.sign(ko.v.x);
          m.run = 0;
          m.vis = m.pos.y > -30;
          return;
        }
        runPos(i, lp, m.pos);
        const finished = m.pos.z <= -58.9 - (i % 4) * 1.4 - Math.floor(i / 4) * 0.6 + 0.01;
        m.rx = 0;
        m.rz = 0;
        m.yaw = finished ? Math.PI + Math.sin(t + i) * 0.3 : Math.PI + Math.sin(lp * 9 + i) * 0.25;
        m.run = finished ? 0 : Math.max(run, m.pos.y > 0.05 ? 0.6 : 0);
        m.celebrate = finished && lp > 0.96;
      });
      for (const [k, ko] of Object.entries(KO))
        if (prev < ko.lp && lp >= ko.lp) {
          play('bonk');
          play(+k === 11 ? 'chirp' : 'mallet', { vol: 0.7 });
          ctx.kick(1);
          cast[+k].rig.hit(2);
        }
      if (prev < 0.97 && lp >= 0.97) {
        play('qualify', { vol: 0.6 });
        cast.forEach((m, i) => !KO[i] && m.rig.cheer(1.5));
      }
      prev = lp;
      lead.copy(cast[0].pos);
    },
    cam(lp, out) {
      runPos(0, lp, lead);
      // raised three-quarter view from the right, ahead of the pack (faces visible, the gantry off to the side)
      const fin = span(lp, 0.82, 1);
      const sway = Math.sin(lp * 3.4) * 1.6;
      out.pos.set(10.5 + sway - fin * 2, 8.5 + fin * 1.5, lead.z - 8.5 - fin * 2);
      out.look.set(-0.5, 0.6, lead.z + 2.5 - fin * 2);
    },
    dispose() {
      mat.dispose();
    },
  };
}

// ---------------------------------------------------------------------------------------------------- 3. Goo Tower

function gooMaterial() {
  return new THREE.ShaderMaterial({
    uniforms: { time: { value: 0 }, a: { value: new THREE.Color(C.goo) }, b: { value: new THREE.Color('#FF6FD8') } },
    vertexShader: /* glsl */ `
      uniform float time;
      varying vec2 vUv; varying float vH;
      void main() {
        vUv = uv;
        vec3 p = position;
        float h = sin(p.x * 0.6 + time * 1.7) * 0.12 + sin(p.y * 0.5 - time * 1.3) * 0.12 + sin((p.x + p.y) * 1.3 + time * 2.4) * 0.05;
        p.z += h; vH = h;
        gl_Position = projectionMatrix * modelViewMatrix * vec4(p, 1.0);
      }`,
    fragmentShader: /* glsl */ `
      uniform float time; uniform vec3 a, b;
      varying vec2 vUv; varying float vH;
      void main() {
        vec2 q = vUv * 18.0;
        float n = sin(q.x + time) * sin(q.y * 1.3 - time * 0.8) * 0.5 + 0.5;
        vec2 gq = floor(vUv * 900.0);
        float glit = step(0.992, fract(sin(dot(gq, vec2(12.9898, 78.233))) * 43758.5453)) * (0.5 + 0.5 * sin(time * 6.0 + gq.x));
        vec3 c = mix(a, b, n * 0.45 + vH * 1.5) * 1.15 + glit * 0.9;
        gl_FragColor = vec4(c, 1.0);
      }`,
  });
}

export function gooChapter(props: Record<PropName, THREE.BufferGeometry>): Chapter {
  const group = new THREE.Group();
  const A = V(90, 0, -30);
  group.position.copy(A);
  const P = { props };
  const mat = softMaterial();
  const mg = new Merge();
  const R = 5.7;
  const H = 15;
  const sY = (s: number) => 0.5 + s * H;
  const sTh = (s: number) => s * Math.PI * 4;
  // core: capsule-machine column with bands
  mg.add(paintSplit(rbox(6.4, 19, 6.4, 0.5), '#E6DBFF', '#CBC0F5', 99), [0, 9.5 - 1, 0]);
  for (const y of [3.5, 8.2, 12.9, 17.5]) mg.block(6.9, 0.6, 6.9, [0, y, 0], C.grape, C.grape);
  // ground ring
  mg.add(paintSplit(new THREE.CylinderGeometry(10, 9.4, 1.4, 48), C.walkTop, C.apple, 0.5), [0, -0.7, 0]);
  // the spiral ledge (2 laps)
  const STEPS = 40;
  for (let k = 0; k <= STEPS; k++) {
    const s = k / STEPS;
    const th = sTh(s);
    const q = Math.floor(((th / (Math.PI / 2)) % 4) + 4) % 4;
    mg.block(2.9, 0.7, 2.3, [Math.cos(th) * R, sY(s), Math.sin(th) * R], [C.apple, C.soda, C.tangerine, C.bubblegum][q], C.walkTop, -th);
  }
  mg.add(paintSplit(new THREE.CylinderGeometry(2.4, 2.4, 0.7, 32), C.walkTop, C.mint, 0.2), [Math.cos(sTh(1)) * R, sY(1) - 0.35, Math.sin(sTh(1)) * R]);
  const tower = mg.build(mat, SHADOWS);
  group.add(tower);
  for (const f of [0, 1, 2, 3]) {
    const a = (f * Math.PI) / 2;
    group.add(prop(P, 'coin_slot', [Math.cos(a) * 3.3, 6, Math.sin(a) * 3.3], 1.4, -a + Math.PI / 2));
  }
  for (let k = 0; k < 5; k++) {
    const a = (k / 5) * Math.PI * 2 + 0.4;
    group.add(prop(P, 'inflatable_tower', [Math.cos(a) * 16, -3, Math.sin(a) * 16], 1.2 + hash(k) * 0.5));
  }
  // goo
  const gooMat = gooMaterial();
  const goo = new THREE.Mesh(new THREE.CircleGeometry(40, 96), gooMat);
  goo.geometry.setAttribute('uv', goo.geometry.getAttribute('uv'));
  goo.rotation.x = -Math.PI / 2;
  group.add(goo);
  const gooY = (lp: number) => -1.3 + lp * 12.6;
  const blobs: { m: THREE.Mesh; a: number; r: number; ph: number }[] = [];
  for (let k = 0; k < 9; k++) {
    const m = prop(P, 'goo_blob', [0, 0, 0], 1 + hash(k * 2) * 1.3);
    group.add(m);
    blobs.push({ m, a: hash(k * 3) * Math.PI * 2, r: 11 + hash(k * 5) * 16, ph: hash(k) * 6 });
  }
  const cl = clouds(group, 10, V(0, -6, 0), V(80, 4, 80), [2, 3.5], 133);

  const ORDER = [0, 1, 2, 3, 4, 5, 6, 7];
  const lag = (i: number) => (i === 0 ? 0 : 0.03 + i * 0.025);
  const capS: Record<number, number> = {};
  for (const i of [5, 6, 7]) capS[i] = (gooY(KNOCKOUTS[i][1]) - 0.5 + 0.15) / H;
  const climb = (i: number, lp: number) => {
    const s = Math.max(0, Math.min(1, 0.02 + lp * 1.0 - lag(i)));
    return capS[i] != null ? Math.min(s, capS[i]) : Math.min(s, 0.98 - i * 0.012);
  };
  const lead = V();
  let prev = 0;

  return {
    id: 'goo',
    group,
    focus: lead,
    update(lp, ctx) {
      const { t, cast } = ctx;
      gooMat.uniforms.time.value = t;
      const gy = gooY(lp);
      goo.position.y = gy;
      blobs.forEach((b) => {
        b.m.position.set(Math.cos(b.a + t * 0.05) * b.r, gy - 0.25 + Math.sin(t * 1.6 + b.ph) * 0.12, Math.sin(b.a + t * 0.05) * b.r);
        const s = 1 + Math.sin(t * 2.2 + b.ph) * 0.12;
        b.m.scale.set(s * 1.2, (2 - s) * 1.1, s * 1.2);
      });
      for (const c of cl) c.o.position.y = c.y + Math.sin(t * 0.4 + c.ph) * 0.3;
      const run = Math.min(1, ctx.speed * 4);
      cast.forEach((m, i) => {
        m.celebrate = false;
        if (i > 7) {
          m.vis = false;
          return;
        }
        const s = climb(i, lp);
        const th = sTh(s);
        const rr = R + ((i % 3) - 1) * 0.6;
        const y = sY(s) + 0.0;
        const depth = gy - y;
        m.pos.set(A.x + Math.cos(th) * rr, y, A.z + Math.sin(th) * rr);
        const tx = -Math.sin(th), tz = Math.cos(th);
        m.yaw = Math.atan2(tx, tz);
        m.rx = 0;
        m.rz = 0;
        const stuck = capS[i] != null && s >= capS[i] - 1e-4;
        m.run = stuck ? 0 : run;
        if (depth > -0.1 && capS[i] != null) {
          // swallowed: sinks with a wobble, arms up
          const d = Math.max(0, depth + 0.1);
          m.pos.y = y - d * 1.2;
          m.rz = Math.sin(t * 9 + i) * 0.25;
          m.vis = d < 1.6;
        } else m.vis = true;
        if (!stuck && s >= 0.95 && lp > 0.9) m.celebrate = true;
      });
      for (const i of [5, 6, 7]) {
        const at = KNOCKOUTS[i][1];
        if (prev < at && lp >= at) {
          play('goo');
          play('eliminated', { vol: 0.5 });
          ctx.kick(0.8);
        }
        // the stuck ones flail (hop) while the goo comes
        if (prev < at - 0.12 && lp >= at - 0.12) cast[i].rig.jump(0.8);
      }
      if (prev < 0.95 && lp >= 0.95) {
        play('qualify', { vol: 0.6 });
        ORDER.slice(0, 5).forEach((i) => cast[i].rig.cheer(1.4));
      }
      prev = lp;
      lead.copy(cast[0].pos);
    },
    cam(lp, out) {
      // ride round the outside with the leader, a little behind and above; pull out at the top
      const s = climb(0, lp);
      const th = sTh(s) - 0.55;
      const top = ease(span(lp, 0.85, 1));
      const r = 15.5 + top * 6;
      out.pos.set(A.x + Math.cos(th) * r, sY(s) + 5.5 + top * 4, A.z + Math.sin(th) * r);
      out.look.set(A.x + Math.cos(th + 0.3) * R * 0.75, sY(s) + 0.6, A.z + Math.sin(th + 0.3) * R * 0.75);
    },
    dispose() {
      mat.dispose();
      gooMat.dispose();
    },
  };
}

// ---------------------------------------------------------------------------------------------------- 4. Star Fountain

export function starChapter(props: Record<PropName, THREE.BufferGeometry>): Chapter {
  const group = new THREE.Group();
  const A = V(180, 0, -30);
  group.position.copy(A);
  const P = { props };
  const mat = softMaterial();
  const mg = new Merge();
  mg.add(paintSplit(new THREE.CylinderGeometry(13.6, 12.8, 1.4, 64), C.walkTop, C.tangerine, 0.6), [0, -0.7, 0]);
  mg.add(paintSplit(new THREE.CylinderGeometry(11.2, 11.2, 1.42, 64), '#E9DFFF', C.grape, 0.6), [0, -0.69, 0]);
  mg.add(paint2(new THREE.TorusGeometry(13.6, 0.3, 10, 72).rotateX(Math.PI / 2), C.lemon), [0, 0.12, 0]);
  // fountain column
  mg.add(paintSplit(new THREE.CylinderGeometry(1.4, 1.6, 2.6, 32), C.bubblegum, C.bubblegum, 99), [0, 1.3, 0]);
  mg.add(paint2(new THREE.TorusGeometry(1.45, 0.16, 10, 32).rotateX(Math.PI / 2), C.lemon), [0, 2.6, 0]);
  for (let k = 0; k < 5; k++) mg.add(paint2(new THREE.SphereGeometry(0.34, 16, 12), RIMS[k]), [Math.cos(k * 1.3) * 0.6, 2.95 + (k % 2) * 0.3, Math.sin(k * 1.3) * 0.6]);
  for (let k = 0; k < 3; k++) {
    const a = (k / 3) * Math.PI * 2 + Math.PI / 3;
    mg.block(4, 0.8, 4, [Math.cos(a) * 18.5, 0.8, Math.sin(a) * 18.5], C.apple);
    mg.add(paintSplit(new THREE.CylinderGeometry(0.8, 0.5, 14, 10), '#8F6FD6', '#8F6FD6', 99), [Math.cos(a) * 18.5, -7, Math.sin(a) * 18.5]);
  }
  for (let k = 0; k < 6; k++) {
    const a = (k / 6) * Math.PI * 2;
    mg.add(paintSplit(new THREE.CylinderGeometry(1.4, 1.0, 16, 12), '#8F6FD6', '#8F6FD6', 99), [Math.cos(a) * 9, -8.5, Math.sin(a) * 9]);
  }
  group.add(mg.build(mat, SHADOWS));
  // glass dome + crown of capsule halves (glows before each burst)
  const dome = new THREE.Mesh(
    new THREE.SphereGeometry(1.35, 32, 16, 0, Math.PI * 2, 0, Math.PI / 2),
    new THREE.MeshStandardMaterial({ color: '#DDF3FF', roughness: 0.05, transparent: true, opacity: 0.35, depthWrite: false }),
  );
  dome.position.y = 2.65;
  group.add(dome);
  const crownMat = new THREE.MeshStandardMaterial({ color: C.sun, roughness: 0.25, emissive: '#FFB000', emissiveIntensity: 0.2 });
  const crownG = new THREE.Group();
  crownG.position.y = 4.2;
  for (let k = 0; k < 6; k++) {
    const a = (k / 6) * Math.PI * 2;
    const half = new THREE.Mesh(new THREE.SphereGeometry(0.42, 16, 10, 0, Math.PI * 2, 0, Math.PI / 2), crownMat);
    half.position.set(Math.cos(a) * 0.95, 0, Math.sin(a) * 0.95);
    half.rotation.z = Math.cos(a) * -0.5;
    half.rotation.x = Math.sin(a) * 0.5;
    crownG.add(half);
  }
  group.add(crownG);
  // turntable with stripes
  const table = new THREE.Mesh(new THREE.CylinderGeometry(6.2, 6.2, 0.24, 64), [
    new THREE.MeshStandardMaterial({ color: C.soda, roughness: 0.4 }),
    new THREE.MeshStandardMaterial({ map: blueStripes(), roughness: 0.4 }),
    new THREE.MeshStandardMaterial({ color: C.soda }),
  ]);
  table.position.y = 0.12;
  table.receiveShadow = SHADOWS;
  group.add(table);
  // sweepers
  const sweepers: THREE.Group[] = [];
  for (let k = 0; k < 3; k++) {
    const a = (k / 3) * Math.PI * 2;
    const sw = new THREE.Group();
    sw.position.set(Math.cos(a) * 9.3, 0, Math.sin(a) * 9.3);
    sw.add(prop(P, 'spinner_hub', [0, 0.25, 0], 1.4));
    const bar = new THREE.Mesh(paintSplit(rbox(4.6, 0.34, 0.34, 0.15), C.magenta, C.magenta, 99), mat);
    bar.position.set(2.1, 0.5, 0);
    bar.castShadow = SHADOWS;
    sw.add(bar);
    sw.add(prop(P, 'spinner_cap', [4.4, 0.5, 0], 1.5));
    group.add(sw);
    sweepers.push(sw);
  }
  // stars (emissive gold: they bloom)
  const WAVES = [0.04, 0.34, 0.64];
  const PER = 12;
  const NS = WAVES.length * PER;
  const starMat = new THREE.MeshStandardMaterial({ color: '#FFD84A', roughness: 0.25, emissive: '#FFB300', emissiveIntensity: 1.6, toneMapped: false });
  const stars = new THREE.InstancedMesh(props.prize_star, starMat, NS);
  stars.frustumCulled = false;
  stars.castShadow = SHADOWS;
  group.add(stars);
  const starAnchor = Array.from({ length: NS }, (_, k) => {
    const a = hash(k * 3.7) * Math.PI * 2, r = 3.2 + hash(k * 1.3) * 7.2;
    return V(Math.cos(a) * r, 0.9, Math.sin(a) * r);
  });
  const crownAt = V(0, 4.4, 0);
  const cl = clouds(group, 10, V(0, -10, 0), V(70, 6, 70), [2, 3.5], 177);

  const KO: Record<number, { lp: number; at: THREE.Vector3; v: THREE.Vector3 }> = {};
  const castPos = (i: number, lp: number, out: THREE.Vector3, t: number) => {
    const dir = i % 2 ? 1 : -1;
    const th = i * 1.25 + lp * Math.PI * 2 * (0.45 + 0.07 * i) * dir;
    const r = 6.8 + Math.sin(lp * 9 + i * 2) * 2.2;
    const hopPh = Math.sin(lp * 34 + i * 1.7);
    out.set(A.x + Math.cos(th) * r, Math.max(0, hopPh - 0.55) * 2.4 + 0 * t, A.z + Math.sin(th) * r);
    return th;
  };
  for (const i of [3, 4]) {
    const at = KNOCKOUTS[i][1];
    const p = V();
    castPos(i, at, p, 0);
    const out = V(p.x - A.x, 0, p.z - A.z).normalize();
    KO[i] = { lp: at, at: p, v: V(out.x * 10, 9, out.z * 10) };
  }
  let prev = 0;
  const lead = V();

  return {
    id: 'star',
    group,
    focus: lead,
    update(lp, ctx) {
      const { t, cast } = ctx;
      table.rotation.y = t * 0.45;
      sweepers.forEach((s, k) => (s.rotation.y = lp * Math.PI * 2 * 3.2 * (k === 1 ? -1 : 1) + k));
      let glow = 0;
      for (let w = 0; w < WAVES.length; w++) glow = Math.max(glow, span(lp, WAVES[w] - 0.06, WAVES[w]) * (1 - span(lp, WAVES[w], WAVES[w] + 0.04)));
      crownMat.emissiveIntensity = 0.2 + glow * 2.6;
      crownG.rotation.y = t * 0.6;
      for (let k = 0; k < NS; k++) {
        const w = Math.floor(k / PER);
        const t0 = WAVES[w] + (k % PER) * 0.003;
        const u = span(lp, t0, t0 + 0.07);
        const collect = t0 + 0.16 + hash(k * 9.1) * 0.1;
        const v = span(lp, collect, collect + 0.025);
        const an = starAnchor[k];
        if (u <= 0 || v >= 1) {
          dummy.scale.setScalar(0.0001);
          dummy.position.set(0, -50, 0);
        } else {
          dummy.position.copy(crownAt).lerp(an, easeOut(u));
          dummy.position.y += Math.sin(Math.PI * u) * 5 + (u >= 1 ? Math.sin(t * 3 + k) * 0.15 : 0);
          dummy.scale.setScalar((0.9 + Math.sin(Math.PI * v) * 0.9) * (1 - v) * (0.4 + 0.6 * u));
        }
        dummy.rotation.set(0, t * 2.5 + k, 0);
        dummy.updateMatrix();
        stars.setMatrixAt(k, dummy.matrix);
      }
      stars.instanceMatrix.needsUpdate = true;
      for (const c of cl) c.o.position.y = c.y + Math.sin(t * 0.4 + c.ph) * 0.3;
      for (const w of WAVES)
        if (prev < w && lp >= w) {
          play('confetti', { vol: 0.5 });
          ctx.kick(0.35);
        }

      const run = Math.min(1, ctx.speed * 5);
      cast.forEach((m, i) => {
        m.celebrate = false;
        if (i > 4) {
          m.vis = false;
          return;
        }
        m.vis = true;
        const ko = KO[i];
        if (ko && lp >= ko.lp) {
          const tau = (lp - ko.lp) * 7;
          launch(m.pos, ko.at, ko.v, tau);
          m.rx = -tau * 8;
          m.rz = tau * 5;
          m.run = 0;
          m.vis = m.pos.y > -25;
          return;
        }
        const th = castPos(i, lp, m.pos, t);
        const dir = i % 2 ? 1 : -1;
        m.yaw = Math.atan2(-Math.sin(th) * dir, Math.cos(th) * dir);
        m.rx = 0;
        m.rz = 0;
        m.run = run;
        if (lp > 0.94) m.celebrate = true;
      });
      for (const i of [3, 4])
        if (prev < KO[i].lp && lp >= KO[i].lp) {
          play('bonk');
          play('mallet', { vol: 0.6 });
          ctx.kick(1);
          cast[i].rig.hit(2);
        }
      if (prev < 0.95 && lp >= 0.95) {
        play('qualify', { vol: 0.6 });
        [0, 1, 2].forEach((i) => cast[i].rig.cheer(1.4));
      }
      prev = lp;
      lead.copy(A);
    },
    cam(lp, out) {
      const phi = 2.2 + lp * Math.PI * 1.1;
      const r = 25 - lp * 8;
      out.pos.set(A.x + Math.cos(phi) * r, 14 - lp * 7, A.z + Math.sin(phi) * r);
      out.look.set(A.x, 1.2 + (1 - lp) * 0.8, A.z);
    },
    dispose() {
      mat.dispose();
      starMat.dispose();
      crownMat.dispose();
    },
  };
}

/** Turntable top: soda blue with 12 darker-blue radial stripes (doc 08 Star Fountain colours). */
function blueStripes() {
  const c = document.createElement('canvas');
  c.width = c.height = 256;
  const x = c.getContext('2d')!;
  x.fillStyle = '#8FD0F7';
  x.fillRect(0, 0, 256, 256);
  x.fillStyle = '#4FA6EC';
  for (let k = 0; k < 12; k++) {
    const a0 = (k / 12) * Math.PI * 2, a1 = a0 + Math.PI / 12;
    x.beginPath();
    x.moveTo(128, 128);
    x.arc(128, 128, 182, a0, a1);
    x.closePath();
    x.fill();
  }
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}

function paint2(g: THREE.BufferGeometry, c: string) {
  return paintSplit(g, c, c, 99);
}

// ---------------------------------------------------------------------------------------------------- 5. Lid Flip (on-chain)

export const LID_DIGITAL = (lp: number) => ease(span(lp, 0.0, 0.14)) * (1 - ease(span(lp, 0.9, 1)));

export function lidChapter(props: Record<PropName, THREE.BufferGeometry>): Chapter {
  const group = new THREE.Group();
  const A = V(270, 0, -30);
  group.position.copy(A);
  const P = { props };
  const mat = softMaterial();
  // lids
  const S = 1.8, GAP = 1.87;
  const NL = 81;
  const lids = tileMesh(NL, S, 0.42, S);
  const wireMat = new THREE.MeshBasicMaterial({ color: '#5BF0B5', wireframe: true, transparent: true, opacity: 0, toneMapped: false, depthWrite: false });
  const wires = new THREE.InstancedMesh(new THREE.BoxGeometry(S + 0.04, 0.46, S + 0.04, 2, 1, 2), wireMat, NL);
  wires.frustumCulled = false;
  group.add(lids, wires);
  const ringOf: number[] = [], lidAt: THREE.Vector3[] = [], lidCol: THREE.Color[] = [];
  const RING_COL = [C.bubblegum, C.bubblegum, C.soda, C.tangerine, C.bubblegum];
  for (let k = 0; k < NL; k++) {
    const ix = k % 9, iz = Math.floor(k / 9);
    const ring = Math.max(Math.abs(ix - 4), Math.abs(iz - 4));
    ringOf.push(ring);
    lidAt.push(V((ix - 4) * GAP, -0.21, (iz - 4) * GAP));
    lidCol.push(new THREE.Color(RING_COL[ring]));
  }
  const RETIRE: Record<number, number> = { 4: 0.3, 3: 0.52, 2: 0.7 };
  const WAVES = Array.from({ length: 8 }, (_, j) => 0.06 + j * 0.105);
  const SHARE = [0.16, 0.2, 0.26, 0.32, 0.38, 0.44, 0.48, 0.5];
  // lower shelf
  const low = tileMesh(25, S, 0.42, S);
  for (let k = 0; k < 25; k++) {
    dummy.position.set(((k % 5) - 2) * GAP, -5.2, (Math.floor(k / 5) - 2) * GAP);
    dummy.rotation.set(0, 0, 0);
    dummy.scale.setScalar(1);
    dummy.updateMatrix();
    low.setMatrixAt(k, dummy.matrix);
    low.setColorAt(k, new THREE.Color(Math.max(Math.abs((k % 5) - 2), Math.abs(Math.floor(k / 5) - 2)) === 2 ? '#9C78E6' : C.grape));
  }
  group.add(low);
  // claw-machine frame + glass (the Ephemeral Rollup: the arena under glass)
  const mg = new Merge();
  // a tall frame: the header ring sits above the camera so it never cuts across the shot
  for (const sx of [-1, 1]) for (const sz of [-1, 1]) mg.block(0.8, 24.5, 0.8, [sx * 9.9, 17.5, sz * 9.9], C.grape, C.lemon);
  for (const s of [-1, 1]) {
    mg.block(20.6, 0.9, 0.9, [0, 17.9, s * 9.9], C.grape, C.lemon);
    mg.block(0.9, 0.9, 20.6, [s * 9.9, 17.9, 0], C.grape, C.lemon);
    mg.block(0.9, 0.9, 20.6, [0, 17.9, 0], C.grape, C.lemon);
  }
  mg.add(paintSplit(new THREE.CylinderGeometry(0.06, 0.06, 11, 6), '#CFC7EE', '#CFC7EE', 99), [0, 12.4, 0]);
  group.add(mg.build(mat, SHADOWS));
  const glassMat = new THREE.MeshStandardMaterial({ color: '#CDEBFF', roughness: 0.05, transparent: true, opacity: 0.1, depthWrite: false, side: THREE.DoubleSide });
  for (let k = 0; k < 4; k++) {
    const g = new THREE.Mesh(new THREE.PlaneGeometry(19.8, 24), glassMat);
    const a = (k * Math.PI) / 2;
    g.position.set(Math.sin(a) * 9.9, 6, Math.cos(a) * 9.9);
    g.rotation.y = a;
    group.add(g);
  }
  group.add(prop(P, 'claw', [0, 6.2, 0], 1.6));
  // sweeper bar from the claw
  const sweep = new THREE.Group();
  const bar = new THREE.Mesh(paintSplit(rbox(16.8, 0.36, 0.36, 0.16), C.magenta, C.magenta, 99), mat);
  bar.position.y = 0.45;
  bar.castShadow = SHADOWS;
  sweep.add(bar, prop(P, 'spinner_hub', [0, 0.3, 0], 1.3));
  group.add(sweep);
  // digital grid floor + rising data bits
  const grid = new THREE.GridHelper(80, 40, '#5BF0B5', '#5BF0B5');
  (grid.material as THREE.Material).transparent = true;
  (grid.material as THREE.LineBasicMaterial).toneMapped = false;
  grid.position.y = -9;
  group.add(grid);
  const NB = QUALITY.tier === 'high' ? 90 : 40;
  const bitMat = new THREE.MeshBasicMaterial({ color: '#5BF0B5', transparent: true, opacity: 0, toneMapped: false, depthWrite: false });
  const bits = new THREE.InstancedMesh(new THREE.BoxGeometry(0.12, 0.12, 0.12), bitMat, NB);
  bits.frustumCulled = false;
  group.add(bits);
  // VRF dice
  const dice = [makeDie(), makeDie()];
  dice.forEach((d) => group.add(d));
  // session key (big wind-up key) + coin slot (Metaplex mint) + the crown capsule
  const key = prop(P, 'windup_key', [7, 5.5, 6.5], 2.6);
  group.add(key);
  const slot = prop(P, 'coin_slot', [0, 4.2, -13], 2.6, Math.PI);
  group.add(slot);
  const capsule = new THREE.Group();
  const capTop = new THREE.Mesh(new THREE.SphereGeometry(0.75, 28, 14, 0, Math.PI * 2, 0, Math.PI / 2), new THREE.MeshStandardMaterial({ color: '#FFFFFF', roughness: 0.08, transparent: true, opacity: 0.7 }));
  const capBot = new THREE.Mesh(
    new THREE.SphereGeometry(0.75, 28, 14, 0, Math.PI * 2, Math.PI / 2, Math.PI / 2),
    new THREE.MeshStandardMaterial({ color: C.sun, roughness: 0.2, emissive: '#FFA800', emissiveIntensity: 0.9 }),
  );
  const capCrown = makeCrown(1.2);
  capCrown.scale.setScalar(0.55);
  capCrown.position.y = -0.1;
  capsule.add(capTop, capBot, capCrown);
  group.add(capsule);

  const KO: Record<number, { lp: number; at: THREE.Vector3; lid: number }> = {};
  const castPos = (i: number, lp: number, out: THREE.Vector3) =>
    out.set(A.x + Math.cos(lp * 5.2 + i * 2.1) * (1.6 + i * 0.5), 0, A.z + Math.sin(lp * 4.1 + i * 2.1) * (1.6 + i * 0.5));
  for (const i of [1, 2]) {
    const at = KNOCKOUTS[i][1];
    const p = castPos(i, at, V());
    const ix = Math.round((p.x - A.x) / GAP) + 4, iz = Math.round((p.z - A.z) / GAP) + 4;
    KO[i] = { lp: at, at: p, lid: iz * 9 + ix };
  }
  const lidState = (k: number, lp: number) => {
    let lit = 0, open = 0;
    for (const o of Object.values(KO)) {
      if (o.lid === k) {
        lit = Math.max(lit, span(lp, o.lp - 0.05, o.lp) * (lp < o.lp + 0.06 ? 1 : 0));
        open = Math.max(open, Math.sin(Math.PI * span(lp, o.lp, o.lp + 0.08)));
      }
    }
    if (ringOf[k] <= 1 && hash(k) < 0.55) return { lit, open };
    for (let j = 0; j < WAVES.length; j++) {
      if (hash(k * 13.1 + j * 7.3) >= SHARE[j]) continue;
      const w = WAVES[j];
      lit = Math.max(lit, lp >= w - 0.035 && lp < w ? 1 : 0);
      open = Math.max(open, Math.sin(Math.PI * span(lp, w, w + 0.055)));
    }
    return { lit, open };
  };
  const hinge = new THREE.Matrix4(), m1 = new THREE.Matrix4(), m2 = new THREE.Matrix4();
  const lead = V();
  let prev = 0;

  return {
    id: 'lid',
    group,
    focus: lead,
    digital: LID_DIGITAL,
    update(lp, ctx) {
      const { t, cast } = ctx;
      const dg = LID_DIGITAL(lp);
      for (let k = 0; k < NL; k++) {
        const { lit, open } = lidState(k, lp);
        const rt = RETIRE[ringOf[k]];
        const ru = rt != null ? span(lp, rt, rt + 0.12) : 0;
        const warn = rt != null ? span(lp, rt - 0.06, rt) * (ru > 0 ? 0 : 1) : 0;
        const p = lidAt[k];
        // hinge on the +x edge
        m1.makeTranslation(p.x + S / 2, p.y - easeIn(ru) * 26, p.z);
        hinge.makeRotationZ(open * 1.9 + ru * 2.5 * (hash(k) - 0.5));
        m2.makeTranslation(-S / 2, 0, 0);
        m1.multiply(hinge).multiply(m2);
        if (ru >= 1) m1.makeScale(0.0001, 0.0001, 0.0001);
        lids.setMatrixAt(k, m1);
        wires.setMatrixAt(k, m1);
        col.copy(lidCol[k]);
        if (warn > 0) col.lerp(new THREE.Color(C.magenta), (Math.sin(t * 14) * 0.5 + 0.5) * 0.8);
        if (lit > 0) col.lerp(new THREE.Color('#FFE14D'), lit);
        lids.setColorAt(k, col);
      }
      lids.instanceMatrix.needsUpdate = true;
      wires.instanceMatrix.needsUpdate = true;
      if (lids.instanceColor) lids.instanceColor.needsUpdate = true;
      wireMat.opacity = dg * 0.75;
      wires.visible = dg > 0.01;
      (grid.material as THREE.Material).opacity = dg * 0.55;
      grid.visible = dg > 0.01;
      bitMat.opacity = dg * 0.9;
      bits.visible = dg > 0.01;
      if (bits.visible)
        for (let k = 0; k < NB; k++) {
          const y = ((hash(k * 2.7) * 22 + t * (0.8 + hash(k) * 1.4)) % 22) - 8;
          dummy.position.set((hash(k * 1.1) - 0.5) * 22, y, (hash(k * 3.9) - 0.5) * 22);
          dummy.rotation.set(t + k, t * 0.7 + k, 0);
          dummy.scale.setScalar(0.6 + hash(k * 5) * 1.4);
          dummy.updateMatrix();
          bits.setMatrixAt(k, dummy.matrix);
        }
      bits.instanceMatrix.needsUpdate = true;
      sweep.rotation.y = span(lp, 0.15, 1) * Math.PI * 2 * 2.4;
      sweep.position.y = (1 - ease(span(lp, 0.1, 0.16))) * 4;
      // VRF dice roll in (lp .02 - .24), then settle and hover
      dice.forEach((d, n) => {
        const u = span(lp, 0.02 + n * 0.02, 0.22 + n * 0.02);
        const r = (1 - easeOut(u)) * (14 + n * 3);
        d.rotation.set(r + n * 0.4, r * 0.7 + n, r * 0.4);
        d.position.set(-6.5 + n * 1.9, 3.6 + Math.abs(Math.sin(u * Math.PI * 3)) * (1 - u) * 3 + Math.sin(t * 1.4 + n) * 0.15, 6.2 + n * 0.8);
        d.scale.setScalar(backOut(Math.min(1, span(lp, 0, 0.06))) || 0.0001);
      });
      key.rotation.z = t * 1.2 + lp * 10;
      key.position.y = 5.5 + Math.sin(t * 1.1) * 0.25;
      key.scale.setScalar(2.6 * Math.max(0.0001, ease(span(lp, 0.4, 0.48))));
      // the crown capsule: minted from the coin slot (lp .8 → 1), floats up toward the camera
      const cu = span(lp, 0.8, 1);
      capsule.visible = cu > 0;
      capsule.position.set(0, 4.6 + easeOut(cu) * 3.6, -13.6 - easeOut(cu) * 4.5);
      capsule.rotation.y = t * 1.5;
      capsule.scale.setScalar(backOut(Math.min(1, cu * 2)) * 1.3 || 0.0001);
      if (prev < 0.8 && lp >= 0.8) {
        play('capsulepop');
        ctx.kick(0.6);
      }

      cast.forEach((m, i) => {
        m.celebrate = false;
        if (i > 2) {
          m.vis = false;
          return;
        }
        m.vis = true;
        const ko = KO[i];
        if (ko && lp >= ko.lp + 0.012) {
          const tau = (lp - ko.lp - 0.012) * 6;
          m.pos.set(ko.at.x, -0.5 * G * tau * tau, ko.at.z);
          m.rx = tau * 6;
          m.rz = tau * 3;
          m.run = 0;
          m.vis = m.pos.y > -30;
          return;
        }
        castPos(i, lp, m.pos);
        const dx = -Math.sin(lp * 5.2 + i * 2.1) * 5.2, dz = Math.cos(lp * 4.1 + i * 2.1) * 4.1;
        m.yaw = Math.atan2(dx, dz);
        m.rx = 0;
        m.rz = 0;
        m.run = Math.min(1, ctx.speed * 4);
        if (lp > 0.78 && i === 0) m.celebrate = true;
      });
      for (const i of [1, 2])
        if (prev < KO[i].lp && lp >= KO[i].lp) {
          play('eliminated', { vol: 0.6 });
          ctx.kick(0.9);
        }
      prev = lp;
      lead.copy(A);
    },
    cam(lp, out) {
      // orbit the claw machine; at the end come round the back to face the coin slot as the crown capsule pops out
      const phi = -0.4 + lp * Math.PI * 0.9;
      const r = 23 - lp * 4;
      const end = ease(span(lp, 0.74, 0.92));
      out.pos.set(A.x + Math.sin(phi) * r, 15 - lp * 2, A.z + Math.cos(phi) * r);
      out.pos.lerp(tmpV.set(A.x + 2.5, 9.5, A.z - 27), end);
      out.look.set(A.x, 0.6, A.z).lerp(tmpV2.set(A.x, 6.2, A.z - 15), end);
    },
    dispose() {
      mat.dispose();
      wireMat.dispose();
      bitMat.dispose();
      glassMat.dispose();
    },
  };
}

function makeDie() {
  const mg = new Merge();
  mg.add(paintSplit(rbox(1.3, 1.3, 1.3, 0.26, 3), '#FFFFFF', '#FFFFFF', 99));
  const pip = () => paintSplit(new THREE.SphereGeometry(0.12, 10, 8), C.ink, C.ink, 99);
  const faces: [THREE.Vector3, THREE.Vector3, THREE.Vector3, [number, number][]][] = [
    [V(0, 0, 1), V(1, 0, 0), V(0, 1, 0), [[0, 0]]],
    [V(0, 0, -1), V(1, 0, 0), V(0, 1, 0), [[-1, -1], [-1, 0], [-1, 1], [1, -1], [1, 0], [1, 1]]],
    [V(1, 0, 0), V(0, 0, 1), V(0, 1, 0), [[-1, -1], [1, 1]]],
    [V(-1, 0, 0), V(0, 0, 1), V(0, 1, 0), [[-1, -1], [-1, 1], [1, -1], [1, 1], [0, 0]]],
    [V(0, 1, 0), V(1, 0, 0), V(0, 0, 1), [[-1, -1], [0, 0], [1, 1]]],
    [V(0, -1, 0), V(1, 0, 0), V(0, 0, 1), [[-1, -1], [-1, 1], [1, -1], [1, 1]]],
  ];
  for (const [n, u, v, pips] of faces)
    for (const [a, b] of pips) {
      const p = n.clone().multiplyScalar(0.64).addScaledVector(u, a * 0.33).addScaledVector(v, b * 0.33);
      mg.add(pip(), [p.x, p.y, p.z], [0, 0, 0], [1, 1, 0.45]);
    }
  const m = mg.build(softMaterial(), SHADOWS);
  const g = new THREE.Group();
  g.add(m);
  return g;
}

// ---------------------------------------------------------------------------------------------------- 6. the crown

export function crownChapter(props: Record<PropName, THREE.BufferGeometry>, reduced: boolean): Chapter {
  const group = new THREE.Group();
  const A = V(360, 0, -30);
  group.position.copy(A);
  const P = { props };
  const mat = softMaterial();
  const mg = new Merge();
  mg.add(paintSplit(new THREE.CylinderGeometry(13, 12.4, 1.4, 64), C.cream, C.lemon, 0.6), [0, -0.7, 0]);
  mg.block(2.8, 2.3, 2.8, [0, 2.3, 0], C.lemon);
  mg.block(2.8, 1.5, 2.8, [-3, 1.5, 0.4], C.soda);
  mg.block(2.8, 1.0, 2.8, [3, 1.0, 0.4], C.tangerine);
  for (let k = 0; k < 6; k++) {
    const a = (k / 6) * Math.PI * 2;
    mg.add(paintSplit(new THREE.CylinderGeometry(1.4, 1.0, 18, 12), '#8F6FD6', '#8F6FD6', 99), [Math.cos(a) * 9, -10, Math.sin(a) * 9]);
  }
  group.add(mg.build(mat, SHADOWS));
  // the capsule machine: cabinet prop + a glass globe full of capsules (the globe is glass dressing in the game too)
  group.add(prop(P, 'capsule_machine', [0, 0, -9], 2.4));
  const globeY = 1.94 * 2.4 + 2.1;
  const globe = new THREE.Mesh(
    new THREE.SphereGeometry(2.5, 40, 24),
    new THREE.MeshStandardMaterial({ color: '#E8F6FF', roughness: 0.04, transparent: true, opacity: 0.32, depthWrite: false }),
  );
  globe.position.set(0, globeY, -9);
  group.add(globe);
  const capMg = new Merge();
  for (let k = 0; k < 26; k++) {
    const a = hash(k * 2.1) * Math.PI * 2, rr = Math.sqrt(hash(k * 3.3)) * 1.7, y = -1.9 + Math.floor(k / 7) * 0.62 + hash(k) * 0.2;
    if (Math.hypot(rr, y) > 2.05) continue;
    const g = paintSplit(new THREE.SphereGeometry(0.34, 14, 10), C.white, RIMS[k % RIMS.length], 0.0);
    capMg.add(g, [Math.cos(a) * rr, globeY + y, -9 + Math.sin(a) * rr], [hash(k) * 3, hash(k * 5) * 3, 0]);
  }
  group.add(capMg.build(mat, false));
  for (const s of [-1, 1]) {
    group.add(prop(P, 'bleacher_block', [s * 8.5, 0, -6.5], 1.1, s * -0.5));
    group.add(prop(P, 'pennant', [s * 6.5, 0, 3.5], 1.1));
    group.add(prop(P, 'confetti_cannon', [s * 5.2, 0, 2.6], 1.3, s > 0 ? -Math.PI * 0.85 : Math.PI * 0.85));
  }
  group.add(prop(P, 'ribbon_banner', [0, 11.2, -7.5], 1.4));
  const crown = makeCrown(1.0);
  crown.scale.setScalar(0.62);
  group.add(crown);
  const confetti = new Confetti(QUALITY.confetti + 80, { x: 11, yTop: 13, z0: -8, z1: 8, floor: -1 }, reduced);
  confetti.ambient = false;
  group.add(confetti.mesh);
  const cl = clouds(group, 10, V(0, -8, 0), V(70, 6, 70), [2, 3.5], 199);
  // podium (Bolt, then the two Final runners-up) and everyone else back for the crown, in an arc round the back
  const CROWD = Array.from({ length: 12 }, (_, i) => {
    if (i === 0) return V(0, 2.3, 0);
    if (i === 1) return V(-3, 1.5, 0.4);
    if (i === 2) return V(3, 1.0, 0.4);
    const a = Math.PI * 0.36 + ((i - 3) / 8) * Math.PI * 1.28;
    return V(Math.sin(a) * 6.3, 0, Math.cos(a) * 6.3);
  });
  let prev = 0;
  const focus = A.clone().add(V(0, 2, 0));
  return {
    id: 'crown',
    group,
    focus,
    update(lp, ctx) {
      const { t, cast } = ctx;
      // crown drops onto Bolt's head with a bounce
      const u = span(lp, 0.12, 0.42);
      const headY = 2.3 + 1.62;
      crown.position.set(0, headY + (1 - backOut(u, 1.4)) * 10, 0.05);
      crown.rotation.y = (1 - u) * 12 + t * 0.6;
      crown.visible = u > 0;
      if (prev < 0.42 && lp >= 0.42) {
        play('confetti');
        play('qualify', { vol: 0.8 });
        ctx.kick(1.2);
        confetti.burst(V(-5, 1.8, 2.6), 140);
        confetti.burst(V(5, 1.8, 2.6), 140);
        confetti.burst(V(0, 4.5, 0), 90);
        cast.forEach((m) => m.rig.cheer(2.5));
      }
      confetti.ambient = lp > 0.42 && !reduced;
      confetti.update(ctx.dt);
      for (const c of cl) c.o.position.y = c.y + Math.sin(t * 0.4 + c.ph) * 0.3;
      cast.forEach((m, i) => {
        const p = CROWD[i];
        m.pos.set(A.x + p.x, p.y, A.z + p.z);
        m.vis = true;
        m.rx = 0;
        m.rz = 0;
        m.run = 0;
        m.yaw = i < 3 ? 0 : Math.atan2(-p.x, -p.z);
        m.celebrate = lp > 0.42 || i === 0;
        if (i >= 3 && !reduced && Math.sin(t * 2.3 + i * 1.7) > 0.985) m.rig.jump(0.8);
      });
      prev = lp;
    },
    cam(lp, out) {
      const e = ease(lp);
      const phi = -0.35 + e * 0.55;
      const r = 8 + e * 14;
      out.pos.set(A.x + Math.sin(phi) * r, 3.4 + e * 7, A.z + Math.cos(phi) * r);
      out.look.set(A.x, 3.1 - e * 0.8, A.z);
    },
    dispose() {
      mat.dispose();
      confetti.dispose();
    },
  };
}

