/**
 * Soft-toy geometry kit for the story dioramas (doc 17 soft look: pastel surfaces, coloured rims, bevels, soft shadows).
 * Static blocks of a chapter are merged into one vertex-coloured mesh (one draw call per chapter).
 */
import * as THREE from 'three';
import { RoundedBoxGeometry } from 'three/examples/jsm/geometries/RoundedBoxGeometry.js';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { PRESETS, type Preset } from '../../lib/presets';

// Palette tokens (soft look; same names as tools/blender/props.py SLOTS).
export const C = {
  ink: '#2A1250',
  walkTop: '#F2F0FF',
  walkSide: '#D9DDF7',
  cream: '#FFF3E0',
  soda: '#5FB8F0',
  tangerine: '#FFA45C',
  bubblegum: '#FF8CC0',
  apple: '#8EDB6A',
  grape: '#B08CF0',
  lemon: '#FFD860',
  mint: '#2FE0A8',
  magenta: '#FF3EA5',
  sun: '#FFD21F',
  goo: '#9B3CFF',
  white: '#FFFFFF',
};
export const RIMS = [C.soda, C.tangerine, C.bubblegum, C.apple, C.grape, C.lemon];

export const softMaterial = () => new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.5, metalness: 0 });

/** Flat vertex colour on every vertex. */
export function paint(g: THREE.BufferGeometry, color: THREE.ColorRepresentation) {
  const c = new THREE.Color(color);
  const n = g.attributes.position.count;
  const arr = new Float32Array(n * 3);
  for (let i = 0; i < n; i++) c.toArray(arr, i * 3);
  g.setAttribute('color', new THREE.BufferAttribute(arr, 3));
  return g;
}

/** Vertical gradient: vertices above `split` (local y) get `top`, below get `side` (a rim-coloured body with a pastel top). */
export function paintSplit(g: THREE.BufferGeometry, top: THREE.ColorRepresentation, side: THREE.ColorRepresentation, split: number) {
  const ct = new THREE.Color(top), cs = new THREE.Color(side);
  const pos = g.attributes.position;
  const nrm = g.attributes.normal;
  const arr = new Float32Array(pos.count * 3);
  for (let i = 0; i < pos.count; i++) (pos.getY(i) > split && nrm.getY(i) > 0.35 ? ct : cs).toArray(arr, i * 3);
  g.setAttribute('color', new THREE.BufferAttribute(arr, 3));
  return g;
}

export const rbox = (w: number, h: number, d: number, r = 0.14, seg = 2) => new RoundedBoxGeometry(w, h, d, seg, Math.min(r, w / 2, h / 2, d / 2) * 0.999);

/** Collects coloured geometry; `build()` merges it into one mesh. */
export class Merge {
  private parts: THREE.BufferGeometry[] = [];
  private m = new THREE.Matrix4();
  private q = new THREE.Quaternion();
  private e = new THREE.Euler();
  add(g: THREE.BufferGeometry, pos: [number, number, number] = [0, 0, 0], rot: [number, number, number] = [0, 0, 0], scale: [number, number, number] = [1, 1, 1]) {
    const geo = g.index ? g.toNonIndexed() : g.clone();
    if (g !== geo) g.dispose();
    for (const k of Object.keys(geo.attributes)) if (k !== 'position' && k !== 'normal' && k !== 'color') geo.deleteAttribute(k);
    this.q.setFromEuler(this.e.set(rot[0], rot[1], rot[2]));
    this.m.compose(new THREE.Vector3(...pos), this.q, new THREE.Vector3(...scale));
    geo.applyMatrix4(this.m);
    this.parts.push(geo);
    return this;
  }
  /** A toy block: rim-coloured rounded body with a pastel top (y = top surface height). */
  block(w: number, h: number, d: number, at: [number, number, number], rim: string, top: string = C.walkTop, rotY = 0) {
    const g = paintSplit(rbox(w, h, d, Math.min(0.18, h * 0.4)), top, rim, h / 2 - 0.12);
    return this.add(g, [at[0], at[1] - h / 2, at[2]], [0, rotY, 0]);
  }
  build(mat: THREE.Material, shadows = false) {
    const geo = mergeGeometries(this.parts, false)!;
    this.parts.forEach((p) => p.dispose());
    this.parts = [];
    geo.computeBoundingSphere();
    const mesh = new THREE.Mesh(geo, mat);
    mesh.castShadow = shadows;
    mesh.receiveShadow = shadows;
    return mesh;
  }
}

/** Deterministic hash 0..1 (so the scroll-driven scenes look the same both ways). */
export const hash = (n: number) => {
  const x = Math.sin(n * 127.1 + 311.7) * 43758.5453;
  return x - Math.floor(x);
};

/** The 12-Rushling cast: the 7 game presets plus 5 extra shells (looks only). Slot 0 = Bolt, the champion. */
const EXTRA: Preset[] = [
  { name: 'Cherry', shell: '#FF4D6D', emblem: 'heart', emblemColor: '#FFFFFF', topper: 'headphones', face: '#58E1FF', key: 'star', keyColor: '#FFD21F', personality: '' },
  { name: 'Sky', shell: '#58C4FF', emblem: 'moon', emblemColor: '#FFFFFF', topper: 'goggles', face: '#5BF0B5', key: 'classic', keyColor: '#FF3EA5', personality: '' },
  { name: 'Lime', shell: '#9BE15D', emblem: 'star', emblemColor: '#7B2FF7', topper: 'chef', face: '#FFB547', key: 'gear', keyColor: '#7B2FF7', personality: '' },
  { name: 'Plum', shell: '#B05CFF', emblem: 'diamond', emblemColor: '#FFD21F', topper: 'propeller', face: '#FF7AC6', key: 'classic', keyColor: '#FFD21F', personality: '' },
  { name: 'Coral', shell: '#FF9E80', emblem: 'flame', emblemColor: '#FFFFFF', topper: 'headphones', face: '#5BF0B5', key: 'star', keyColor: '#2A1250', personality: '' },
];
export const CAST: Preset[] = [...PRESETS, ...EXTRA];

/** The champion's crown (our own five-point shape, same as the hero's finish arch crown). */
export function makeCrown(emissive = 0.5) {
  const crown = new THREE.Group();
  const gold = new THREE.MeshStandardMaterial({ color: 0xffd21f, roughness: 0.22, emissive: 0xffa800, emissiveIntensity: emissive });
  const ring = new THREE.Mesh(new THREE.CylinderGeometry(0.62, 0.55, 0.42, 30, 1, true), gold);
  gold.side = THREE.DoubleSide;
  crown.add(ring);
  for (let i = 0; i < 5; i++) {
    const a = (i / 5) * Math.PI * 2;
    const spike = new THREE.Mesh(new THREE.ConeGeometry(0.18, 0.5, 12), gold);
    spike.position.set(Math.sin(a) * 0.58, 0.42, Math.cos(a) * 0.58);
    crown.add(spike);
    const gem = new THREE.Mesh(
      new THREE.SphereGeometry(0.09, 12, 8),
      new THREE.MeshStandardMaterial({ color: i % 2 ? 0xff3ea5 : 0x7b2ff7, roughness: 0.15, emissive: i % 2 ? 0xff3ea5 : 0x7b2ff7, emissiveIntensity: 0.6 }),
    );
    gem.position.set(Math.sin(a) * 0.6, 0.7, Math.cos(a) * 0.6);
    crown.add(gem);
  }
  return crown;
}

/** Gradient sky dome (follows the camera). `digital` 0..1 blends to the on-chain night with a star field. */
export function makeSky() {
  const mat = new THREE.ShaderMaterial({
    side: THREE.BackSide,
    depthWrite: false,
    fog: false,
    uniforms: {
      top: { value: new THREE.Color('#4FA6EC') },
      mid: { value: new THREE.Color('#9CD2F5') },
      bottom: { value: new THREE.Color('#F7E9FB') },
      nTop: { value: new THREE.Color('#0B0624') },
      nMid: { value: new THREE.Color('#25105A') },
      nBottom: { value: new THREE.Color('#3B1A7A') },
      digital: { value: 0 },
      time: { value: 0 },
    },
    vertexShader: /* glsl */ `
      varying vec3 vDir;
      void main() {
        vDir = normalize(position);
        gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
      }`,
    fragmentShader: /* glsl */ `
      uniform vec3 top, mid, bottom, nTop, nMid, nBottom;
      uniform float digital, time;
      varying vec3 vDir;
      float h21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
      void main() {
        float y = vDir.y;
        vec3 day = y > 0.0 ? mix(mid, top, smoothstep(0.0, 0.6, y)) : mix(mid, bottom, smoothstep(0.0, -0.5, y));
        vec3 night = y > 0.0 ? mix(nMid, nTop, smoothstep(0.0, 0.7, y)) : mix(nMid, nBottom, smoothstep(0.0, -0.5, y));
        // night: twinkling stars + a faint lattice (the rollup)
        vec2 uv = vec2(atan(vDir.z, vDir.x) * 40.0, y * 40.0);
        vec2 cell = floor(uv);
        float s = step(0.985, h21(cell)) * (0.6 + 0.4 * sin(time * 2.0 + h21(cell + 3.0) * 30.0));
        vec2 g = abs(fract(uv / 3.0) - 0.5);
        float grid = (1.0 - smoothstep(0.0, 0.03, min(g.x, g.y))) * 0.12 * smoothstep(-0.2, 0.5, y);
        night += vec3(0.75, 1.0, 0.9) * s + vec3(0.36, 0.94, 0.71) * grid;
        gl_FragColor = vec4(mix(day, night, digital), 1.0);
      }`,
  });
  const mesh = new THREE.Mesh(new THREE.SphereGeometry(450, 32, 16), mat);
  mesh.frustumCulled = false;
  mesh.renderOrder = -1;
  return { mesh, mat };
}
