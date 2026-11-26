/**
 * The personal race poster: 1600×900 PNG, drawn client-side.
 * The Rushling is rendered offscreen (own WebGLRenderer, preserveDrawingBuffer) in a celebrating pose,
 * then composited on a 2D canvas. Safe areas: 70 px top/bottom clear, and the bottom-left corner
 * (X crops large cards slightly and overlays the domain there).
 */
import * as THREE from 'three';
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js';
import type { Preset } from '../lib/presets';
import { RushlingRig } from './Rushling';

export const POSTER_W = 1600;
export const POSTER_H = 900;

export interface PosterInput {
  preset: Preset;
  /** null = the generic site card (og-default.png). */
  rank: number | null;
  siteHost: string;
}

async function fontsReady() {
  try {
    await Promise.all([
      document.fonts.load('400 120px "Lilita One"'),
      document.fonts.load('400 30px "Varela Round"'),
    ]);
    await document.fonts.ready;
  } catch {
    /* fall back to system fonts */
  }
}

/** Offscreen render of a celebrating Rushling on a transparent background. */
function renderRushling(preset: Preset, w: number, h: number): HTMLCanvasElement {
  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(1);
  renderer.setSize(w, h, false);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.setClearColor(0x000000, 0);
  const scene = new THREE.Scene();
  const pmrem = new THREE.PMREMGenerator(renderer);
  const room = new RoomEnvironment();
  const env = pmrem.fromScene(room, 0.04).texture;
  scene.environment = env;
  scene.environmentIntensity = 0.4;
  scene.add(new THREE.HemisphereLight('#EFF6FF', '#F3C9E4', 1.2));
  const sun = new THREE.DirectionalLight('#FFF3E0', 2.2);
  sun.position.set(3, 6, 6);
  scene.add(sun);
  const rim = new THREE.DirectionalLight('#FFD6F0', 0.9);
  rim.position.set(-4, 3, -4);
  scene.add(rim);

  const rig = new RushlingRig(preset, { shadows: false });
  rig.pose = 'celebrate';
  rig.group.rotation.y = 0.32;
  scene.add(rig.group);
  // settle the noodle arms into the arms-up pose
  for (let i = 0; i < 90; i++) rig.update(1 / 60, { run: 0 });

  const cam = new THREE.PerspectiveCamera(26, w / h, 0.1, 50);
  cam.position.set(0.55, 0.95, 4.3);
  cam.lookAt(0, 0.62, 0);
  renderer.render(scene, cam);

  const out = document.createElement('canvas');
  out.width = w;
  out.height = h;
  out.getContext('2d')!.drawImage(renderer.domElement, 0, 0);

  rig.dispose();
  env.dispose();
  pmrem.dispose();
  room.traverse((o) => {
    const m = o as THREE.Mesh;
    m.geometry?.dispose();
    (m.material as THREE.Material | undefined)?.dispose?.();
  });
  renderer.dispose();
  renderer.forceContextLoss();
  return out;
}

const rr = (x: CanvasRenderingContext2D, X: number, Y: number, W: number, H: number, R: number) => {
  x.beginPath();
  x.moveTo(X + R, Y);
  x.arcTo(X + W, Y, X + W, Y + H, R);
  x.arcTo(X + W, Y + H, X, Y + H, R);
  x.arcTo(X, Y + H, X, Y, R);
  x.arcTo(X, Y, X + W, Y, R);
  x.closePath();
};

/** Seeded random so the same player gets the same confetti. */
const rng = (seed: number) => () => {
  seed = (seed * 16807) % 2147483647;
  return (seed - 1) / 2147483646;
};

/** Wordmark like the site: white / yellow, thick grape stroke, stacked purple extrusion. */
function wordmark(x: CanvasRenderingContext2D, text: string, fill: string, px: number, py: number, size: number) {
  x.font = `400 ${size}px "Lilita One", "Arial Rounded MT Bold", sans-serif`;
  x.textBaseline = 'alphabetic';
  x.lineJoin = 'round';
  const ext = ['#2A1250', '#4A1C99', '#5F25BD', '#6A28D9'];
  const steps = [0.16, 0.125, 0.085, 0.045];
  x.save();
  x.shadowColor = 'rgba(42,18,80,0.35)';
  x.shadowBlur = size * 0.25;
  x.shadowOffsetY = size * 0.1;
  x.lineWidth = size * 0.17;
  x.strokeStyle = ext[0];
  x.strokeText(text, px, py + size * steps[0]);
  x.restore();
  steps.forEach((s, i) => {
    x.lineWidth = size * 0.17;
    x.strokeStyle = ext[i];
    x.strokeText(text, px, py + size * s);
    x.fillStyle = ext[i];
    x.fillText(text, px, py + size * s);
  });
  x.lineWidth = size * 0.17;
  x.strokeStyle = '#7B2FF7';
  x.strokeText(text, px, py);
  x.fillStyle = fill;
  x.fillText(text, px, py);
}

function cloud(x: CanvasRenderingContext2D, cx: number, cy: number, s: number) {
  x.beginPath();
  [[-1.1, 0.2, 0.7], [-0.4, -0.2, 0.9], [0.5, -0.1, 0.8], [1.2, 0.25, 0.6], [0, 0.35, 0.8]].forEach(([dx, dy, r]) => {
    x.moveTo(cx + dx * s + r * s, cy + dy * s);
    x.arc(cx + dx * s, cy + dy * s, r * s, 0, Math.PI * 2);
  });
  x.fill();
}

export async function generatePoster({ preset, rank, siteHost }: PosterInput): Promise<Blob> {
  await fontsReady();
  const W = POSTER_W, H = POSTER_H;
  const c = document.createElement('canvas');
  c.width = W;
  c.height = H;
  const x = c.getContext('2d')!;
  const rand = rng(((rank ?? 7) * 7919 + preset.name.length * 104729) % 2147483646 || 1);

  // sky
  const sky = x.createLinearGradient(0, 0, 0, H);
  sky.addColorStop(0, '#4FA6EC');
  sky.addColorStop(0.38, '#66B5F2');
  sky.addColorStop(0.72, '#9CD2F5');
  sky.addColorStop(1, '#DDEFFB');
  x.fillStyle = sky;
  x.fillRect(0, 0, W, H);

  // sun rays + glow behind the character (left half)
  const cx = 420, cy = 470;
  x.save();
  x.translate(cx, cy);
  for (let i = 0; i < 18; i++) {
    x.rotate((Math.PI * 2) / 18);
    x.beginPath();
    x.moveTo(0, 0);
    x.lineTo(1100, -95);
    x.lineTo(1100, 95);
    x.closePath();
    x.fillStyle = i % 2 ? 'rgba(255,255,255,0.13)' : 'rgba(255,255,255,0.04)';
    x.fill();
  }
  x.restore();
  const glow = x.createRadialGradient(cx, cy, 20, cx, cy, 420);
  glow.addColorStop(0, 'rgba(255,246,200,0.95)');
  glow.addColorStop(0.35, 'rgba(255,236,160,0.45)');
  glow.addColorStop(1, 'rgba(255,236,160,0)');
  x.fillStyle = glow;
  x.fillRect(0, 0, W, H);

  // distant floating islands (simple pastel discs)
  const isl = [['#F9D1E2', '#DB79A4'], ['#F9EDC2', '#DBC679'], ['#D6EDF9', '#79BADB'], ['#DED1F9', '#9879DB']];
  [[90, 300, 70], [760, 250, 56], [1520, 560, 64], [830, 620, 46]].forEach(([ix, iy, r], i) => {
    const [top, side] = isl[i % isl.length];
    x.fillStyle = '#8F6FD6';
    x.fillRect(ix - r * 0.18, iy + 6, r * 0.36, 200);
    x.fillStyle = side;
    x.beginPath();
    x.ellipse(ix, iy + 14, r, r * 0.3, 0, 0, Math.PI * 2);
    x.fill();
    x.fillStyle = top;
    x.beginPath();
    x.ellipse(ix, iy, r, r * 0.3, 0, 0, Math.PI * 2);
    x.fill();
  });

  // pedestal: lavender, yellow ring
  const px = 420, py = 735;
  x.fillStyle = '#9879DB';
  x.beginPath();
  x.ellipse(px, py + 40, 270, 66, 0, 0, Math.PI * 2);
  x.fill();
  x.fillRect(px - 270, py, 540, 40);
  x.fillStyle = '#DED1F9';
  x.beginPath();
  x.ellipse(px, py, 270, 66, 0, 0, Math.PI * 2);
  x.fill();
  x.strokeStyle = '#FFD21F';
  x.lineWidth = 12;
  x.beginPath();
  x.ellipse(px, py, 212, 48, 0, 0, Math.PI * 2);
  x.stroke();

  // clouds along the bottom (in front of the pedestal base)
  x.fillStyle = '#FFFFFF';
  for (let i = 0; i < 9; i++) cloud(x, 40 + i * 200 + rand() * 60, 880 + rand() * 30, 52 + rand() * 26);

  // the Rushling (offscreen WebGL render)
  try {
    const img = renderRushling(preset, 640, 720);
    x.save();
    x.shadowColor = 'rgba(42,18,80,0.25)';
    x.shadowBlur = 30;
    x.shadowOffsetY = 12;
    x.drawImage(img, px - 320, py - 585);
    x.restore();
  } catch (e) {
    console.warn('poster: WebGL render failed', e);
  }

  // confetti
  const conf = ['#FF3EA5', '#FFD21F', '#7B2FF7', '#3FA9F5', '#2FE0A8', '#FF9A3D'];
  for (let i = 0; i < 70; i++) {
    const fx = rand() * W, fy = 80 + rand() * (H - 160);
    if (fx > 860 && fx < 1560 && fy > 120 && fy < 800 && rand() < 0.7) continue; // keep the text column calm
    x.save();
    x.translate(fx, fy);
    x.rotate(rand() * Math.PI);
    x.fillStyle = conf[i % conf.length];
    x.fillRect(-7, -12, 14, 24);
    x.restore();
  }

  // ---- right column ----
  const colX = 880, colW = 640;
  // wordmark, rotated −4°, pink underline with a checker flag
  x.save();
  x.translate(colX, 250);
  x.rotate((-4 * Math.PI) / 180);
  wordmark(x, 'FALL', '#FFFFFF', 0, 0, 130);
  const fallW = x.measureText('FALL ').width;
  wordmark(x, 'RUSH', '#FFD21F', fallW, 0, 130);
  const total = fallW + x.measureText('RUSH').width;
  const barY = 52, barW = Math.min(total, 520);
  x.fillStyle = '#FF3EA5';
  x.strokeStyle = '#7B2FF7';
  x.lineWidth = 7;
  rr(x, 0, barY, barW - 30, 22, 11);
  x.fill();
  x.stroke();
  const fx0 = barW - 40, fy0 = barY - 6, fw = 64, fh = 34;
  x.fillStyle = '#FFFFFF';
  rr(x, fx0, fy0, fw, fh, 5);
  x.fill();
  x.save();
  rr(x, fx0, fy0, fw, fh, 5);
  x.clip();
  x.fillStyle = '#7B2FF7';
  for (let i = 0; i < 4; i++) for (let j = 0; j < 2; j++) if ((i + j) % 2 === 0) x.fillRect(fx0 + i * (fw / 4), fy0 + j * (fh / 2), fw / 4, fh / 2);
  x.restore();
  x.strokeStyle = '#7B2FF7';
  x.lineWidth = 5;
  rr(x, fx0, fy0, fw, fh, 5);
  x.stroke();
  x.restore();

  // badge
  const bx = colX, by = 360, bw = colW, bh = 230;
  x.fillStyle = '#2A1250';
  rr(x, bx, by + 10, bw, bh, 30);
  x.fill();
  x.fillStyle = '#FFD21F';
  rr(x, bx, by, bw, bh, 30);
  x.fill();
  x.strokeStyle = '#2A1250';
  x.lineWidth = 6;
  x.stroke();
  x.fillStyle = '#2A1250';
  x.textAlign = 'center';
  x.font = '400 38px "Lilita One", sans-serif';
  x.fillText(rank == null ? 'EVERY ROUND ON-CHAIN' : 'IN LINE FOR THE CROWN', bx + bw / 2, by + 64);
  const big = rank == null ? 'KNOCKOUT PARTY' : `#${rank.toLocaleString('en-US')}`;
  let size = rank == null ? 84 : 150;
  x.font = `400 ${size}px "Lilita One", sans-serif`;
  while (x.measureText(big).width > bw - 70 && size > 40) {
    size -= 4;
    x.font = `400 ${size}px "Lilita One", sans-serif`;
  }
  x.fillStyle = '#7B2FF7';
  x.fillText(big, bx + bw / 2 + 4, by + 76 + (bh - 76) / 2 + size * 0.36 + 6);
  x.fillStyle = '#2A1250';
  x.fillText(big, bx + bw / 2, by + 76 + (bh - 76) / 2 + size * 0.36);

  // name plate in the shell colour
  const plate = rank == null ? 'PICK YOUR RUSHLING' : `${preset.name.toUpperCase()} JOINED THE RACE`;
  let ps = 44;
  x.font = `400 ${ps}px "Lilita One", sans-serif`;
  while (x.measureText(plate).width > colW - 80 && ps > 24) {
    ps -= 2;
    x.font = `400 ${ps}px "Lilita One", sans-serif`;
  }
  const ny = 630, nh = 84;
  x.fillStyle = '#2A1250';
  rr(x, bx, ny + 8, colW, nh, 24);
  x.fill();
  x.fillStyle = preset.shell;
  rr(x, bx, ny, colW, nh, 24);
  x.fill();
  x.strokeStyle = '#2A1250';
  x.lineWidth = 6;
  x.stroke();
  x.lineJoin = 'round';
  x.lineWidth = 8;
  x.strokeStyle = '#2A1250';
  x.strokeText(plate, bx + colW / 2, ny + nh / 2 + ps * 0.36);
  x.fillStyle = '#FFFFFF';
  x.fillText(plate, bx + colW / 2, ny + nh / 2 + ps * 0.36);

  // footer line (above the 70 px bottom safe area)
  x.font = '400 28px "Varela Round", sans-serif';
  x.fillStyle = '#2A1250';
  x.fillText(rank == null ? 'Packed lobbies · 1 crown · No wallet needed' : `Pick your Rushling at ${siteHost}`, bx + colW / 2, 790);
  x.textAlign = 'left';

  return new Promise((res, rej) => c.toBlob((b) => (b ? res(b) : rej(new Error('toBlob failed'))), 'image/png'));
}
