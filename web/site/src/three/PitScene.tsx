/**
 * The Rushling pit: a toy box full of Rushlings and gumballs with real physics (Rapier). Grab one and fling it, tap to
 * bonk, or shake the whole box. Loaded only when the section comes near (the wasm is ~0.6 MB).
 */
import { forwardRef, use, useEffect, useImperativeHandle, useMemo, useRef } from 'react';
import { Canvas, useFrame, useThree, type ThreeEvent } from '@react-three/fiber';
import { CapsuleCollider, CuboidCollider, Physics, RigidBody, type RapierRigidBody } from '@react-three/rapier';
import * as THREE from 'three';
import { RushlingRig } from './Rushling';
import { QUALITY } from './quality';
import { AdaptiveDpr, Daylight, FirstFrames, useActive } from './common';
import { loadProps, propMaterial } from './props';
import { CAST, C, Merge, hash, softMaterial } from './story/kit';
import { play } from '../lib/sfx';

export interface PitHandle {
  shake: () => void;
}

const BOX = { w: 14, d: 9, wall: 2.2 };
const N_RUSH = QUALITY.tier === 'high' ? 22 : 12;
const N_BALL = QUALITY.tier === 'high' ? 18 : 8;
let pitProps: ReturnType<typeof loadProps> | null = null;

const PitScene = forwardRef<PitHandle, { onReady?: () => void }>(function PitScene({ onReady }, ref) {
  const host = useRef<HTMLDivElement>(null);
  const active = useActive(host);
  const api = useRef<PitHandle>({ shake: () => undefined });
  useImperativeHandle(ref, () => ({ shake: () => api.current.shake() }), []);
  return (
    <div ref={host} className="pit-stage">
      <Canvas
        shadows={QUALITY.shadows ? { type: THREE.PCFShadowMap } : false}
        dpr={QUALITY.dpr}
        frameloop={active ? 'always' : 'never'}
        camera={{ fov: 38, near: 0.1, far: 120, position: [0, 13, 13.5] }}
        gl={{ antialias: true, alpha: true }}
      >
        <FirstFrames onShown={() => onReady?.()} />
        <AdaptiveDpr min={1} max={QUALITY.dpr[1]} />
        <Daylight shadowExtent={10} sunPos={[6, 16, 8]} shadows={QUALITY.shadows} shadowMap={QUALITY.shadowMap} />
        <Physics gravity={[0, -22, 0]} paused={!active}>
          <Pit api={api} />
        </Physics>
      </Canvas>
    </div>
  );
});
export default PitScene;

interface Body {
  rb: React.RefObject<RapierRigidBody | null>;
  rig?: RushlingRig;
}

function Pit({ api }: { api: React.RefObject<PitHandle> }) {
  const props = use((pitProps ??= loadProps(['gumball'])));
  const { camera, gl } = useThree();
  const bodies = useRef<Body[]>([]);
  const grab = useRef<{ b: Body; plane: THREE.Plane; off: THREE.Vector3 } | null>(null);
  const pointer = useRef(new THREE.Vector2());
  const ray = useMemo(() => new THREE.Raycaster(), []);
  const hit = useMemo(() => new THREE.Vector3(), []);

  const box = useMemo(() => {
    const mg = new Merge();
    const { w, d, wall } = BOX;
    mg.block(w + 1.6, 1, d + 1.6, [0, 0, 0], C.grape, '#EFE7FF');
    mg.block(w + 1.6, wall, 0.8, [0, wall, -d / 2 - 0.4], C.bubblegum, C.lemon);
    mg.block(w + 1.6, wall * 0.55, 0.8, [0, wall * 0.55, d / 2 + 0.4], C.bubblegum, C.lemon);
    mg.block(0.8, wall, d + 1.6, [-w / 2 - 0.4, wall, 0], C.soda, C.lemon);
    mg.block(0.8, wall, d + 1.6, [w / 2 + 0.4, wall, 0], C.soda, C.lemon);
    return mg.build(softMaterial(), QUALITY.shadows);
  }, []);

  const rushers = useMemo(
    () =>
      Array.from({ length: N_RUSH }, (_, i) => {
        const rig = new RushlingRig(CAST[i % CAST.length], { shadows: QUALITY.shadows, blob: false });
        return { rig, at: [(hash(i) - 0.5) * (BOX.w - 2), 2 + i * 0.7, (hash(i * 3) - 0.5) * (BOX.d - 2)] as [number, number, number], rb: { current: null } as React.RefObject<RapierRigidBody | null> };
      }),
    [],
  );
  const balls = useMemo(
    () =>
      Array.from({ length: N_BALL }, (_, i) => ({
        at: [(hash(i * 7) - 0.5) * (BOX.w - 2), 3 + i * 0.8, (hash(i * 11) - 0.5) * (BOX.d - 2)] as [number, number, number],
        s: 0.42 + hash(i * 5) * 0.22,
        tint: new THREE.Color([C.magenta, C.soda, C.lemon, C.apple, C.grape, C.tangerine][i % 6]),
        rb: { current: null } as React.RefObject<RapierRigidBody | null>,
      })),
    [],
  );
  const ballMats = useMemo(
    () => balls.map((b) => new THREE.MeshStandardMaterial({ color: b.tint, roughness: 0.22 })),
    [balls],
  );
  // dispose on a real unmount only (React dev StrictMode re-runs effects; rig.dispose() also detaches the group,
  // which <primitive> would not re-attach)
  const alive = useRef(false);
  useEffect(() => {
    alive.current = true;
    bodies.current = [...rushers.map((r) => ({ rb: r.rb, rig: r.rig })), ...balls.map((b) => ({ rb: b.rb }))];
    return () => {
      alive.current = false;
      setTimeout(() => {
        if (alive.current) return;
        rushers.forEach((r) => r.rig.dispose());
        ballMats.forEach((m) => m.dispose());
      }, 0);
    };
  }, [rushers, balls, ballMats]);

  useEffect(() => {
    api.current.shake = () => {
      play('rush', { vol: 0.6 });
      for (const b of bodies.current) {
        const rb = b.rb.current;
        if (!rb) continue;
        rb.wakeUp();
        // a pop of 7-10 m/s up whatever the body's mass
        const m = rb.mass();
        rb.applyImpulse({ x: (Math.random() - 0.5) * 3 * m, y: (7 + Math.random() * 3) * m, z: (Math.random() - 0.5) * 2.5 * m }, true);
        rb.applyTorqueImpulse({ x: (Math.random() - 0.5) * 0.3 * m, y: (Math.random() - 0.5) * 0.3 * m, z: (Math.random() - 0.5) * 0.3 * m }, true);
        b.rig?.cheer(1.2, false);
      }
    };
  }, [api]);

  // pointer tracking for drags (on the canvas; release anywhere)
  useEffect(() => {
    const el = gl.domElement;
    const move = (e: PointerEvent) => {
      const r = el.getBoundingClientRect();
      pointer.current.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
    };
    const up = () => {
      const g = grab.current;
      if (g) {
        g.b.rig?.cheer(0.8, false);
        play('chirp', { vol: 0.5 });
      }
      grab.current = null;
      el.style.cursor = '';
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', up);
    return () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('pointercancel', up);
    };
  }, [gl]);

  const onDown = (b: Body) => (e: ThreeEvent<PointerEvent>) => {
    e.stopPropagation();
    const rb = b.rb.current;
    if (!rb) return;
    const p = rb.translation();
    grab.current = { b, plane: new THREE.Plane(new THREE.Vector3(0, 1, 0), -Math.max(2.4, p.y + 1.2)), off: new THREE.Vector3() };
    rb.wakeUp();
    rb.applyImpulse({ x: 0, y: 3 * rb.mass(), z: 0 }, true);
    play('bonk', { vol: 0.5, gapMs: 120 });
    b.rig?.hit(0.6);
    gl.domElement.style.cursor = 'grabbing';
  };

  useFrame((_, dt) => {
    const g = grab.current;
    if (g) {
      const rb = g.b.rb.current;
      if (rb) {
        ray.setFromCamera(pointer.current, camera);
        if (ray.ray.intersectPlane(g.plane, hit)) {
          hit.x = Math.max(-BOX.w / 2 + 0.6, Math.min(BOX.w / 2 - 0.6, hit.x));
          hit.z = Math.max(-BOX.d / 2 + 0.6, Math.min(BOX.d / 2 - 0.6, hit.z));
          const p = rb.translation();
          rb.setLinvel({ x: (hit.x - p.x) * 14, y: (hit.y - p.y) * 14, z: (hit.z - p.z) * 14 }, true);
          rb.setAngvel({ x: 0, y: 0, z: 0 }, true);
        }
      }
    }
    for (const r of rushers) {
      r.rig.update(dt, { run: 0 });
      // anything that escapes the box drops back in from above
      const rb = r.rb.current;
      if (rb && rb.translation().y < -6) {
        rb.setTranslation({ x: 0, y: 8, z: 0 }, true);
        rb.setLinvel({ x: 0, y: 0, z: 0 }, true);
      }
    }
    for (const b of balls) {
      const rb = b.rb.current;
      if (rb && rb.translation().y < -6) {
        rb.setTranslation({ x: 0, y: 8, z: 0 }, true);
        rb.setLinvel({ x: 0, y: 0, z: 0 }, true);
      }
    }
  });

  const { w, d, wall } = BOX;
  return (
    <>
      <primitive object={box} />
      <RigidBody type="fixed" colliders={false}>
        <CuboidCollider args={[w / 2 + 0.8, 0.5, d / 2 + 0.8]} position={[0, -0.5, 0]} restitution={0.2} friction={0.9} />
        <CuboidCollider args={[w / 2 + 0.8, wall * 2, 0.4]} position={[0, wall * 2, -d / 2 - 0.4]} />
        <CuboidCollider args={[w / 2 + 0.8, wall * 2, 0.4]} position={[0, wall * 2, d / 2 + 0.4]} />
        <CuboidCollider args={[0.4, wall * 2, d / 2 + 0.8]} position={[-w / 2 - 0.4, wall * 2, 0]} />
        <CuboidCollider args={[0.4, wall * 2, d / 2 + 0.8]} position={[w / 2 + 0.4, wall * 2, 0]} />
      </RigidBody>
      {rushers.map((r, i) => (
        <RigidBody
          key={'r' + i}
          ref={r.rb}
          colliders={false}
          position={r.at}
          rotation={[0, hash(i * 9) * 6, 0]}
          restitution={0.45}
          friction={0.7}
          linearDamping={0.15}
          angularDamping={0.6}
          onContactForce={({ totalForceMagnitude }) => totalForceMagnitude > 900 && play("land", { vol: 0.25, gapMs: 90 })}
        >
          <CapsuleCollider args={[0.22, 0.34]} position={[0, 0.56, 0]} />
          <primitive object={r.rig.group} onPointerDown={onDown({ rb: r.rb, rig: r.rig })} />
        </RigidBody>
      ))}
      {balls.map((b, i) => (
        <RigidBody key={'b' + i} ref={b.rb} colliders="ball" position={b.at} restitution={0.7} friction={0.4} linearDamping={0.1}>
          <mesh
            geometry={props.gumball}
            material={ballMats[i] ?? propMaterial}
            scale={b.s}
            castShadow={QUALITY.shadows}
            onPointerDown={onDown({ rb: b.rb })}
          />
        </RigidBody>
      ))}
    </>
  );
}
