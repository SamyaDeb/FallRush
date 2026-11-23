/**
 * Rushling: three.js port of client-unity RushlingParts.cs (mesh) + RushlingVisuals.cs (procedural animation).
 *
 * Body space matches Unity: feet on y = 0, body height 1.0, radius R 0.31, seam at 0.54, front = +Z.
 * (Unity is left-handed, three.js right-handed: outlines are mirrored in X so emblems read the same from the front.)
 *
 *   group (position on the floor, yaw)            <- the scene moves this
 *     tilt   (y 0.5 + bob + hop; waddle / lean / flop)
 *       squash (y -0.5; squash & stretch spring)
 *         shell lathe, dome lathe, seam, hinge, LED screen, emblem, key (spins), topper
 *     feet (stepping in group space, tucked into body space while airborne / flopped)
 *     arms (noodle tubes along a Catmull-Rom curve through a verlet chain, rebuilt per frame) + mittens
 */
import { forwardRef, useEffect, useImperativeHandle, useMemo } from 'react';
import { useFrame } from '@react-three/fiber';
import * as THREE from 'three';
import type { Preset } from '../lib/presets';
import { faceTexture, type FaceMode } from './textures';
import { blobShadowTexture } from './common';
import { QUALITY } from './quality';

let _blobGeo: THREE.PlaneGeometry | null = null;
const blobGeo = () => (_blobGeo ??= new THREE.PlaneGeometry(0.78, 0.78));

export const R = 0.31;
export const SEAM_Y = 0.54;
const BODY_BOTTOM = 0.08;
const LOWER_C = BODY_BOTTOM + R; // 0.39
const UPPER_C = 1 - R; // 0.69
const INK = '#2A1250';

export const radiusAt = (y: number) => {
  if (y > UPPER_C) return Math.sqrt(Math.max(0, R * R - (y - UPPER_C) ** 2));
  if (y < LOWER_C) return Math.sqrt(Math.max(0, R * R - (LOWER_C - y) ** 2));
  return R;
};

// ------------------------------------------------------------------ shared geometry (built once)

type V2 = [number, number];

const lathe = (top: boolean) => {
  const pts: THREE.Vector2[] = [];
  const N = 10;
  if (!top) {
    for (let i = 0; i <= N; i++) {
      const a = -Math.PI / 2 + (i / N) * (Math.PI / 2);
      pts.push(new THREE.Vector2(Math.max(1e-4, R * Math.cos(a)), LOWER_C + R * Math.sin(a)));
    }
    pts.push(new THREE.Vector2(R, SEAM_Y));
  } else {
    pts.push(new THREE.Vector2(R, SEAM_Y), new THREE.Vector2(R, UPPER_C));
    for (let i = 1; i <= N; i++) {
      const a = (i / N) * (Math.PI / 2);
      pts.push(new THREE.Vector2(Math.max(1e-4, R * Math.cos(a)), UPPER_C + R * Math.sin(a)));
    }
  }
  return new THREE.LatheGeometry(pts, 40);
};

/** ProcMesh.BuildFace: squircle patch over ±38° of the front, y 0.60 → 0.86, lifted 4 mm off the dome. */
const facePatch = () => {
  const cols = 14, rows = 9;
  const span = (38 * Math.PI) / 180;
  const y0 = 0.6, y1 = 0.86, lift = 0.004;
  const pos: number[] = [], nor: number[] = [], uv: number[] = [], idx: number[] = [];
  for (let r = 0; r <= rows; r++) {
    const v = r / rows;
    for (let c = 0; c <= cols; c++) {
      const u = c / cols;
      let qx = u * 2 - 1, qy = v * 2 - 1;
      const k = Math.max(Math.abs(qx), Math.abs(qy));
      const n4 = Math.pow(qx ** 4 + qy ** 4, 0.25);
      if (n4 > 1e-5) {
        qx *= k / n4;
        qy *= k / n4;
      }
      const y = y0 + (y1 - y0) * (qy + 1) * 0.5;
      const rad = radiusAt(y) + lift;
      const a = -span + 2 * span * (qx + 1) * 0.5;
      pos.push(Math.sin(a) * rad, y, Math.cos(a) * rad);
      const ny = y > UPPER_C ? (y - UPPER_C) / R : 0;
      const n = new THREE.Vector3(Math.sin(a) * (1 - ny), ny, Math.cos(a) * (1 - ny)).normalize();
      nor.push(n.x, n.y, n.z);
      uv.push((qx + 1) * 0.5, (qy + 1) * 0.5);
    }
  }
  for (let r = 0; r < rows; r++)
    for (let c = 0; c < cols; c++) {
      const a = r * (cols + 1) + c, b = a + cols + 1;
      idx.push(a, a + 1, b, a + 1, b + 1, b);
    }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(nor, 3));
  g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  g.setIndex(idx);
  return g;
};

const starOutline = (r0: number, r1: number, n: number): V2[] =>
  Array.from({ length: n * 2 }, (_, i) => {
    const a = Math.PI / 2 + (i * Math.PI) / n;
    const r = i % 2 ? r1 : r0;
    return [Math.cos(a) * r, Math.sin(a) * r];
  });

const gearOutline = (r0: number, r1: number, teeth: number): V2[] => {
  const o: V2[] = [];
  for (let i = 0; i < teeth; i++) {
    const a = (i * Math.PI * 2) / teeth, w = (Math.PI / teeth) * 0.5;
    o.push([Math.cos(a - w) * r1, Math.sin(a - w) * r1]);
    o.push([Math.cos(a - w * 0.6) * r0, Math.sin(a - w * 0.6) * r0]);
    o.push([Math.cos(a + w * 0.6) * r0, Math.sin(a + w * 0.6) * r0]);
    o.push([Math.cos(a + w) * r1, Math.sin(a + w) * r1]);
  }
  return o;
};

const heartOutline = (s: number): V2[] =>
  Array.from({ length: 28 }, (_, i) => {
    const t = (i / 28) * Math.PI * 2;
    const x = 16 * Math.sin(t) ** 3;
    const y = 13 * Math.cos(t) - 5 * Math.cos(2 * t) - 2 * Math.cos(3 * t) - Math.cos(4 * t);
    return [x * (s / 16), y * (s / 16)];
  });

const flameOutline = (s: number): V2[] =>
  ([
    [0, 1], [0.25, 0.55], [0.45, 0.7], [0.55, 0.15], [0.4, -0.45], [0, -0.65],
    [-0.4, -0.45], [-0.55, 0.05], [-0.35, 0.45], [-0.2, 0.2],
  ] as V2[]).map(([x, y]) => [x * s, y * s]);

const moonOutline = (s: number): V2[] => {
  const o: V2[] = [];
  for (let i = 0; i <= 12; i++) {
    const a = ((-140 + (280 * i) / 12) * Math.PI) / 180;
    o.push([Math.cos(a) * s, Math.sin(a) * s]);
  }
  for (let i = 12; i >= 0; i--) {
    const a = ((-110 + (220 * i) / 12) * Math.PI) / 180;
    o.push([(Math.cos(a) * 0.7 + 0.38) * s, Math.sin(a) * 0.78 * s]);
  }
  return o;
};

const boltOutline: V2[] = [
  [0.02, 0.1], [0.06, 0.1], [0.01, 0.01], [0.05, 0.01],
  [-0.05, -0.11], [-0.01, -0.02], [-0.05, -0.02], [-0.01, 0.1],
];

const EMBLEMS: Record<Preset['emblem'], V2[]> = {
  bolt: boltOutline,
  star: starOutline(0.085, 0.037, 5),
  heart: heartOutline(0.08),
  flame: flameOutline(0.1),
  diamond: [[0, 0.1], [0.065, 0], [0, -0.1], [-0.065, 0]],
  moon: moonOutline(0.085),
};

/** Flat outline extruded toward +Z (front face at z = depth). X mirrored (Unity → three handedness). */
const extrude = (o: V2[], depth: number, bevel = 0.004) => {
  const s = new THREE.Shape(o.map(([x, y]) => new THREE.Vector2(-x, y)));
  const g = new THREE.ExtrudeGeometry(s, {
    depth,
    bevelEnabled: bevel > 0,
    bevelThickness: bevel,
    bevelSize: bevel * 0.8,
    bevelSegments: 2,
    curveSegments: 6,
  });
  g.computeVertexNormals();
  return g;
};

let G: ReturnType<typeof buildShared> | null = null;
function buildShared() {
  const emblem = {} as Record<Preset['emblem'], THREE.BufferGeometry>;
  for (const k of Object.keys(EMBLEMS) as Preset['emblem'][]) emblem[k] = extrude(EMBLEMS[k], 0.022);
  const foot = new THREE.CapsuleGeometry(0.052, 0.07, 4, 12);
  foot.rotateX(Math.PI / 2);
  return {
    shell: lathe(false),
    dome: lathe(true),
    face: facePatch(),
    seam: new THREE.TorusGeometry(R + 0.004, 0.017, 8, 56).rotateX(Math.PI / 2),
    hinge: new THREE.BoxGeometry(0.1, 0.035, 0.03),
    emblem,
    keyShaft: new THREE.CylinderGeometry(0.022, 0.022, 0.1, 10).rotateX(Math.PI / 2),
    keyLoop: new THREE.TorusGeometry(0.068, 0.022, 8, 22),
    keyStar: extrude(starOutline(0.13, 0.058, 5), 0.03, 0.008),
    keyGear: extrude(gearOutline(0.085, 0.064, 8), 0.028, 0.006),
    keyHub: new THREE.SphereGeometry(0.03, 12, 8),
    foot,
    mitt: new THREE.SphereGeometry(1, 14, 10),
    sphere: new THREE.SphereGeometry(1, 20, 14),
    cyl: new THREE.CylinderGeometry(1, 1, 1, 24),
    ring: new THREE.TorusGeometry(1, 0.13, 10, 28),
    strap: new THREE.TorusGeometry(1, 0.05, 6, 48),
    band: new THREE.TorusGeometry(1, 0.075, 8, 32, Math.PI),
    blade: new THREE.BoxGeometry(0.34, 0.014, 0.065),
  };
}
const shared = () => (G ??= buildShared());

// ------------------------------------------------------------------ noodle arm tube

const ARM_POINTS = 6;
const ARM_SEG = 0.085;
const TUBE_R = 0.042;
const TUBE_S = 14; // samples along the curve
const TUBE_SIDES = 8;

class NoodleTube {
  geo = new THREE.BufferGeometry();
  private pos: Float32Array;
  private nor: Float32Array;
  private curve: THREE.CatmullRomCurve3;
  private tmp = { p: new THREE.Vector3(), t: new THREE.Vector3(), n: new THREE.Vector3(), b: new THREE.Vector3(), prevN: new THREE.Vector3(), q: new THREE.Vector3() };
  lastTangent = new THREE.Vector3(0, -1, 0);

  constructor(public points: THREE.Vector3[]) {
    const nV = (TUBE_S + 1) * (TUBE_SIDES + 1);
    this.pos = new Float32Array(nV * 3);
    this.nor = new Float32Array(nV * 3);
    const idx: number[] = [];
    for (let i = 0; i < TUBE_S; i++)
      for (let j = 0; j < TUBE_SIDES; j++) {
        const a = i * (TUBE_SIDES + 1) + j, b = a + TUBE_SIDES + 1;
        idx.push(a, b, a + 1, a + 1, b, b + 1);
      }
    this.geo.setIndex(idx);
    this.geo.setAttribute('position', new THREE.BufferAttribute(this.pos, 3).setUsage(THREE.DynamicDrawUsage));
    this.geo.setAttribute('normal', new THREE.BufferAttribute(this.nor, 3).setUsage(THREE.DynamicDrawUsage));
    this.curve = new THREE.CatmullRomCurve3(points, false, 'centripetal');
  }

  rebuild() {
    const { p, t, n, b, prevN, q } = this.tmp;
    for (let i = 0; i <= TUBE_S; i++) {
      const u = i / TUBE_S;
      this.curve.getPoint(u, p);
      this.curve.getTangent(Math.min(0.999, Math.max(0.001, u)), t);
      if (i === 0) {
        // initial normal: anything perpendicular to the tangent
        q.set(0, 0, 1);
        if (Math.abs(t.dot(q)) > 0.9) q.set(1, 0, 0);
        n.crossVectors(t, q).normalize();
      } else {
        // parallel transport: project the previous normal onto the new normal plane
        n.copy(prevN).addScaledVector(t, -prevN.dot(t)).normalize();
      }
      prevN.copy(n);
      b.crossVectors(t, n);
      const r = TUBE_R * (i === 0 ? 0.9 : 1);
      for (let j = 0; j <= TUBE_SIDES; j++) {
        const a = (j / TUBE_SIDES) * Math.PI * 2;
        const cx = Math.cos(a), sx = Math.sin(a);
        const nx = n.x * cx + b.x * sx, ny = n.y * cx + b.y * sx, nz = n.z * cx + b.z * sx;
        const k = (i * (TUBE_SIDES + 1) + j) * 3;
        this.pos[k] = p.x + nx * r;
        this.pos[k + 1] = p.y + ny * r;
        this.pos[k + 2] = p.z + nz * r;
        this.nor[k] = nx;
        this.nor[k + 1] = ny;
        this.nor[k + 2] = nz;
      }
    }
    this.lastTangent.copy(t);
    this.geo.attributes.position.needsUpdate = true;
    this.geo.attributes.normal.needsUpdate = true;
  }
}

// ------------------------------------------------------------------ the rig

export type RushlingPose = 'auto' | 'celebrate';

export interface RushlingInput {
  /** 0 = standing, 1 = full sprint (drives gait, bob, waddle, arm swing, key spin). */
  run: number;
}

const _m = new THREE.Matrix4();
const _q = new THREE.Quaternion();
const _right = new THREE.Vector3();
const _up = new THREE.Vector3();
const _fwd = new THREE.Vector3();
const _dir = new THREE.Vector3();
const _v = new THREE.Vector3();
const _d = new THREE.Vector3();
const _sh = new THREE.Vector3();
const Z = new THREE.Vector3(0, 0, 1);

let nextId = 1;

export class RushlingRig {
  readonly id = nextId++;
  readonly group = new THREE.Group();
  readonly tilt = new THREE.Group();
  readonly squash = new THREE.Group();
  readonly preset: Preset;
  pose: RushlingPose = 'auto';
  /** Multiplies every motion (prefers-reduced-motion runs the scene slower). */
  timeScale = 1;

  private faceMat: THREE.MeshStandardMaterial;
  private keySpin = new THREE.Group();
  private propeller: THREE.Group | null = null;
  private feet: THREE.Mesh[] = [];
  private arms: { side: number; p: THREE.Vector3[]; prev: THREE.Vector3[]; tube: NoodleTube; mitt: THREE.Mesh; thumb: THREE.Mesh }[] = [];
  private mats: THREE.Material[] = [];
  private ownGeos: THREE.BufferGeometry[] = [];

  // animation state
  private t = Math.random() * 10;
  private gait = Math.random() * 6;
  private run = 0;
  private sq = 0;
  private sqV = 0;
  private hop = 0;
  private vy = 0;
  private air = false;
  private antic = 0; // take-off anticipation timer
  private hitT = 0;
  private lie = 0;
  private starT = 0;
  private blinkT = 0;
  private nextBlink = 1 + Math.random() * 3;
  private armsInit = false;
  private face: FaceMode = 'happy';

  private blob: THREE.Mesh | null = null;

  /** shadows: cast real-time shadows (big parts only). blob: soft contact shadow instead (when shadows are off). */
  constructor(preset: Preset, opts: { shadows?: boolean; blob?: boolean } = {}) {
    this.preset = preset;
    const g = shared();
    const cast = opts.shadows !== false;
    const mat = (color: string, roughness = 0.3, extra: THREE.MeshStandardMaterialParameters = {}) => {
      const m = new THREE.MeshStandardMaterial({ color, roughness, metalness: 0, ...extra });
      this.mats.push(m);
      return m;
    };
    const add = (geo: THREE.BufferGeometry, m: THREE.Material, parent: THREE.Object3D = this.squash) => {
      const o = new THREE.Mesh(geo, m);
      o.castShadow = false; // small parts: no shadow pass (the shell, dome, arms and feet carry the silhouette)
      parent.add(o);
      return o;
    };

    this.group.add(this.tilt);
    this.tilt.position.y = 0.5;
    this.tilt.rotation.order = 'YXZ';
    this.tilt.add(this.squash);
    this.squash.position.y = -0.5;

    const shell = mat(preset.shell, 0.3);
    const white = mat('#FAFAFF', 0.2);
    const ink = mat(INK, 0.45);

    add(g.shell, shell).castShadow = cast;
    add(g.dome, white).castShadow = cast;
    add(g.seam, ink).position.y = SEAM_Y;
    add(g.hinge, ink).position.set(0, SEAM_Y, -R - 0.008);

    // LED screen
    this.faceMat = mat('#FFFFFF', 0.18, {
      map: faceTexture('happy', preset.face),
      emissive: new THREE.Color('#FFFFFF'),
      emissiveMap: faceTexture('happy', preset.face),
      emissiveIntensity: 0.85,
    });
    add(g.face, this.faceMat).castShadow = false;

    // emblem decal on the lower front (front face at z ≈ R + 0.012)
    const em = add(g.emblem[preset.emblem], mat(preset.emblemColor, 0.35));
    em.position.set(0, 0.43, R - 0.012);
    em.scale.setScalar(0.85);

    // wind-up key on the back (spins about the shaft)
    const keyM = mat(preset.keyColor, 0.28);
    const key = new THREE.Group();
    key.position.set(0, 0.5, -R - 0.005);
    this.squash.add(key);
    add(g.keyShaft, keyM, key).position.z = -0.05;
    this.keySpin.position.z = -0.11;
    key.add(this.keySpin);
    if (preset.key === 'classic') {
      for (const s of [-1, 1]) {
        const l = add(g.keyLoop, keyM, this.keySpin);
        l.position.x = s * 0.085;
        l.scale.set(1.05, 0.82, 1);
      }
      add(g.keyHub, keyM, this.keySpin);
    } else {
      const k = add(preset.key === 'star' ? g.keyStar : g.keyGear, keyM, this.keySpin);
      k.position.z = -0.015;
    }

    this.buildTopper(preset, mat, add);

    // feet (key colour, like the logo's purple feet)
    const footM = mat(preset.keyColor, 0.35);
    for (let f = 0; f < 2; f++) {
      const m = new THREE.Mesh(g.foot, footM);
      m.castShadow = cast;
      m.scale.set(1, 0.85, 1);
      this.group.add(m);
      this.feet.push(m);
    }

    // noodle arms + mittens (shell colour, like the logo)
    const armM = mat(preset.shell, 0.32);
    for (const side of [-1, 1]) {
      const p = Array.from({ length: ARM_POINTS }, () => new THREE.Vector3());
      const prev = Array.from({ length: ARM_POINTS }, () => new THREE.Vector3());
      const tube = new NoodleTube(p);
      this.ownGeos.push(tube.geo);
      const mesh = new THREE.Mesh(tube.geo, armM);
      mesh.castShadow = cast;
      mesh.frustumCulled = false;
      this.group.add(mesh);
      const mitt = new THREE.Mesh(g.mitt, armM);
      mitt.castShadow = cast;
      mitt.scale.set(0.058, 0.05, 0.068);
      this.group.add(mitt);
      const thumb = new THREE.Mesh(g.mitt, armM);
      mitt.add(thumb);
      thumb.position.set(-side * 0.75, 0.55, 0.2);
      thumb.scale.set(0.42, 0.48, 0.44);
      this.arms.push({ side, p, prev, tube, mitt, thumb });
    }

    if (opts.blob) {
      const bm = new THREE.MeshBasicMaterial({ map: blobShadowTexture(), transparent: true, depthWrite: false });
      this.mats.push(bm);
      this.blob = new THREE.Mesh(blobGeo(), bm);
      this.blob.rotation.x = -Math.PI / 2;
      this.blob.position.y = 0.006;
      this.blob.renderOrder = -1;
      this.group.add(this.blob);
    }

    this.group.traverse((o) => (o.userData.rushling = this.id));
    this.update(0, { run: 0 });
  }

  private buildTopper(
    p: Preset,
    mat: (c: string, r?: number, e?: THREE.MeshStandardMaterialParameters) => THREE.MeshStandardMaterial,
    add: (geo: THREE.BufferGeometry, m: THREE.Material, parent?: THREE.Object3D) => THREE.Mesh,
  ) {
    const g = shared();
    const top = new THREE.Group();
    this.squash.add(top);
    if (p.topper === 'goggles') {
      const strapY = 0.84;
      const sr = radiusAt(strapY) + 0.008;
      const strap = add(g.strap, mat('#5A3A1E', 0.6), top);
      strap.rotation.x = Math.PI / 2 - (12 * Math.PI) / 180;
      strap.position.y = strapY;
      strap.scale.set(sr, sr, 0.75);
      const gold = mat('#FFD21F', 0.22);
      const lens = mat('#3FA9F5', 0.05, { metalness: 0.15 });
      for (const s of [-1, 1]) {
        // goggles pushed up on the forehead, facing forward-up (Unity: −50° pitch, ±18° yaw)
        const ly = 0.88;
        const lz = radiusAt(ly) + 0.012;
        const n = new THREE.Vector3(s * Math.sin((20 * Math.PI) / 180), Math.sin((38 * Math.PI) / 180), Math.cos((38 * Math.PI) / 180)).normalize();
        const holder = new THREE.Group();
        holder.position.set(s * 0.095, ly, lz);
        holder.quaternion.setFromUnitVectors(Z, n);
        top.add(holder);
        const ring = add(g.ring, gold, holder);
        ring.scale.set(0.072, 0.072, 0.16);
        const l = add(g.cyl, lens, holder);
        l.rotation.x = Math.PI / 2;
        l.scale.set(0.064, 0.02, 0.064);
      }
    } else if (p.topper === 'propeller') {
      const gold = mat('#FFD21F', 0.25);
      const hub = add(g.cyl, gold, top);
      hub.position.y = 1.03;
      hub.scale.set(0.028, 0.08, 0.028);
      const ball = add(g.sphere, gold, top);
      ball.position.y = 1.075;
      ball.scale.setScalar(0.032);
      this.propeller = new THREE.Group();
      this.propeller.position.y = 1.075;
      top.add(this.propeller);
      add(g.blade, mat('#FF3EA5', 0.3), this.propeller).rotation.y = Math.PI / 6;
      add(g.blade, mat('#3FA9F5', 0.3), this.propeller).rotation.y = (2 * Math.PI) / 3;
    } else if (p.topper === 'chef') {
      const w = mat('#FFFFFF', 0.55);
      const band = add(g.cyl, w, top);
      band.position.set(0, 0.995, -0.02);
      band.scale.set(0.13, 0.08, 0.13);
      const puff = add(g.sphere, w, top);
      puff.position.set(0, 1.11, -0.02);
      puff.scale.set(0.17, 0.1, 0.17);
      for (let i = 0; i < 3; i++) {
        const b = add(g.sphere, w, top);
        const a = (i / 3) * Math.PI * 2 + 0.4;
        b.position.set(Math.sin(a) * 0.08, 1.14, -0.02 + Math.cos(a) * 0.08);
        b.scale.setScalar(0.085);
      }
    } else if (p.topper === 'headphones') {
      const band = add(g.band, mat(INK, 0.4), top);
      band.position.y = 0.72;
      band.scale.set(R + 0.03, 0.32, 0.55);
      const cupM = mat('#FF3EA5', 0.3);
      for (const s of [-1, 1]) {
        const cup = add(g.cyl, cupM, top);
        cup.rotation.z = Math.PI / 2;
        cup.position.set(s * (R + 0.022), 0.74, 0);
        cup.scale.set(0.075, 0.06, 0.075);
        const pad = add(g.cyl, mat('#FFD21F', 0.35), top);
        pad.rotation.z = Math.PI / 2;
        pad.position.set(s * (R + 0.056), 0.74, 0);
        pad.scale.set(0.045, 0.012, 0.045);
      }
    }
  }

  // ------------------------------------------------------------ actions

  get airborne() {
    return this.air || this.antic > 0;
  }
  get knocked() {
    return this.hitT > 0;
  }

  /** Jump: anticipation squash → stretch → land squash, arms up, O O eyes. */
  jump(power = 1) {
    if (this.air || this.antic > 0 || this.hitT > 0) return false;
    this.antic = 0.08;
    this.vy = 4.4 * power;
    return true;
  }

  /** Star eyes for a while (and a hop). */
  cheer(seconds = 1.4, hop = true) {
    this.starT = Math.max(this.starT, seconds);
    if (hop) this.jump();
  }

  /** Knocked over: flop back ~80°, dizzy face, pop back up. */
  hit(seconds = 1.25) {
    if (this.hitT > 0) return false;
    this.hitT = seconds;
    this.antic = 0;
    this.air = false;
    this.hop = 0;
    this.vy = 0;
    this.sqV -= 2;
    return true;
  }

  // ------------------------------------------------------------ per frame

  update(dtIn: number, input: RushlingInput) {
    const dt = Math.min(0.05, dtIn) * this.timeScale;
    this.t += dt;
    const t = this.t;
    const celebrate = this.pose === 'celebrate';

    // run amount (smoothed)
    const want = this.hitT > 0 || celebrate ? 0 : Math.max(0, Math.min(1, input.run));
    this.run += (want - this.run) * Math.min(1, dt * 6);
    const run = this.run;
    const walking = run > 0.05 && !this.air && this.hitT <= 0;
    if (walking) this.gait += dt * (7 + 6.5 * run);

    // jump physics (hop is inside the rig: the group stays on the floor)
    if (this.antic > 0) {
      this.antic -= dt;
      if (this.antic <= 0) {
        this.air = true;
        this.sqV += 3.2; // spring into a stretch
      }
    }
    if (this.air) {
      this.vy -= 11 * dt;
      this.hop += this.vy * dt;
      if (this.hop <= 0) {
        this.hop = 0;
        this.air = false;
        this.vy = 0;
        this.sqV -= 3.6; // land squash
      }
    }

    // knocked over
    if (this.hitT > 0) {
      const total = 1.25;
      this.hitT -= dt;
      const u = 1 - Math.max(0, this.hitT) / total;
      this.lie = u < 0.14 ? u / 0.14 : u > 0.78 ? Math.max(0, 1 - (u - 0.78) / 0.16) : 1;
      if (this.hitT <= 0) {
        this.lie = 0;
        this.hitT = 0;
        this.antic = 0;
        this.vy = 2.6; // pop back up
        this.air = true;
        this.sqV += 2;
      }
    } else this.lie = 0;

    // squash & stretch spring (~0.18 s)
    let sqTarget = Math.sin(t * 2.3) * 0.014; // breathing
    if (this.antic > 0) sqTarget = -0.16;
    else if (this.air) sqTarget = this.vy > 0 ? 0.08 : 0.03;
    else if (celebrate) sqTarget = 0.04 + Math.sin(t * 5) * 0.02;
    this.sqV += ((sqTarget - this.sq) * 380 - this.sqV * 17) * dt;
    this.sq += this.sqV * dt;
    this.sq = Math.max(-0.3, Math.min(0.3, this.sq));
    const sy = 1 + this.sq;
    const sxz = 1 / Math.sqrt(sy);
    this.squash.scale.set(sxz, sy, sxz);

    // gait: bob, waddle roll ±7°, yaw ±5°, forward lean
    const gk = walking ? Math.min(1, run * 1.4) : 0;
    const bob = walking ? -Math.abs(Math.cos(this.gait)) * 0.045 * gk + 0.025 * gk : 0;
    const roll = Math.sin(this.gait) * ((7 * Math.PI) / 180) * gk + (this.hitT > 0 ? Math.sin(t * 18) * 0.12 * this.lie : 0);
    const yaw = Math.sin(this.gait) * ((5 * Math.PI) / 180) * gk + (celebrate ? Math.sin(t * 2.2) * 0.12 : 0);
    const lean = 0.1 * run - this.lie * ((80 * Math.PI) / 180) + (this.air ? -0.08 : 0);
    this.tilt.position.y = 0.5 - 0.18 * this.lie + bob + this.hop;
    if (this.blob) {
      const k = 1 / (1 + this.hop * 0.9);
      this.blob.scale.set(k * (1 + this.lie * 0.6), k * (1 + this.lie * 0.9), 1);
      (this.blob.material as THREE.MeshBasicMaterial).opacity = Math.max(0.25, k);
    }
    this.tilt.rotation.set(lean, yaw, roll);

    // key + propeller
    this.keySpin.rotation.z += dt * (3 + 9 * run + (this.air ? 16 : 0) + (celebrate ? 6 : 0));
    if (this.propeller) this.propeller.rotation.y += dt * (9 + 14 * run + (this.air ? 20 : 0));

    // face
    this.nextBlink -= dt;
    if (this.nextBlink <= 0) {
      this.blinkT = 0.12;
      this.nextBlink = 2 + Math.random() * 4;
    }
    if (this.blinkT > 0) this.blinkT -= dt;
    if (this.starT > 0) this.starT -= dt;
    const mode: FaceMode =
      this.hitT > 0 ? 'dizzy'
      : celebrate || this.starT > 0 ? 'star'
      : this.airborne ? 'jump'
      : this.blinkT > 0 ? 'blink'
      : run > 0.25 ? 'run'
      : 'happy';
    if (mode !== this.face) {
      this.face = mode;
      const tex = faceTexture(mode, this.preset.face);
      this.faceMat.map = tex;
      this.faceMat.emissiveMap = tex;
    }

    this.tilt.updateMatrix();
    this.squash.updateMatrix();
    _m.multiplyMatrices(this.tilt.matrix, this.squash.matrix); // body space → group space
    this.updateFeet(walking, gk);
    this.updateArms(dt, walking, gk);
  }

  private updateFeet(walking: boolean, gk: number) {
    const tucked = this.air || this.antic > 0 || this.hitT > 0;
    for (let f = 0; f < 2; f++) {
      const side = f === 0 ? -0.12 : 0.12;
      const foot = this.feet[f];
      if (tucked) {
        // follow the body pose, a little kick while in the air
        const kick = this.hitT > 0 ? Math.sin(this.t * 20 + f * Math.PI) * 0.05 : 0;
        _v.set(side, 0.05, 0.03 + kick).applyMatrix4(_m);
        foot.position.copy(_v);
        foot.quaternion.copy(this.tilt.quaternion);
      } else {
        const ph = this.gait + f * Math.PI;
        const stride = walking ? 0.12 * gk : 0;
        foot.position.set(side, 0.05 + (walking ? Math.max(0, Math.cos(ph)) * 0.07 * gk : 0), 0.03 + Math.sin(ph) * stride);
        foot.rotation.set(walking ? -Math.cos(ph) * 0.25 * gk : 0, 0, 0);
      }
    }
  }

  private updateArms(dt: number, walking: boolean, gk: number) {
    _q.copy(this.tilt.quaternion);
    _right.set(1, 0, 0).applyQuaternion(_q);
    _up.set(0, 1, 0).applyQuaternion(_q);
    _fwd.set(0, 0, 1).applyQuaternion(_q);
    const t = this.t;
    const celebrate = this.pose === 'celebrate' || this.starT > 0.3;
    // substep the verlet at ≤ 1/60 s for stability
    const steps = Math.max(1, Math.ceil(dt / (1 / 60)));
    const h = dt / steps;

    for (let a = 0; a < 2; a++) {
      const arm = this.arms[a];
      const s = arm.side;
      const sh = _sh.set(s * (R - 0.01), 0.5, 0.02).applyMatrix4(_m);

      if (this.hitT > 0) {
        _dir.copy(_right).multiplyScalar(s * 0.9).addScaledVector(_up, Math.sin(t * 18 + a * 2) * 0.6);
      } else if (this.air || this.antic > 0) {
        const f = Math.sin(t * 13 + a * 2.1);
        _dir.copy(_right).multiplyScalar(s * 0.55).addScaledVector(_up, 0.95 + 0.35 * f).addScaledVector(_fwd, 0.2 * Math.cos(t * 9 + a));
      } else if (celebrate) {
        _dir.copy(_right).multiplyScalar(s * 0.38).addScaledVector(_up, 1).addScaledVector(_fwd, 0.12 * Math.sin(t * 9 + a * 3));
      } else {
        // hanging out and down, swinging opposite the foot on the same side
        const ph = this.gait + (s < 0 ? 0 : Math.PI);
        const swing = walking ? -Math.sin(ph) * gk : 0;
        _dir.copy(_right).multiplyScalar(s * 0.48).addScaledVector(_up, -0.85).addScaledVector(_fwd, 0.1 + swing * 0.75);
      }
      _dir.normalize();

      if (!this.armsInit) {
        for (let i = 0; i < ARM_POINTS; i++) {
          arm.p[i].copy(sh).addScaledVector(_dir, i * ARM_SEG);
          arm.prev[i].copy(arm.p[i]);
        }
      }

      const k = this.hitT > 0 ? 260 : this.air ? 240 : 170;
      for (let st = 0; st < steps; st++) {
        const h2 = h * h;
        for (let i = 1; i < ARM_POINTS; i++) {
          const p = arm.p[i];
          const tx = sh.x + _dir.x * i * ARM_SEG, ty = sh.y + _dir.y * i * ARM_SEG, tz = sh.z + _dir.z * i * ARM_SEG;
          // spring toward the pose + gravity + a little drag behind when running
          const ax = (tx - p.x) * k, ay = (ty - p.y) * k - 6, az = (tz - p.z) * k - this.run * 2.5;
          const nx = p.x + (p.x - arm.prev[i].x) * 0.86 + ax * h2;
          const ny = p.y + (p.y - arm.prev[i].y) * 0.86 + ay * h2;
          const nz = p.z + (p.z - arm.prev[i].z) * 0.86 + az * h2;
          arm.prev[i].copy(p);
          p.set(nx, ny, nz);
        }
        arm.p[0].copy(sh);
        arm.prev[0].copy(sh);
        for (let it = 0; it < 3; it++) {
          for (let i = 1; i < ARM_POINTS; i++) {
            const d = _d.subVectors(arm.p[i], arm.p[i - 1]);
            const l = d.length();
            if (l < 1e-5) continue;
            d.multiplyScalar((l - ARM_SEG) / l);
            if (i === 1) arm.p[i].sub(d);
            else {
              arm.p[i].addScaledVector(d, -0.5);
              arm.p[i - 1].addScaledVector(d, 0.5);
            }
          }
          arm.p[0].copy(sh);
        }
      }
      // keep the noodles above the floor
      for (let i = 1; i < ARM_POINTS; i++) if (arm.p[i].y < 0.04) arm.p[i].y = 0.04;

      arm.tube.rebuild();
      arm.mitt.position.copy(arm.p[ARM_POINTS - 1]);
      arm.mitt.quaternion.setFromUnitVectors(Z, arm.tube.lastTangent);
    }
    this.armsInit = true;
  }

  dispose() {
    for (const m of this.mats) m.dispose();
    for (const g of this.ownGeos) g.dispose();
    this.group.removeFromParent();
  }
}

// ------------------------------------------------------------------ R3F wrapper

export interface RushlingProps {
  preset: Preset;
  run?: number;
  pose?: RushlingPose;
  position?: [number, number, number];
  rotationY?: number;
  scale?: number;
  timeScale?: number;
  onClick?: () => void;
}

/** Declarative Rushling for R3F scenes. The ref exposes the rig (jump / cheer / hit). */
export const Rushling = forwardRef<RushlingRig, RushlingProps>(function Rushling(
  { preset, run = 0, pose = 'auto', position = [0, 0, 0], rotationY = 0, scale = 1, timeScale = 1, onClick },
  ref,
) {
  const rig = useMemo(() => new RushlingRig(preset, { shadows: QUALITY.shadows, blob: !QUALITY.shadows }), [preset]);
  useImperativeHandle(ref, () => rig, [rig]);
  useEffect(() => () => rig.dispose(), [rig]);
  rig.pose = pose;
  rig.timeScale = timeScale;
  useFrame((_, dt) => rig.update(dt, { run }));
  return (
    <primitive
      object={rig.group}
      position={position}
      rotation-y={rotationY}
      scale={scale}
      onClick={
        onClick &&
        ((e: { stopPropagation: () => void }) => {
          e.stopPropagation();
          onClick();
        })
      }
    />
  );
});
