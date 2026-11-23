/**
 * The scroll story's 3D stage: one sticky canvas, one world laid out along a flight path, one cast of 12 Rushlings
 * carried from the sky drop through the three rounds to the on-chain Final and the crown. The camera rides each
 * chapter's rig and swoops through clouds between them. All of it reads the shared scroll progress.
 */
import { use, useEffect, useMemo, useRef, useState } from 'react';
import { Canvas, useFrame, useThree } from '@react-three/fiber';
import { Bloom, ChromaticAberration, EffectComposer, Vignette } from '@react-three/postprocessing';
import { BlendFunction, type ChromaticAberrationEffect } from 'postprocessing';
import * as THREE from 'three';
import { RushlingRig } from './Rushling';
import { QUALITY } from './quality';
import { AdaptiveDpr, FirstFrames, prefersReducedMotion, useActive } from './common';
import { loadProps, type PropName } from './props';
import { CAST, makeSky } from './story/kit';
import { crownChapter, fallChapter, gooChapter, lidChapter, runChapter, starChapter, type CamPose, type CastMember, type Chapter, type Ctx } from './story/chapters';
import { CHAPTERS, ZONE, chapterAt, localP } from './story/timeline';
import { ease } from '../lib/scroll';
import type { Progress } from '../lib/scroll';

const PROP_NAMES: PropName[] = [
  'start_arch', 'bunting', 'gumball', 'mallet_head', 'spinner_hub', 'spinner_cap', 'coin_slot', 'inflatable_tower', 'goo_blob',
  'prize_star', 'claw', 'windup_key', 'capsule_machine', 'bleacher_block', 'pennant', 'confetti_cannon', 'ribbon_banner',
];
let propsPromise: Promise<Record<PropName, THREE.BufferGeometry>> | null = null;
const getProps = () => (propsPromise ??= loadProps(PROP_NAMES));

export interface StoryCanvasProps {
  prog: React.RefObject<Progress>;
  /** 0..1 on-chain night, for the page's DOM tint */
  onDigital?: (d: number) => void;
  onReady?: () => void;
}

export default function StoryCanvas(props: StoryCanvasProps) {
  const host = useRef<HTMLDivElement>(null);
  const active = useActive(host);
  const [shown, setShown] = useState(false);
  const fx = useRef({ ca: 0 });
  const effects = QUALITY.tier === 'high';
  return (
    <div ref={host} className={'story-stage' + (shown ? ' on' : '')} aria-hidden="true">
      <Canvas
        shadows={QUALITY.shadows ? { type: THREE.PCFShadowMap } : false}
        dpr={QUALITY.dpr}
        frameloop={active ? 'always' : 'never'}
        camera={{ fov: 42, near: 0.1, far: 1000, position: [7, 54, 12] }}
        gl={{ antialias: !effects && QUALITY.antialias, powerPreference: 'high-performance', stencil: false }}
      >
        <FirstFrames
          onShown={() => {
            setShown(true);
            props.onReady?.();
          }}
        />
        <AdaptiveDpr min={1} max={QUALITY.dpr[1]} />
        <StoryWorld {...props} fx={fx} />
        {effects && <Effects fx={fx} />}
      </Canvas>
    </div>
  );
}

function Effects({ fx }: { fx: React.RefObject<{ ca: number }> }) {
  const ca = useRef<ChromaticAberrationEffect>(null);
  const off = useMemo(() => new THREE.Vector2(0, 0), []);
  useFrame((_, dt) => {
    const f = fx.current;
    f.ca = Math.max(0, f.ca - dt * 2.2);
    if (ca.current) ca.current.offset.set(f.ca * 0.006, f.ca * 0.004);
  });
  return (
    <EffectComposer multisampling={4} enableNormalPass={false}>
      <Bloom mipmapBlur intensity={0.85} luminanceThreshold={0.9} luminanceSmoothing={0.12} radius={0.7} />
      <ChromaticAberration ref={ca} offset={off} radialModulation={false} modulationOffset={0} blendFunction={BlendFunction.NORMAL} />
      <Vignette offset={0.32} darkness={0.42} />
    </EffectComposer>
  );
}

function StoryWorld({ prog, onDigital, fx }: StoryCanvasProps & { fx: React.RefObject<{ ca: number }> }) {
  const props = use(getProps());
  const { scene, camera, size } = useThree();
  const cam = camera as THREE.PerspectiveCamera;
  const reduced = useMemo(prefersReducedMotion, []);
  const onDig = useRef(onDigital);
  onDig.current = onDigital;

  const W = useMemo(() => {
    const chapters: Chapter[] = [
      fallChapter(),
      runChapter(props),
      gooChapter(props),
      starChapter(props),
      lidChapter(props),
      crownChapter(props, reduced),
    ];
    const sky = makeSky();
    const cast: CastMember[] = CAST.map((p, i) => {
      const rig = new RushlingRig(p, { shadows: QUALITY.shadows, blob: !QUALITY.shadows });
      rig.timeScale = reduced ? 0.5 : 1;
      const scale = i === 0 ? 1.35 : 1.18;
      rig.group.scale.setScalar(scale);
      return { rig, pos: new THREE.Vector3(), yaw: 0, rx: 0, rz: 0, run: 0, vis: true, scale, celebrate: false };
    });
    return { chapters, sky, cast };
  }, [props, reduced]);

  // lights: soft day (hemisphere + sun with a shadow box that follows the action) and a night fill for the Final
  const sun = useMemo(() => {
    const l = new THREE.DirectionalLight('#FFF3E0', 2.1);
    l.castShadow = QUALITY.shadows;
    l.shadow.mapSize.set(QUALITY.shadowMap * 2, QUALITY.shadowMap * 2);
    const c = l.shadow.camera;
    c.left = c.bottom = -16;
    c.right = c.top = 16;
    c.near = 1;
    c.far = 80;
    l.shadow.bias = -0.0005;
    l.shadow.normalBias = 0.03;
    return l;
  }, []);
  const hemi = useMemo(() => new THREE.HemisphereLight('#EFF6FF', '#F3C9E4', 1.2), []);
  const rim = useMemo(() => new THREE.DirectionalLight('#FFD6F0', 0.7), []);
  const night = useMemo(() => new THREE.PointLight('#7BFFD0', 0, 60, 1.2), []);

  useEffect(() => {
    const fog = new THREE.Fog('#BFE0F7', 40, 150);
    scene.fog = fog;
    scene.add(W.sky.mesh, hemi, sun, sun.target, rim, night);
    W.chapters.forEach((c) => scene.add(c.group));
    W.cast.forEach((m) => scene.add(m.rig.group));
    return () => {
      scene.fog = null;
      scene.remove(W.sky.mesh, hemi, sun, sun.target, rim, night);
      W.chapters.forEach((c) => {
        scene.remove(c.group);
        c.dispose();
      });
      W.cast.forEach((m) => {
        scene.remove(m.rig.group);
        m.rig.dispose();
      });
      W.sky.mat.dispose();
    };
  }, [scene, W, hemi, sun, rim, night]);

  useEffect(() => {
    cam.fov = size.width / size.height < 1 ? 58 : 42;
    cam.updateProjectionMatrix();
  }, [cam, size.width, size.height]);

  const st = useRef({ t: 0, p: prog.current?.raw ?? 0, lps: CHAPTERS.map(() => 0), dig: -1 });
  const poseA: CamPose = useMemo(() => ({ pos: new THREE.Vector3(), look: new THREE.Vector3() }), []);
  const poseB: CamPose = useMemo(() => ({ pos: new THREE.Vector3(), look: new THREE.Vector3() }), []);
  const look = useMemo(() => new THREE.Vector3(), []);
  const fogDay = useMemo(() => new THREE.Color('#BFE0F7'), []);
  const fogNight = useMemo(() => new THREE.Color('#1B0E44'), []);
  const ctx: Ctx = useMemo(
    () => ({ t: 0, dt: 0, speed: 0, cast: W.cast, props, kick: (a: number) => !reduced && (fx.current.ca = Math.min(1.6, fx.current.ca + a)), reduced }),
    [W, props, reduced, fx],
  );

  useFrame((_, dtRaw) => {
    const s = st.current;
    const dt = Math.min(0.05, dtRaw);
    s.t += dt;
    const pr = prog.current!;
    // ease toward the scroll position (reduced motion: a slower, calmer glide)
    const target = pr.raw;
    s.p += (target - s.p) * Math.min(1, dt * (reduced ? 2.5 : 5));
    if (Math.abs(target - s.p) < 1e-5) s.p = target;
    pr.p = s.p;
    const p = s.p;
    ctx.t = s.t;
    ctx.dt = dt;

    const ci = chapterAt(p);
    // which chapters are on screen (the current one, and its neighbour inside a fly-over zone)
    let other = -1, u = 0;
    const cur = CHAPTERS[ci];
    if (ci > 0 && p < cur.a + ZONE) {
      other = ci - 1;
      u = 0.5 + (p - cur.a) / (2 * ZONE);
    } else if (ci < CHAPTERS.length - 1 && p > cur.b - ZONE) {
      other = ci + 1;
      u = 0.5 - (cur.b - p) / (2 * ZONE);
    }
    // the cast belongs to whichever side of the fly-over we are on
    const owner = other < 0 ? ci : other < ci ? (u < 0.5 ? other : ci) : u < 0.5 ? ci : other;
    let dig = 0;
    W.chapters.forEach((c, i) => {
      const vis = i === ci || i === other;
      c.group.visible = vis;
      const lp = localP(p, CHAPTERS[i]);
      if (!vis) {
        s.lps[i] = lp;
        return;
      }
      ctx.speed = Math.abs(lp - s.lps[i]) / Math.max(1e-3, dt);
      s.lps[i] = lp;
      if (i === owner) c.update(lp, ctx);
      else updateDecorOnly(c, lp, ctx);
      if (c.digital) dig = Math.max(dig, c.digital(lp) * (i === ci ? 1 : 0));
    });

    // cast
    for (const m of W.cast) {
      const g = m.rig.group;
      g.visible = m.vis;
      if (!m.vis) continue;
      g.position.copy(m.pos);
      g.rotation.set(m.rx, m.yaw, m.rz, 'YXZ');
      m.rig.pose = m.celebrate ? 'celebrate' : 'auto';
      m.rig.update(dt, { run: m.run });
    }

    // camera: the chapter's rig, or a swoop between two
    const A = W.chapters[ci];
    if (other < 0) {
      A.cam(localP(p, cur), poseA);
      cam.position.copy(poseA.pos);
      look.copy(poseA.look);
    } else {
      const first = other < ci ? other : ci;
      const second = other < ci ? ci : other;
      // the round card covers the screen mid-zone: hold the outgoing end shot, cut behind the card, then a short
      // settle-in drift on the incoming opening shot
      if (u < 0.5) {
        W.chapters[first].cam(1, poseA);
        cam.position.copy(poseA.pos);
        look.copy(poseA.look);
      } else {
        W.chapters[second].cam(0, poseB);
        const k = 1 - ease(Math.min(1, (u - 0.5) * 2));
        cam.position.copy(poseB.pos);
        cam.position.y += k * 2.5;
        cam.position.lerp(poseB.look, -k * 0.12);
        look.copy(poseB.look);
      }
    }
    cam.lookAt(look);

    // sky, fog, lights follow the action
    W.sky.mesh.position.copy(cam.position);
    W.sky.mat.uniforms.digital.value = dig;
    W.sky.mat.uniforms.time.value = s.t;
    const fog = scene.fog as THREE.Fog;
    fog.color.copy(fogDay).lerp(fogNight, dig);
    fog.near = 40 - dig * 10;
    fog.far = 150 - dig * 40;
    hemi.intensity = 1.2 - dig * 0.55;
    sun.intensity = 2.1 - dig * 1.1;
    night.intensity = dig * 60;
    const focus = W.chapters[owner].focus;
    sun.target.position.copy(focus);
    sun.position.set(focus.x + 9, focus.y + 22, focus.z + 12);
    rim.position.set(focus.x - 8, focus.y + 6, focus.z - 10);
    night.position.set(focus.x, focus.y + 9, focus.z + 2);
    if (Math.abs(dig - s.dig) > 0.01) {
      s.dig = dig;
      onDig.current?.(dig);
    }
  });

  return null;
}

/** A neighbouring chapter in a fly-over still animates its ambient bits, but must not move the cast. */
const shadow = new Map<Chapter, CastMember[]>();
function updateDecorOnly(c: Chapter, lp: number, ctx: Ctx) {
  let fake = shadow.get(c);
  if (!fake) {
    fake = ctx.cast.map((m) => ({ ...m, pos: m.pos.clone(), rig: m.rig }));
    shadow.set(c, fake);
  }
  const real = ctx.cast;
  ctx.cast = fake;
  // rig events (cheer / hit / jump) from the decor-only pass would hit the real rigs: neutralise them
  const rigs = fake.map((m) => m.rig);
  const stub = rigStub();
  fake.forEach((m) => (m.rig = stub));
  try {
    c.update(lp, ctx);
  } finally {
    fake.forEach((m, i) => (m.rig = rigs[i]));
    ctx.cast = real;
  }
}
let stubRig: RushlingRig | null = null;
const rigStub = () =>
  (stubRig ??= { cheer: () => undefined, hit: () => false, jump: () => false } as unknown as RushlingRig);
