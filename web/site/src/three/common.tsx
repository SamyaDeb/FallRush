import { useEffect, useRef, useState } from 'react';
import { useFrame, useThree } from '@react-three/fiber';
import * as THREE from 'three';
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';

export const prefersReducedMotion = () =>
  typeof window !== 'undefined' && !!window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

/**
 * True while the element is on screen (drives Canvas frameloop). Hidden tabs need no check here: browsers stop
 * requestAnimationFrame in background tabs, so the render loop already pauses (and resumes reliably) on its own.
 */
export function useActive(ref: React.RefObject<HTMLElement | null>) {
  const [onScreen, setOnScreen] = useState(true);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const io = new IntersectionObserver((e) => setOnScreen(e[0].isIntersecting));
    io.observe(el);
    return () => io.disconnect();
  }, [ref]);
  return onScreen;
}

/**
 * Keeps the frame rate up: every 2 s, if the scene ran under ~45 fps, lower the canvas pixel ratio a step (down to
 * `min`); after a sustained stretch at ~58+ fps, raise it back toward the starting value.
 */
export function AdaptiveDpr({ min, max }: { min: number; max: number }) {
  const setDpr = useThree((s) => s.setDpr);
  const a = useRef({ t: 0, n: 0, good: 0, dpr: Math.min(typeof window === 'undefined' ? 1 : window.devicePixelRatio || 1, max) });
  useFrame((_, dt) => {
    const s = a.current;
    if (dt > 0.25) return; // tab was hidden / throttled: not a real measurement
    s.t += dt;
    s.n++;
    if (s.t < 2) return;
    const fps = s.n / s.t;
    s.t = 0;
    s.n = 0;
    if (fps < 45 && s.dpr > min) {
      s.dpr = Math.max(min, +(s.dpr - 0.25).toFixed(2));
      s.good = 0;
      setDpr(s.dpr);
    } else if (fps > 58) {
      if (++s.good >= 3 && s.dpr < max) {
        s.dpr = Math.min(max, +(s.dpr + 0.25).toFixed(2));
        s.good = 0;
        setDpr(s.dpr);
      }
    } else s.good = 0;
  });
  return null;
}

/** Calls `onShown` once the scene has actually drawn a couple of frames (shaders compiled), for a fade-in. */
export function FirstFrames({ onShown }: { onShown: () => void }) {
  const n = useRef(0);
  useFrame(() => {
    if (n.current < 0) return;
    if (++n.current >= 2) {
      n.current = -1;
      onShown();
    }
  });
  return null;
}

/** Soft daylight: hemisphere + shadowed sun (1024 map) + pink rim + a tiny room environment for the vinyl gloss. */
export function Daylight({ shadowExtent = 7, sunPos = [5, 11, 7] as [number, number, number], shadows = true, shadowMap = 1024 }) {
  const { gl, scene } = useThree();
  useEffect(() => {
    const pmrem = new THREE.PMREMGenerator(gl);
    const room = new RoomEnvironment();
    const env = pmrem.fromScene(room, 0.04).texture;
    scene.environment = env;
    scene.environmentIntensity = 0.35;
    return () => {
      scene.environment = null;
      env.dispose();
      pmrem.dispose();
      room.traverse((o) => {
        const m = o as THREE.Mesh;
        if (m.geometry) m.geometry.dispose();
        const mat = m.material as THREE.Material | undefined;
        if (mat) mat.dispose();
      });
    };
  }, [gl, scene]);
  const sun = useRef<THREE.DirectionalLight>(null);
  useEffect(() => {
    const s = sun.current;
    if (!s) return;
    const c = s.shadow.camera;
    c.left = -shadowExtent;
    c.right = shadowExtent;
    c.top = shadowExtent;
    c.bottom = -shadowExtent;
    c.near = 1;
    c.far = 40;
    c.updateProjectionMatrix();
  }, [shadowExtent]);
  return (
    <>
      <hemisphereLight args={['#EFF6FF', '#F3C9E4', 1.15]} />
      <directionalLight
        ref={sun}
        position={sunPos}
        intensity={2.1}
        color="#FFF3E0"
        castShadow={shadows}
        shadow-mapSize={[shadowMap, shadowMap]}
        shadow-bias={-0.0005}
        shadow-normalBias={0.02}
        shadow-radius={4}
      />
      <directionalLight position={[-6, 4, -8]} intensity={0.6} color="#FFD6F0" />
    </>
  );
}

const CONFETTI_COLORS = ['#FF3EA5', '#FFD21F', '#7B2FF7', '#3FA9F5', '#2FE0A8', '#FF9A3D'];

/** Instanced confetti: a gentle ambient fall plus bursts (bursts are skipped with reduced motion). */
export class Confetti {
  mesh: THREE.InstancedMesh;
  private p: THREE.Vector3[] = [];
  private v: THREE.Vector3[] = [];
  private r: number[][] = [];
  private burstFlag: boolean[] = [];
  private dummy = new THREE.Object3D();
  private t = 0;
  ambient = true;

  constructor(private n: number, private area: { x: number; yTop: number; z0: number; z1: number; floor: number }, private reduced = false) {
    this.mesh = new THREE.InstancedMesh(new THREE.PlaneGeometry(0.07, 0.13), new THREE.MeshBasicMaterial({ side: THREE.DoubleSide }), n);
    this.mesh.frustumCulled = false;
    const col = CONFETTI_COLORS.map((c) => new THREE.Color(c));
    for (let i = 0; i < n; i++) {
      this.p.push(new THREE.Vector3());
      this.v.push(new THREE.Vector3());
      this.r.push([0, 0, 0]);
      this.burstFlag.push(false);
      this.seed(i);
      this.p[i].y = area.floor + Math.random() * (area.yTop - area.floor);
      this.mesh.setColorAt(i, col[i % col.length]);
    }
  }

  private seed(i: number, at?: THREE.Vector3) {
    const a = this.area;
    if (at) {
      this.p[i].copy(at).add(new THREE.Vector3((Math.random() - 0.5) * 0.6, 0.6, (Math.random() - 0.5) * 0.6));
      this.v[i].set((Math.random() - 0.5) * 5, 3 + Math.random() * 4, (Math.random() - 0.5) * 4);
      this.burstFlag[i] = true;
    } else {
      this.p[i].set((Math.random() - 0.5) * a.x * 2, a.yTop + Math.random() * 3, a.z0 + Math.random() * (a.z1 - a.z0));
      this.v[i].set(0, -(0.5 + Math.random() * 0.8), 0);
      this.burstFlag[i] = false;
    }
    this.r[i] = [Math.random() * 6, Math.random() * 6, 2 + Math.random() * 6];
  }

  burst(at: THREE.Vector3, count: number) {
    if (this.reduced) return;
    for (let q = 0; q < count; q++) this.seed(Math.floor(Math.random() * this.n), at);
  }

  update(dt: number) {
    this.t += dt;
    let any = false;
    for (let i = 0; i < this.n; i++) {
      const p = this.p[i], v = this.v[i];
      if (!this.ambient && !this.burstFlag[i]) {
        this.dummy.position.set(0, -999, 0);
        this.dummy.updateMatrix();
        this.mesh.setMatrixAt(i, this.dummy.matrix);
        continue;
      }
      any = true;
      if (this.burstFlag[i]) v.y -= 7 * dt;
      else p.x += Math.sin(this.t * 1.3 + i) * 0.006;
      p.addScaledVector(v, dt);
      if (p.y < this.area.floor) {
        if (this.burstFlag[i] || this.ambient) this.seed(i);
        if (!this.ambient) this.burstFlag[i] = false;
      }
      const r = this.r[i];
      r[0] += dt * r[2];
      r[1] += dt * r[2] * 0.7;
      this.dummy.position.copy(p);
      this.dummy.rotation.set(r[0], r[1], 0);
      this.dummy.updateMatrix();
      this.mesh.setMatrixAt(i, this.dummy.matrix);
    }
    this.mesh.visible = any;
    this.mesh.instanceMatrix.needsUpdate = true;
  }

  dispose() {
    this.mesh.geometry.dispose();
    (this.mesh.material as THREE.Material).dispose();
    this.mesh.dispose();
  }
}

const cloudGeo = new THREE.SphereGeometry(1, 24, 16);
export const cloudMaterial = () => new THREE.MeshStandardMaterial({ color: '#FFFFFF', roughness: 1 });

/** A puffy cloud: a few squashed spheres merged into one mesh (one draw call). */
export function makeCloud(mat: THREE.Material, size = 1) {
  const g = new THREE.Group();
  const n = 4 + Math.floor(Math.random() * 3);
  const parts: THREE.BufferGeometry[] = [];
  const m4 = new THREE.Matrix4();
  for (let j = 0; j < n; j++) {
    const s = (0.9 + Math.random() * 1.1) * size;
    m4.compose(
      new THREE.Vector3((j - n / 2) * 1.2 * size, Math.random() * 0.5 * size, Math.random() * 0.8 * size),
      new THREE.Quaternion(),
      new THREE.Vector3(s * 1.3, s, s),
    );
    parts.push(cloudGeo.clone().applyMatrix4(m4));
  }
  const merged = mergeGeometries(parts);
  parts.forEach((p) => p.dispose());
  g.add(new THREE.Mesh(merged, mat));
  return g;
}

/** Soft round contact shadow texture (for the blob shadows used when real-time shadows are off). */
let blobTex: THREE.CanvasTexture | null = null;
export function blobShadowTexture() {
  if (blobTex) return blobTex;
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const x = c.getContext('2d')!;
  const g = x.createRadialGradient(32, 32, 2, 32, 32, 32);
  g.addColorStop(0, 'rgba(42,18,80,0.55)');
  g.addColorStop(0.6, 'rgba(42,18,80,0.22)');
  g.addColorStop(1, 'rgba(42,18,80,0)');
  x.fillStyle = g;
  x.fillRect(0, 0, 64, 64);
  blobTex = new THREE.CanvasTexture(c);
  return blobTex;
}

/** Dispose every geometry / material / texture under an object (skips shared cloud geometry). */
export function disposeTree(root: THREE.Object3D, keep: Set<unknown> = new Set([cloudGeo, blobTex])) {
  const seen = new Set<unknown>();
  root.traverse((o) => {
    const m = o as THREE.Mesh;
    if (m.geometry && !keep.has(m.geometry) && !seen.has(m.geometry)) {
      seen.add(m.geometry);
      m.geometry.dispose();
    }
    const mats = (Array.isArray(m.material) ? m.material : m.material ? [m.material] : []) as THREE.Material[];
    for (const mat of mats) {
      if (seen.has(mat) || keep.has(mat)) continue;
      seen.add(mat);
      for (const v of Object.values(mat)) if (v instanceof THREE.Texture && !seen.has(v)) {
        seen.add(v);
        v.dispose();
      }
      mat.dispose();
    }
  });
}
