/**
 * The game's original capsule-toy prop kit (tools/blender/props.py → public/models/*.glb, ≤ 1500 tris each).
 * Each .glb carries COLOR_0 = the soft-look albedo × baked AO shade, written as sRGB numbers; we convert to linear once
 * and render with one shared vertex-colour material (soft vinyl).
 */
import * as THREE from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';

export type PropName =
  | 'arch_post' | 'arch_seg' | 'bleacher_block' | 'bunting' | 'capsule_machine' | 'claw' | 'coin_slot' | 'confetti_cannon'
  | 'goo_blob' | 'gumball' | 'inflatable_tower' | 'mallet_head' | 'padded_beam' | 'pennant' | 'pillow_bumper' | 'prize_star'
  | 'ribbon_banner' | 'spinner_cap' | 'spinner_hub' | 'spring_pad' | 'start_arch' | 'windup_key';

const loader = new GLTFLoader();
const cache = new Map<PropName, Promise<THREE.BufferGeometry>>();

export const propMaterial = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.42, metalness: 0 });

export function loadProp(name: PropName): Promise<THREE.BufferGeometry> {
  if (!cache.has(name)) {
    cache.set(
      name,
      loader.loadAsync(`/models/${name}.glb`).then((g) => {
        let geo: THREE.BufferGeometry | null = null;
        g.scene.traverse((o) => {
          const m = o as THREE.Mesh;
          if (!geo && m.isMesh) {
            geo = m.geometry.clone();
            geo.applyMatrix4(m.matrixWorld);
          }
        });
        if (!geo) throw new Error(`prop ${name}: no mesh`);
        const g2 = geo as THREE.BufferGeometry;
        const col = g2.getAttribute('color') as THREE.BufferAttribute | undefined;
        if (col) {
          // glTF COLOR_0 may arrive normalized (unsigned short) or float; rebuild as float linear RGB
          const out = new Float32Array(col.count * 3);
          const c = new THREE.Color();
          for (let i = 0; i < col.count; i++) {
            c.setRGB(col.getX(i), col.getY(i), col.getZ(i), THREE.SRGBColorSpace);
            out[i * 3] = c.r;
            out[i * 3 + 1] = c.g;
            out[i * 3 + 2] = c.b;
          }
          g2.setAttribute('color', new THREE.BufferAttribute(out, 3));
        }
        g2.computeBoundingBox();
        g2.computeBoundingSphere();
        return g2;
      }),
    );
  }
  return cache.get(name)!;
}

export const loadProps = (names: PropName[]) =>
  Promise.all(names.map(loadProp)).then((gs) => Object.fromEntries(names.map((n, i) => [n, gs[i]])) as Record<PropName, THREE.BufferGeometry>);

/** A mesh of a loaded prop with the shared material (optionally tinted by a colour multiply via a material clone). */
export function propMesh(geo: THREE.BufferGeometry, opts: { shadows?: boolean; material?: THREE.Material } = {}) {
  const m = new THREE.Mesh(geo, opts.material ?? propMaterial);
  m.castShadow = !!opts.shadows;
  m.receiveShadow = !!opts.shadows;
  return m;
}
