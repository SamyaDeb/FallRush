/**
 * `/join` background: a floating turntable (lavender disc, checker rim, pillar) with the five playable Rushlings on pads
 * facing outward. The selected one turns to the front, grows to 1.4×, faces the camera and hops with star eyes;
 * a pulsing yellow ring marks the front spot.
 */
import { createRef, useEffect, useMemo, useRef, useState, type RefObject } from 'react';
import { Canvas, useFrame, useThree } from '@react-three/fiber';
import * as THREE from 'three';
import { PLAYABLE } from '../lib/presets';
import { Rushling, type RushlingRig } from './Rushling';
import { QUALITY } from './quality';
import { AdaptiveDpr, Confetti, Daylight, FirstFrames, cloudMaterial, disposeTree, makeCloud, prefersReducedMotion, useActive } from './common';
import { checkerTexture } from './textures';

export interface JoinSceneProps {
  selected: number;
  /** Increment to make everyone jump with a confetti burst (after joining). */
  party: number;
  /** Celebrating pose for the selected Rushling (poster / share steps). */
  celebrate: boolean;
  onReady?: () => void;
}

const RADIUS = 2.15;
const STEP = (Math.PI * 2) / PLAYABLE.length;

export default function JoinScene(props: JoinSceneProps) {
  const host = useRef<HTMLDivElement>(null);
  const active = useActive(host);
  const [shown, setShown] = useState(false);
  return (
    <div ref={host} className={'stage' + (shown ? ' on' : '')} aria-hidden="true">
      <Canvas
        shadows={QUALITY.shadows ? { type: THREE.PCFShadowMap } : false}
        dpr={QUALITY.dpr}
        frameloop={active ? 'always' : 'never'}
        camera={{ fov: 36, near: 0.1, far: 120, position: [0, 2.9, 9.2] }}
        gl={{ antialias: QUALITY.antialias, alpha: true, powerPreference: 'default' }}
        onCreated={({ gl }) => {
          if (import.meta.env.DEV) (window as unknown as { __gl: unknown }).__gl = gl; // dev-only perf inspection
        }}
      >
        <fog attach="fog" args={['#D4EAF9', 16, 46]} />
        <FirstFrames
          onShown={() => {
            setShown(true);
            props.onReady?.();
          }}
        />
        <AdaptiveDpr min={1} max={QUALITY.dpr[1]} />
        <Daylight shadowExtent={5} sunPos={[4, 10, 7]} shadows={QUALITY.shadows} shadowMap={QUALITY.shadowMap} />
        <Turntable {...props} />
      </Canvas>
    </div>
  );
}

function buildDecor() {
  const g = new THREE.Group();
  const lav = new THREE.MeshStandardMaterial({ color: '#DED1F9', roughness: 0.5 });
  const side = new THREE.MeshStandardMaterial({ color: '#9879DB', roughness: 0.6 });
  const disc = new THREE.Mesh(new THREE.CylinderGeometry(3.2, 3.0, 0.5, 64), [side, lav, side]);
  disc.position.y = -0.25;
  disc.receiveShadow = true;
  g.add(disc);
  const rim = new THREE.Mesh(new THREE.TorusGeometry(3.2, 0.16, 12, 80), new THREE.MeshStandardMaterial({ map: checkerTexture(24, 1, '#7B2FF7', '#FFFFFF'), roughness: 0.4 }));
  rim.rotation.x = Math.PI / 2;
  rim.receiveShadow = true;
  g.add(rim);
  const pillar = new THREE.Mesh(new THREE.CylinderGeometry(1.1, 0.6, 9, 24), new THREE.MeshStandardMaterial({ color: '#8F6FD6', roughness: 0.7 }));
  pillar.position.y = -5;
  g.add(pillar);
  // pads under each Rushling
  const padM = new THREE.MeshStandardMaterial({ color: '#FFFFFF', roughness: 0.35 });
  const pads: THREE.Mesh[] = [];
  PLAYABLE.forEach((p, i) => {
    const a = i * STEP;
    const pad = new THREE.Mesh(new THREE.CylinderGeometry(0.55, 0.6, 0.08, 36), [new THREE.MeshStandardMaterial({ color: p.shell, roughness: 0.4 }), padM, padM]);
    pad.position.set(Math.sin(a) * RADIUS, 0.04, Math.cos(a) * RADIUS);
    pad.receiveShadow = true;
    pads.push(pad);
  });
  return { g, pads };
}

function Turntable({ selected, party, celebrate }: JoinSceneProps) {
  const { scene, camera, size } = useThree();
  const reduce = useMemo(prefersReducedMotion, []);
  const spin = useRef<THREE.Group>(null);
  const refs = useMemo(() => PLAYABLE.map(() => createRef<RushlingRig>()) as RefObject<RushlingRig | null>[], []);
  const holders = useRef<(THREE.Group | null)[]>([]);
  const decor = useMemo(buildDecor, []);
  const confetti = useMemo(() => new Confetti(Math.round(QUALITY.confetti * 0.9), { x: 7, yTop: 8, z0: -6, z1: 5, floor: -2 }, reduce), [reduce]);
  const world = useMemo(() => {
    const g = new THREE.Group();
    const cm = cloudMaterial();
    const clouds: THREE.Group[] = [];
    for (let i = 0; i < (QUALITY.tier === 'low' ? 5 : 9); i++) {
      const c = makeCloud(cm, 0.9);
      const a = (i / 9) * Math.PI * 2;
      c.position.set(Math.sin(a) * (9 + (i % 3) * 3), i % 2 ? -3.5 - Math.random() * 2 : 4 + Math.random() * 3, Math.cos(a) * 7 - 8);
      g.add(c);
      clouds.push(c);
    }
    // pulsing yellow ring on the front spot (fixed in the world)
    const ring = new THREE.Mesh(new THREE.TorusGeometry(0.72, 0.045, 10, 48), new THREE.MeshBasicMaterial({ color: '#FFD21F' }));
    ring.rotation.x = Math.PI / 2;
    ring.position.set(0, 0.1, RADIUS);
    g.add(ring);
    return { g, clouds, ring };
  }, []);

  useEffect(() => {
    scene.add(world.g, confetti.mesh);
    return () => {
      scene.remove(world.g, confetti.mesh);
      disposeTree(world.g);
      disposeTree(decor.g);
      decor.pads.forEach((p) => disposeTree(p));
      confetti.dispose();
    };
  }, [scene, world, confetti, decor]);

  // selected one hops with star eyes
  useEffect(() => {
    refs[selected].current?.cheer(1.4);
  }, [selected, refs]);

  // everyone jumps + confetti after joining
  useEffect(() => {
    if (!party) return;
    refs.forEach((r, i) => setTimeout(() => r.current?.cheer(1.6), i * 90));
    confetti.burst(new THREE.Vector3(0, 1.2, RADIUS), 110);
  }, [party, refs, confetti]);

  const portrait = size.width <= 900 || size.width / size.height < 0.9;
  const t = useRef(0);
  useFrame((_, dtRaw) => {
    const dt = Math.min(0.05, dtRaw);
    t.current += dt;
    const g = spin.current;
    if (g) {
      // shortest turn to bring the selected pad to the front (+z)
      const target = -selected * STEP;
      let d = target - g.rotation.y;
      d = Math.atan2(Math.sin(d), Math.cos(d));
      g.rotation.y += d * Math.min(1, dt * (reduce ? 3 : 5));
    }
    holders.current.forEach((h, i) => {
      if (!h) return;
      const s = h.scale.x + ((i === selected ? 1.4 : 1) - h.scale.x) * Math.min(1, dt * 6);
      h.scale.setScalar(s);
    });
    const pulse = reduce ? 1 : 1 + Math.sin(t.current * 4) * 0.06;
    world.ring.scale.set(pulse, pulse, 1);
    world.clouds.forEach((c, i) => (c.position.y += Math.sin(t.current * 0.6 + i) * 0.002));
    // turntable centred in the upper part of the screen; the step card sits below it
    if (portrait) {
      // phones: a smaller turntable fully in view in the top third, the card right under it
      camera.position.set(0, 8.2, 27.5);
      camera.lookAt(0, -4.9, 0);
    } else {
      camera.position.set(0, 3.4, 12.2);
      camera.lookAt(0, -0.95, 0);
    }
    confetti.update(dt);
  });

  return (
    <group ref={spin}>
      <primitive object={decor.g} />
      {decor.pads.map((p, i) => (
        <primitive key={'pad' + i} object={p} />
      ))}
      {PLAYABLE.map((p, i) => {
        const a = i * STEP;
        return (
          <group key={p.name} ref={(el) => void (holders.current[i] = el)} position={[Math.sin(a) * RADIUS, 0.08, Math.cos(a) * RADIUS]} rotation-y={a}>
            <Rushling ref={refs[i]} preset={p} pose={celebrate && i === selected ? 'celebrate' : 'auto'} timeScale={reduce ? 0.5 : 1} />
          </group>
        );
      })}
    </group>
  );
}
