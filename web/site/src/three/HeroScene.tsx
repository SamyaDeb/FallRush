/**
 * `/` hero: Rushlings race toward the camera on a floating toy-block course that scrolls away under their feet.
 * Free roam: WASD / arrows / tap drive Bolt, Space jumps, clicking a Rushling makes it cheer. 12 s idle → back to the race.
 */
import { useEffect, useMemo, useRef, useState } from 'react';
import { Canvas, useFrame, useThree } from '@react-three/fiber';
import * as THREE from 'three';
import { PRESETS, type Preset } from '../lib/presets';
import { RushlingRig } from './Rushling';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { QUALITY } from './quality';
import { AdaptiveDpr, Confetti, Daylight, FirstFrames, cloudMaterial, disposeTree, makeCloud, prefersReducedMotion, useActive } from './common';
import { bandTexture, checkerTexture, stripeTexture, trackTexture } from './textures';
import { play } from '../lib/sfx';

export interface HeroSceneProps {
  /** The visitor's own Rushling. `reveal` = it drops in a capsule and pops out (right after joining). */
  you: { preset: Preset; frac: number; reveal?: boolean } | null;
  onRevealed?: () => void;
  /** Play mode (the Play free button): WASD / arrows / tap drive Bolt, Space jumps. Off = the race plays by itself. */
  playing: boolean;
  onReady?: () => void;
  /** A Rushling was bonked (clicked outside play mode): screen position for the DOM "BONK!" burst. */
  onBonk?: (x: number, y: number) => void;
}

export default function HeroScene(props: HeroSceneProps) {
  const host = useRef<HTMLDivElement>(null);
  const active = useActive(host);
  const [shown, setShown] = useState(false);
  return (
    <div
      ref={host}
      className={'stage' + (shown ? ' on' : '')}
      role="img"
      aria-label="3D scene: Bolt and other Rushlings racing down a floating toy-block course toward a checkered finish arch. Use W A S D or the arrow keys to move Bolt, Space to jump."
    >
      <Canvas
        shadows={QUALITY.shadows ? { type: THREE.PCFShadowMap } : false}
        dpr={QUALITY.dpr}
        frameloop={active ? 'always' : 'never'}
        camera={{ fov: 34, near: 0.1, far: 200, position: [-2, 2.7, 9.4] }}
        gl={{ antialias: QUALITY.antialias, alpha: true, powerPreference: 'default' }}
        onCreated={({ gl }) => {
          if (import.meta.env.DEV) (window as unknown as { __gl: unknown }).__gl = gl; // dev-only perf inspection
        }}
      >
        <fog attach="fog" args={['#D4EAF9', 20, 66]} />
        <FirstFrames
          onShown={() => {
            setShown(true);
            props.onReady?.();
          }}
        />
        <AdaptiveDpr min={1} max={QUALITY.dpr[1]} />
        <Daylight shadows={QUALITY.shadows} shadowMap={QUALITY.shadowMap} />
        <HeroWorld {...props} />
      </Canvas>
    </div>
  );
}

const TRACK_LEN = 138;
const BOUNDS = { x: 2.0, zMin: -15, zMax: 4.6 };
const HERO_SLOT: [number, number] = [1.0, 2.4];
const RUNNER_SLOTS: [number, number][] = [[-1.2, -1.6], [1.6, -4.2], [-0.4, -7.2], [1.0, -10.5], [-1.5, -13.5]];
const SPD = 4.2;

type Actor = {
  rig: RushlingRig;
  slot?: [number, number];
  drift: number;
  yaw: number;
  push: THREE.Vector3 | null;
  wt: { x: number; z: number } | null;
  wait: number;
  marker?: THREE.Group;
  /** bonk fling (ballistic; lands, flops over, gets up and runs back to its slot) */
  fly?: { v: THREE.Vector3; spin: THREE.Vector3; bounces: number } | null;
};

const angLerp = (a: number, b: number, f: number) => {
  let d = b - a;
  while (d > Math.PI) d -= Math.PI * 2;
  while (d < -Math.PI) d += Math.PI * 2;
  return a + d * f;
};
const clampP = (p: { x: number; z: number }) => {
  p.x = Math.max(-BOUNDS.x, Math.min(BOUNDS.x, p.x));
  p.z = Math.max(BOUNDS.zMin, Math.min(BOUNDS.zMax, p.z));
};
const slotFor = (f: number) => new THREE.Vector3(0.2, 0, 1.2 - f * 14);

function buildWorld() {
  const mat = (c: THREE.ColorRepresentation, r = 0.38) => new THREE.MeshStandardMaterial({ color: c, roughness: r, metalness: 0 });
  const world = new THREE.Group();
  const trackTex = trackTexture(23);
  const bandTex = bandTexture(98);

  // the course
  const top = new THREE.MeshStandardMaterial({ map: trackTex, roughness: 0.55 });
  const side = mat(0x9879db, 0.6);
  const track = new THREE.Mesh(new THREE.BoxGeometry(4.6, 0.7, TRACK_LEN), [side, side, top, side, side, side]);
  track.position.set(0, -0.35, 60 - TRACK_LEN / 2);
  track.receiveShadow = true;
  world.add(track);
  const bandM = new THREE.MeshStandardMaterial({ map: bandTex, roughness: 0.35 });
  for (const s of [-1, 1]) {
    const rim = new THREE.Mesh(new THREE.CylinderGeometry(0.2, 0.2, TRACK_LEN, 16), bandM);
    rim.rotation.x = Math.PI / 2;
    rim.position.set(s * 2.38, 0.06, 60 - TRACK_LEN / 2);
    rim.receiveShadow = true;
    world.add(rim);
  }

  // finish arch: mint/white checker, posts, a spinning crown (our own five-point shape)
  const arch = new THREE.Group();
  arch.position.set(0, 0, -54);
  world.add(arch);
  const archM = new THREE.MeshStandardMaterial({ map: checkerTexture(10, 1), roughness: 0.4 });
  arch.add(new THREE.Mesh(new THREE.TorusGeometry(3.3, 0.5, 18, 48, Math.PI), archM));
  const crown = new THREE.Group();
  crown.position.y = 4.5;
  arch.add(crown);
  const gold = mat(0xffd21f, 0.22);
  const ring = new THREE.Mesh(new THREE.CylinderGeometry(0.62, 0.55, 0.42, 30, 1, true), gold);
  (ring.material as THREE.MeshStandardMaterial).side = THREE.DoubleSide;
  crown.add(ring);
  for (let i = 0; i < 5; i++) {
    const a = (i / 5) * Math.PI * 2;
    const spike = new THREE.Mesh(new THREE.ConeGeometry(0.18, 0.5, 12), gold);
    spike.position.set(Math.sin(a) * 0.58, 0.42, Math.cos(a) * 0.58);
    crown.add(spike);
    const gem = new THREE.Mesh(new THREE.SphereGeometry(0.09, 12, 8), mat(i % 2 ? 0xff3ea5 : 0x7b2ff7, 0.15));
    gem.position.set(Math.sin(a) * 0.6, 0.7, Math.cos(a) * 0.6);
    crown.add(gem);
  }

  // floating islands on pillars, spinners, flags (move with the course)
  const pastel = [
    [0xf9d1e2, 0xdb79a4, 0xff6fae], [0xf9edc2, 0xdbc679, 0xffd43b], [0xd6edf9, 0x79badb, 0x4cc2ff],
    [0xe0f9d1, 0x9edb79, 0x86e04e], [0xded1f9, 0x9879db, 0xa77dff], [0xf9e4d1, 0xdba879, 0xff9a3d],
  ];
  const decor: { o: THREE.Object3D; y: number; ph: number; spinner?: THREE.Object3D; flag?: THREE.Object3D; cloud?: boolean }[] = [];
  const flagM = new THREE.MeshStandardMaterial({ color: 0xff3ea5, roughness: 0.5, side: THREE.DoubleSide });
  const flagShape = new THREE.Shape();
  flagShape.moveTo(0, 0);
  flagShape.lineTo(0.9, -0.25);
  flagShape.lineTo(0, -0.5);
  flagShape.closePath();
  const flagGeo = new THREE.ShapeGeometry(flagShape);
  const stripes = new THREE.MeshStandardMaterial({ map: stripeTexture(2), roughness: 0.3 });
  const hot = mat(0xff2d8a);
  // one vertex-coloured material for every island body (body + lip + pillar merged: 1 draw call per island)
  const islandM = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.55 });
  const pillarCol = new THREE.Color(0x8f6fd6);
  const N = QUALITY.islands;
  for (let i = 0; i < N; i++) {
    const pal = pastel[i % pastel.length];
    const sd = i % 2 ? 1 : -1;
    const r = 1.1 + Math.random() * 1.3;
    const isl = new THREE.Group();
    const body = paintGroups(new THREE.CylinderGeometry(r, r * 0.9, 0.7, 28), [new THREE.Color(pal[1]), new THREE.Color(pal[0]), new THREE.Color(pal[1])]);
    const lip = paint(new THREE.TorusGeometry(r, 0.14, 8, 28).rotateX(Math.PI / 2).translate(0, 0.35, 0), new THREE.Color(pal[2]));
    const pil = paint(new THREE.CylinderGeometry(r * 0.35, r * 0.2, 7, 12).translate(0, -3.8, 0), pillarCol);
    const merged = mergeGeometries([body, lip, pil]);
    [body, lip, pil].forEach((g) => g.dispose());
    isl.add(new THREE.Mesh(merged, islandM));
    let spinner: THREE.Object3D | undefined, flag: THREE.Object3D | undefined;
    if (i % 3 === 0) {
      spinner = new THREE.Mesh(new THREE.CylinderGeometry(r * 0.72, r * 0.72, 0.14, 40), [hot, stripes, hot]);
      spinner.position.y = 0.45;
      isl.add(spinner);
      const hub = new THREE.Mesh(new THREE.SphereGeometry(0.22, 16, 12), gold);
      hub.position.y = 0.55;
      isl.add(hub);
    } else if (i % 3 === 1) {
      const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.04, 1.8, 8), mat(0xffffff));
      pole.position.set(0, 1.25, 0);
      isl.add(pole);
      flag = new THREE.Mesh(flagGeo, flagM);
      flag.position.set(0, 2.1, 0);
      isl.add(flag);
    }
    const y = -1.6 + Math.random() * 3.2;
    isl.position.set(sd * (4.6 + Math.random() * 5.5), y, 70 - (i / N) * 150);
    world.add(isl);
    decor.push({ o: isl, y, ph: Math.random() * 6, spinner, flag });
  }

  // clouds
  const cm = cloudMaterial();
  for (let i = 0; i < QUALITY.clouds; i++) {
    const cl = makeCloud(cm);
    const y = i % 3 === 0 ? 7 + Math.random() * 4 : -4 - Math.random() * 2;
    cl.position.set((Math.random() - 0.5) * 34, y, 70 - Math.random() * 150);
    world.add(cl);
    decor.push({ o: cl, y, ph: Math.random() * 6, cloud: true });
  }

  // swinging striped foam mallet
  const mallet = new THREE.Group();
  mallet.position.set(5.2, 6.4, -9);
  world.add(mallet);
  const handle = new THREE.Mesh(new THREE.CylinderGeometry(0.12, 0.12, 3.6, 12), gold);
  handle.position.y = -1.8;
  mallet.add(handle);
  const head = new THREE.Mesh(new THREE.CylinderGeometry(0.8, 0.8, 2.0, 28), [new THREE.MeshStandardMaterial({ map: stripeTexture(3), roughness: 0.3 }), hot, hot]);
  head.rotation.z = Math.PI / 2;
  head.position.y = -3.8;
  head.castShadow = true;
  mallet.add(head);

  return { world, trackTex, bandTex, decor, mallet, crown };
}

type Capsule = { g: THREE.Group; topG: THREE.Group; botG: THREE.Group; band: THREE.Mesh; at: THREE.Vector3; t: number; pt: number; popped: boolean; r0: number };

function makeCapsule(p: Preset): Capsule {
  const r0 = 0.62;
  const g = new THREE.Group(), topG = new THREE.Group(), botG = new THREE.Group();
  const bottom = new THREE.Mesh(new THREE.SphereGeometry(r0, 36, 16, 0, Math.PI * 2, Math.PI / 2, Math.PI / 2), new THREE.MeshStandardMaterial({ color: p.shell, roughness: 0.22 }));
  const top = new THREE.Mesh(
    new THREE.SphereGeometry(r0, 36, 16, 0, Math.PI * 2, 0, Math.PI / 2),
    new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.06, transparent: true, opacity: 0.62 }),
  );
  const band = new THREE.Mesh(new THREE.TorusGeometry(r0, 0.05, 10, 44), new THREE.MeshStandardMaterial({ color: 0x2a1250, roughness: 0.5 }));
  band.rotation.x = Math.PI / 2;
  bottom.castShadow = top.castShadow = true;
  topG.add(top);
  botG.add(bottom);
  g.add(topG, botG, band);
  const at = new THREE.Vector3(0.2, 0, 3.4);
  g.position.set(at.x, 9, at.z);
  return { g, topG, botG, band, at, t: 0, pt: 0, popped: false, r0 };
}

/** Add a flat vertex colour to a geometry (all its vertices). */
function paint(g: THREE.BufferGeometry, c: THREE.Color) {
  const n = g.attributes.position.count;
  const arr = new Float32Array(n * 3);
  for (let i = 0; i < n; i++) c.toArray(arr, i * 3);
  g.setAttribute('color', new THREE.BufferAttribute(arr, 3));
  g.clearGroups();
  return g;
}

/** Colour each geometry group (e.g. a cylinder's side / top / bottom) differently, then drop the groups. */
function paintGroups(g: THREE.BufferGeometry, cols: THREE.Color[]) {
  const n = g.attributes.position.count;
  const arr = new Float32Array(n * 3);
  const idx = g.index!;
  g.groups.forEach((gr, k) => {
    const c = cols[k] ?? cols[0];
    for (let i = gr.start; i < gr.start + gr.count; i++) c.toArray(arr, idx.getX(i) * 3);
  });
  g.setAttribute('color', new THREE.BufferAttribute(arr, 3));
  g.clearGroups();
  return g;
}

/** Ballistic bonk flight: tumble through the air, one bounce, flop over (rig.hit), then back to normal. */
function flyStep(a: Actor, dt: number) {
  const f = a.fly;
  if (!f) return;
  const g = a.rig.group;
  g.position.addScaledVector(f.v, dt);
  f.v.y -= 22 * dt;
  g.rotation.x += f.spin.x * dt;
  g.rotation.z += f.spin.z * dt;
  a.yaw += f.spin.y * dt;
  if (g.position.y < -14) {
    // fell off the course: drop back in from the sky
    g.position.set(Math.max(-1.6, Math.min(1.6, g.position.x * 0.3)), 10, g.position.z);
    g.rotation.x = g.rotation.z = 0;
    f.v.set(0, -3, 0);
    f.spin.set(0, 0, 0);
    f.bounces = 1;
    return;
  }
  if (g.position.y <= 0 && f.v.y < 0 && Math.abs(g.position.x) < 2.3 && g.position.y > -0.6) {
    g.position.y = 0;
    if (f.bounces === 0 && f.v.y < -5) {
      f.bounces = 1;
      f.v.y = -f.v.y * 0.32;
      f.v.x *= 0.5;
      f.v.z *= 0.5;
      f.spin.multiplyScalar(0.4);
      play('land', { vol: 0.6 });
    } else {
      g.rotation.x = 0;
      g.rotation.z = 0;
      a.fly = null;
      a.rig.hit(1.1);
      play('land', { vol: 0.7 });
    }
  }
}

function makeMarker() {
  const m = new THREE.Group();
  const yellow = new THREE.MeshStandardMaterial({ color: 0xffd21f, roughness: 0.25, emissive: 0x5a4300 });
  const cone = new THREE.Mesh(new THREE.ConeGeometry(0.13, 0.26, 4), yellow);
  cone.rotation.x = Math.PI;
  cone.position.y = 1.55;
  m.add(cone);
  const ring = new THREE.Mesh(new THREE.TorusGeometry(0.42, 0.03, 8, 36), new THREE.MeshBasicMaterial({ color: 0xffd21f }));
  ring.rotation.x = Math.PI / 2;
  ring.position.y = 0.02;
  m.add(ring);
  return m;
}

function HeroWorld({ you, playing, onRevealed, onBonk }: HeroSceneProps) {
  const { camera, gl, scene, size } = useThree();
  const cam = camera as THREE.PerspectiveCamera;
  const reduce = useMemo(prefersReducedMotion, []);
  const playingRef = useRef(playing);
  playingRef.current = playing;
  const onRev = useRef(onRevealed);
  onRev.current = onRevealed;
  const onBonkRef = useRef(onBonk);
  onBonkRef.current = onBonk;
  const capRef = useRef<Capsule | null>(null);
  const popRef = useRef(1);

  const W = useMemo(() => buildWorld(), []);
  const confetti = useMemo(() => new Confetti(QUALITY.confetti, { x: 9, yTop: 9, z0: -12, z1: 4, floor: -2 }, reduce), [reduce]);

  // cast: Bolt leads, five others behind
  const cast = useMemo(() => {
    const mk = (p: Preset, scale: number, slot?: [number, number]): Actor => {
      const rig = new RushlingRig(p, { shadows: QUALITY.shadows, blob: !QUALITY.shadows });
      rig.timeScale = reduce ? 0.5 : 1;
      rig.group.scale.setScalar(scale);
      if (slot) rig.group.position.set(slot[0], 0, slot[1]);
      return { rig, slot, drift: Math.random() * 6, yaw: 0, push: null, wt: null, wait: 0 };
    };
    const hero = mk(PRESETS[0], 1.45, HERO_SLOT);
    const runners = [1, 2, 3, 4, 5].map((pi, k) => mk(PRESETS[pi], 1.25, RUNNER_SLOTS[k]));
    return { hero, runners };
  }, [reduce]);

  const youRef = useRef<Actor | null>(null);
  const youTarget = useRef(new THREE.Vector3());
  const pendingRef = useRef<Actor | null>(null);

  useEffect(() => {
    scene.add(W.world, confetti.mesh, cast.hero.rig.group, ...cast.runners.map((r) => r.rig.group));
    cast.hero.rig.cheer(1.6);
    return () => {
      scene.remove(W.world, confetti.mesh);
      disposeTree(W.world);
      confetti.dispose();
      cast.hero.rig.dispose();
      cast.runners.forEach((r) => r.rig.dispose());
    };
  }, [scene, W, confetti, cast]);

  // your Rushling (joined visitors), with a bobbing yellow marker
  const youKey = you ? you.preset.name : '';
  useEffect(() => {
    if (!you) return;
    const rig = new RushlingRig(you.preset, { shadows: QUALITY.shadows, blob: !QUALITY.shadows });
    rig.timeScale = reduce ? 0.5 : 1;
    rig.group.scale.setScalar(1.25);
    const marker = makeMarker();
    rig.group.add(marker);
    youTarget.current.copy(slotFor(you.frac));
    const actor: Actor = { rig, drift: Math.random() * 6, yaw: 0, push: null, wt: null, wait: 0, marker };
    if (you.reveal) {
      // capsule drops onto the course, wobbles, cracks open, the Rushling pops out (design: "Cracking open your capsule…")
      const cap = makeCapsule(you.preset);
      scene.add(cap.g);
      capRef.current = cap;
      rig.group.position.copy(cap.at);
      rig.group.scale.setScalar(0.001);
      popRef.current = 0;
    } else {
      rig.group.position.copy(youTarget.current);
      scene.add(rig.group);
      youRef.current = actor;
    }
    const pending = actor;
    pendingRef.current = pending;
    return () => {
      if (capRef.current) {
        scene.remove(capRef.current.g);
        disposeTree(capRef.current.g);
        capRef.current = null;
      }
      pendingRef.current = null;
      scene.remove(rig.group);
      rig.group.remove(marker);
      disposeTree(marker);
      rig.dispose();
      youRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [youKey, scene, reduce]);
  useEffect(() => {
    if (you) youTarget.current.copy(slotFor(you.frac));
  }, [you?.frac]); // eslint-disable-line react-hooks/exhaustive-deps

  // input
  const S = useRef({
    keys: {} as Record<string, boolean>,
    vel: new THREE.Vector3(),
    tap: { on: false, x: 0, z: 0 },
    play: { on: false, idle: 0, mix: 0 },
    mouse: { x: 0, y: 0, sx: 0, sy: 0 },
    t: 0,
    autoJ: 2.5,
  });

  useEffect(() => {
    const s = S.current;
    // input only drives Bolt in play mode
    const enterPlay = () => {
      s.play.idle = 0;
    };
    const jumpHero = () => {
      enterPlay();
      cast.hero.rig.jump();
    };
    const onMove = (e: PointerEvent) => {
      s.mouse.x = (e.clientX / window.innerWidth) * 2 - 1;
      s.mouse.y = (e.clientY / window.innerHeight) * 2 - 1;
    };
    const ray = new THREE.Raycaster();
    const ndc = new THREE.Vector2();
    const ground = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0);
    const hitP = new THREE.Vector3();
    const onDown = (e: PointerEvent) => {
      const b = gl.domElement.getBoundingClientRect();
      ndc.set(((e.clientX - b.left) / b.width) * 2 - 1, -((e.clientY - b.top) / b.height) * 2 + 1);
      ray.setFromCamera(ndc, cam);
      const actors = [cast.hero, ...cast.runners, ...(youRef.current ? [youRef.current] : [])];
      if (!playingRef.current) {
        // the race: click a Rushling to BONK it into the air
        const hit = ray.intersectObjects(actors.map((a) => a.rig.group), true)[0];
        if (!hit) return;
        const a = actors.find((q) => q.rig.id === hit.object.userData.rushling);
        if (!a || a.fly) return;
        const side = a.rig.group.position.x < -0.2 ? -1 : a.rig.group.position.x > 0.2 ? 1 : Math.random() < 0.5 ? -1 : 1;
        a.fly = {
          v: new THREE.Vector3(side * (1.5 + Math.random() * 2.5), reduce ? 5 : 8.5 + Math.random() * 3, -(1 + Math.random() * 3)),
          spin: new THREE.Vector3(-(6 + Math.random() * 6), (Math.random() - 0.5) * 8, side * (4 + Math.random() * 5)),
          bounces: 0,
        };
        confetti.burst(hit.point.clone(), 40);
        onBonkRef.current?.(e.clientX, e.clientY);
        return;
      }
      const hits = ray.intersectObjects(actors.map((a) => a.rig.group), true);
      if (hits.length) {
        const id = hits[0].object.userData.rushling;
        const a = actors.find((q) => q.rig.id === id);
        if (a === cast.hero) return jumpHero();
        if (a && !a.rig.knocked) {
          a.rig.cheer(1.4);
          confetti.burst(a.rig.group.getWorldPosition(new THREE.Vector3()).setY(1), 50);
        }
        return;
      }
      if (ray.ray.intersectPlane(ground, hitP) && Math.abs(hitP.x) < 2.6 && hitP.z > BOUNDS.zMin - 2 && hitP.z < BOUNDS.zMax + 3) {
        clampP(hitP);
        s.tap = { on: true, x: hitP.x, z: hitP.z };
        enterPlay();
      } else jumpHero();
    };
    const MOVE: Record<string, 1> = { KeyW: 1, KeyA: 1, KeyS: 1, KeyD: 1, ArrowUp: 1, ArrowDown: 1, ArrowLeft: 1, ArrowRight: 1 };
    const typing = (el: EventTarget | null) => {
      const n = el as HTMLElement | null;
      const tag = n?.tagName;
      return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || !!n?.isContentEditable;
    };
    const onKey = (e: KeyboardEvent) => {
      if (!playingRef.current) return;
      if (e.metaKey || e.ctrlKey || e.altKey || typing(e.target)) return;
      const tag = (e.target as HTMLElement | null)?.tagName;
      if (MOVE[e.code]) {
        s.keys[e.code] = true;
        e.preventDefault();
        enterPlay();
      } else if (e.code === 'Space' && tag !== 'BUTTON' && tag !== 'A') {
        e.preventDefault();
        if (!e.repeat) jumpHero();
      }
    };
    const onKeyUp = (e: KeyboardEvent) => (s.keys[e.code] = false);
    const onBlur = () => {
      for (const k in s.keys) s.keys[k] = false;
    };
    window.addEventListener('pointermove', onMove);
    gl.domElement.addEventListener('pointerdown', onDown);
    window.addEventListener('keydown', onKey);
    window.addEventListener('keyup', onKeyUp);
    window.addEventListener('blur', onBlur);
    return () => {
      window.removeEventListener('pointermove', onMove);
      gl.domElement.removeEventListener('pointerdown', onDown);
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('keyup', onKeyUp);
      window.removeEventListener('blur', onBlur);
    };
  }, [gl, cam, cast, confetti, reduce]);

  // Play free on / off
  useEffect(() => {
    const s = S.current;
    s.play.on = playing;
    s.tap.on = false;
    for (const k in s.keys) s.keys[k] = false;
    if (playing) cast.hero.rig.cheer(1);
  }, [playing, cast]);

  // camera framing (wide: characters on the right of the overlay; portrait: pulled back, characters low)
  const base = useMemo(() => {
    const wide = size.width / size.height > 1.15;
    return wide
      ? { fov: 34, pos: new THREE.Vector3(-2.0, 2.7, 9.4), look: new THREE.Vector3(-1.9, 1.15, -4) }
      : { fov: 50, pos: new THREE.Vector3(0.4, 4.4, 14), look: new THREE.Vector3(0.4, 3.4, -4) };
  }, [size.width, size.height]);
  useEffect(() => {
    cam.fov = base.fov;
    cam.updateProjectionMatrix();
  }, [cam, base]);

  const camLook = useMemo(() => new THREE.Vector3(), []);
  const tmp = useMemo(() => new THREE.Vector3(), []);

  useFrame((_, dtRaw) => {
    const s = S.current;
    const dt = Math.min(0.05, dtRaw);
    const k = reduce ? 0.3 : 1;
    s.t += dt;
    const t = s.t;
    const hero = cast.hero;
    const hp = hero.rig.group.position;
    const yourActor = youRef.current;

    // input → hero velocity
    let ix = (s.keys.KeyD || s.keys.ArrowRight ? 1 : 0) - (s.keys.KeyA || s.keys.ArrowLeft ? 1 : 0);
    let iz = (s.keys.KeyS || s.keys.ArrowDown ? 1 : 0) - (s.keys.KeyW || s.keys.ArrowUp ? 1 : 0);
    if (ix || iz) s.tap.on = false;
    else if (s.tap.on) {
      const dx = s.tap.x - hp.x, dz = s.tap.z - hp.z, d = Math.hypot(dx, dz);
      if (d < 0.15) s.tap.on = false;
      else {
        const f = Math.min(1, d / 0.6);
        ix = (dx / d) * f;
        iz = (dz / d) * f;
        s.play.idle = 0;
      }
    }
    if (ix || iz) s.play.idle = 0;
    const len = Math.hypot(ix, iz);
    if (len > 1) {
      ix /= len;
      iz /= len;
    }
    s.play.mix += ((s.play.on ? 1 : 0) - s.play.mix) * Math.min(1, dt * 2);
    const mix = s.play.mix;

    // the course scrolls under the racers; it eases to a stop while the visitor drives
    const v = 5.5 * k * (1 - mix);
    W.trackTex.offset.y -= (dt * v) / (TRACK_LEN / 23);
    W.bandTex.offset.y += (dt * v) / (TRACK_LEN / 98);
    for (const d of W.decor) {
      d.o.position.z -= dt * v * (d.cloud ? 0.6 : 1);
      if (d.o.position.z < -80) d.o.position.z += 150;
      d.o.position.y = d.y + Math.sin(t * 0.8 + d.ph) * (d.cloud ? 0.15 : 0.25);
      if (d.spinner) d.spinner.rotation.y += dt * 1.6 * Math.max(0.2, k);
      if (d.flag) d.flag.rotation.y = Math.sin(t * 3 + d.ph) * 0.35;
    }
    W.mallet.rotation.z = Math.sin(t * 1.4 * Math.max(0.3, k)) * 0.75;
    W.crown.rotation.y += dt * 0.8;

    // capsule reveal
    const cap = capRef.current;
    if (cap) {
      cap.t += dt;
      const pend = pendingRef.current;
      if (cap.t < 0.75) {
        const u = cap.t / 0.75;
        cap.g.position.y = 9 - (9 - cap.r0) * u * u;
      } else if (!cap.popped) {
        cap.g.position.y = cap.r0;
        const k2 = cap.t - 0.75;
        const sq = k2 < 0.25 ? 1 - 0.2 * Math.sin((k2 / 0.25) * Math.PI) : 1;
        cap.g.scale.set(1 / Math.sqrt(sq), sq, 1 / Math.sqrt(sq));
        cap.g.rotation.z = k2 > 0.25 ? Math.sin(k2 * 42) * 0.14 * Math.min(1, (k2 - 0.25) / 0.6) : 0;
        if (k2 > 1.15 && pend) {
          cap.popped = true;
          cap.g.rotation.z = 0;
          cap.g.scale.set(1, 1, 1);
          scene.add(pend.rig.group);
          youRef.current = pend;
          pend.rig.cheer(2.4);
          confetti.burst(cap.at.clone().setY(0.8), 90);
          cast.hero.rig.cheer(1.2);
          onRev.current?.();
        }
      } else {
        cap.pt += dt;
        cap.topG.position.y += dt * 7;
        cap.topG.position.z -= dt * 2;
        cap.topG.rotation.x -= dt * 6;
        cap.botG.position.x += dt * 3;
        cap.botG.position.y -= dt * 3;
        cap.botG.rotation.z -= dt * 5;
        const shrink = Math.max(0.001, 1 - cap.pt / 1.1);
        cap.topG.scale.setScalar(shrink);
        cap.botG.scale.setScalar(shrink);
        cap.band.visible = cap.pt < 0.25;
        if (cap.pt > 1.2) {
          scene.remove(cap.g);
          disposeTree(cap.g);
          capRef.current = null;
        }
      }
    }
    if (popRef.current < 1 && youRef.current) {
      popRef.current = Math.min(1, popRef.current + dt / 0.5);
      const u = popRef.current, c1 = 2.2;
      const e = 1 + (c1 + 1) * Math.pow(u - 1, 3) + c1 * Math.pow(u - 1, 2);
      youRef.current.rig.group.scale.setScalar(Math.max(0.001, 1.25 * e));
    }

    // hero
    const vel = s.vel;
    vel.x += (ix * SPD - vel.x) * Math.min(1, dt * 10);
    vel.z += (iz * SPD - vel.z) * Math.min(1, dt * 10);
    if (hero.rig.knocked) vel.set(0, 0, 0);
    if (hero.fly) {
      vel.set(0, 0, 0);
    } else if (s.play.on) {
      hp.x += vel.x * dt;
      hp.z += vel.z * dt;
      clampP(hp);
    } else {
      vel.set(0, 0, 0);
      hp.x += (HERO_SLOT[0] - hp.x) * Math.min(1, dt * 2);
      hp.z += (HERO_SLOT[1] - hp.z) * Math.min(1, dt * 2);
    }
    const spd = Math.hypot(vel.x, vel.z);
    if (spd > 0.3) hero.yaw = angLerp(hero.yaw, Math.atan2(vel.x, vel.z), Math.min(1, dt * 12));
    else if (!s.play.on) hero.yaw = angLerp(hero.yaw, s.mouse.sx * 0.5 - 0.12, Math.min(1, dt * 4));
    hero.rig.group.rotation.y = hero.yaw;
    flyStep(hero, dt);
    hero.rig.update(dt, { run: hero.fly ? 0 : s.play.on ? spd / SPD : k });

    // the others
    const npcs = [...cast.runners, ...(yourActor ? [yourActor] : [])];
    for (const r of npcs) {
      const p = r.rig.group.position;
      if (r.push) {
        p.x += r.push.x * dt;
        p.z += r.push.z * dt;
        r.push.multiplyScalar(Math.max(0, 1 - dt * 3));
        if (r.push.lengthSq() < 0.01) r.push = null;
      }
      let run = k;
      if (r.fly) {
        run = 0;
      } else if (mix > 0.02) {
        if (!r.wt) {
          r.wt = { x: (Math.random() * 2 - 1) * BOUNDS.x, z: -12 + Math.random() * 14 };
          r.wait = 0.4 + Math.random() * 1.6;
        }
        const dx = r.wt.x - p.x, dz = r.wt.z - p.z, d = Math.hypot(dx, dz);
        if (r.rig.knocked) run = 0;
        else if (d < 0.2) {
          run = 0;
          r.wait -= dt;
          if (r.wait <= 0) r.wt = null;
        } else {
          p.x += (dx / d) * 1.7 * dt;
          p.z += (dz / d) * 1.7 * dt;
          run = 0.45;
          r.yaw = angLerp(r.yaw, Math.atan2(dx, dz), Math.min(1, dt * 6));
        }
        clampP(p);
      } else {
        r.wt = null;
        let tx: number, tz: number;
        if (r === yourActor) {
          tx = youTarget.current.x + Math.sin(t * 0.6 + r.drift) * 0.25;
          tz = youTarget.current.z + Math.sin(t * 0.4 + r.drift) * 0.5;
        } else {
          tx = r.slot![0] + Math.sin(t * 0.7 + r.drift) * 0.35;
          tz = r.slot![1] + Math.sin(t * 0.45 + r.drift * 2) * 0.9;
        }
        p.x += (tx - p.x) * Math.min(1, dt * 1.6);
        p.z += (tz - p.z) * Math.min(1, dt * 1.2);
        r.yaw = angLerp(r.yaw, Math.sin(t * 0.7 + r.drift) * 0.15, Math.min(1, dt * 4));
      }
      // bumping into Bolt: push apart; at speed, knock over
      const bx = p.x - hp.x, bz = p.z - hp.z, bd = Math.hypot(bx, bz), minD = 0.85;
      if (bd < minD && bd > 1e-4) {
        const nx = bx / bd, nz = bz / bd;
        p.x = hp.x + nx * minD;
        p.z = hp.z + nz * minD;
        if (s.play.on && spd > 2.2 && r.rig.hit()) {
          r.push = new THREE.Vector3(nx * 4.5, 0, nz * 4.5);
          confetti.burst(tmp.set(p.x, 0.8, p.z), 18);
        }
      }
      if (r.marker) {
        r.marker.position.y = reduce ? 0 : Math.sin(t * 4) * 0.06;
        r.marker.rotation.y += dt * 2;
      }
      r.rig.group.rotation.y = r.yaw;
      flyStep(r, dt);
      r.rig.update(dt, { run });
    }

    // camera: mouse parallax in the race, soft follow while driving
    s.mouse.sx += (s.mouse.x - s.mouse.sx) * 0.05;
    s.mouse.sy += (s.mouse.y - s.mouse.sy) * 0.05;
    const fx = (hp.x - HERO_SLOT[0]) * 0.55 * mix, fz = (hp.z - HERO_SLOT[1]) * 0.6 * mix;
    const par = reduce ? 0.3 : 1;
    cam.position.set(base.pos.x + s.mouse.sx * 0.9 * (1 - mix) * par + fx, base.pos.y - s.mouse.sy * 0.45 * (1 - mix) * par, base.pos.z + fz);
    camLook.set(base.look.x + fx, base.look.y, base.look.z + fz);
    cam.lookAt(camLook);

    s.autoJ -= dt;
    if (s.autoJ <= 0 && !reduce && !s.play.on) {
      cast.runners[Math.floor(Math.random() * cast.runners.length)].rig.jump();
      s.autoJ = 1.6 + Math.random() * 2.4;
    }
    confetti.update(dt);
  });

  return null;
}
