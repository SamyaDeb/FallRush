import * as THREE from 'three';

export type FaceMode = 'happy' | 'run' | 'jump' | 'star' | 'blink' | 'dizzy';

const canvas = (w: number, h: number) => {
  const c = document.createElement('canvas');
  c.width = w;
  c.height = h;
  return c;
};

export const canvasTex = (c: HTMLCanvasElement, rx = 0, ry = 0) => {
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 8;
  if (rx) {
    t.wrapS = t.wrapT = THREE.RepeatWrapping;
    t.repeat.set(rx, ry);
  }
  return t;
};

/** Shared, never disposed per Rushling (cached for the page's lifetime; tiny). */
const faceCache = new Map<string, THREE.CanvasTexture>();

/**
 * LED screen face (doc 17 §2.2): dark screen, glowing glyphs in the face-theme colour.
 * Canvas aspect matches the screen patch (≈ 0.41 × 0.26 on the dome).
 */
export function faceTexture(mode: FaceMode, tint: string): THREE.CanvasTexture {
  const key = mode + tint;
  const hit = faceCache.get(key);
  if (hit) return hit;
  const W = 256, H = 160;
  const c = canvas(W, H);
  const x = c.getContext('2d')!;
  const g = x.createLinearGradient(0, 0, 0, H);
  g.addColorStop(0, '#24154A');
  g.addColorStop(1, '#140B2C');
  x.fillStyle = g;
  x.fillRect(0, 0, W, H);
  // faint LED scanlines
  x.fillStyle = 'rgba(255,255,255,0.035)';
  for (let y = 0; y < H; y += 6) x.fillRect(0, y, W, 2);

  x.strokeStyle = tint;
  x.fillStyle = tint;
  x.lineWidth = 15;
  x.lineCap = 'round';
  x.lineJoin = 'round';
  x.shadowColor = tint;
  x.shadowBlur = 18;
  const eyes = [84, 172];
  const y = 80;
  for (const cx of eyes) {
    x.beginPath();
    switch (mode) {
      case 'happy':
        x.moveTo(cx - 22, y + 12);
        x.lineTo(cx, y - 12);
        x.lineTo(cx + 22, y + 12);
        x.stroke();
        break;
      case 'run':
        x.arc(cx, y, 15, 0, Math.PI * 2);
        x.fill();
        break;
      case 'jump':
        x.lineWidth = 12;
        x.arc(cx, y, 21, 0, Math.PI * 2);
        x.stroke();
        break;
      case 'blink':
        x.moveTo(cx - 20, y + 4);
        x.lineTo(cx + 20, y + 4);
        x.stroke();
        break;
      case 'dizzy':
        x.moveTo(cx - 17, y - 17);
        x.lineTo(cx + 17, y + 17);
        x.moveTo(cx + 17, y - 17);
        x.lineTo(cx - 17, y + 17);
        x.stroke();
        break;
      case 'star':
        for (let i = 0; i < 10; i++) {
          const a = -Math.PI / 2 + (i * Math.PI) / 5;
          const r = i % 2 ? 12 : 29;
          const px = cx + Math.cos(a) * r, py = y + 2 + Math.sin(a) * r;
          if (i) x.lineTo(px, py);
          else x.moveTo(px, py);
        }
        x.closePath();
        x.fill();
        break;
    }
  }
  const t = canvasTex(c);
  faceCache.set(key, t);
  return t;
}

// ---------- world textures (hero / join scenes) ----------

export function trackTexture(repeatY: number) {
  const c = canvas(512, 512);
  const x = c.getContext('2d')!;
  const tops = ['#F9D1E2', '#F9EDC2', '#D6EDF9', '#DED1F9'];
  const edge = ['#F0BFD5', '#F0DFA8', '#C2E1F2', '#CDBDF2'];
  tops.forEach((col, i) => {
    x.fillStyle = col;
    x.fillRect(0, i * 128, 512, 128);
    x.strokeStyle = edge[i];
    x.lineWidth = 6;
    x.strokeRect(3, i * 128 + 3, 506, 122);
    x.strokeStyle = 'rgba(255,255,255,0.92)';
    x.lineWidth = 22;
    x.lineCap = 'round';
    x.lineJoin = 'round';
    x.beginPath();
    // canvas-down = toward the camera on the box top: chevrons point the way the racers run
    x.moveTo(196, i * 128 + 40);
    x.lineTo(256, i * 128 + 84);
    x.lineTo(316, i * 128 + 40);
    x.stroke();
  });
  return canvasTex(c, 1, repeatY);
}

/** Padded rim bands: pink / yellow. */
export function bandTexture(repeatY: number) {
  const c = canvas(64, 128);
  const x = c.getContext('2d')!;
  x.fillStyle = '#FF6FAE';
  x.fillRect(0, 0, 64, 64);
  x.fillStyle = '#FFD43B';
  x.fillRect(0, 64, 64, 64);
  return canvasTex(c, 1, repeatY);
}

/** Hazard stripes: hot pink + lemon. */
export function stripeTexture(rep: number) {
  const c = canvas(128, 128);
  const x = c.getContext('2d')!;
  x.fillStyle = '#FF2D8A';
  x.fillRect(0, 0, 128, 128);
  x.fillStyle = '#FFD43B';
  for (let i = -2; i < 4; i++) {
    x.beginPath();
    x.moveTo(i * 64, 0);
    x.lineTo(i * 64 + 32, 0);
    x.lineTo(i * 64 + 160, 128);
    x.lineTo(i * 64 + 128, 128);
    x.closePath();
    x.fill();
  }
  return canvasTex(c, rep, rep);
}

export function checkerTexture(rx: number, ry: number, a = '#2FE0A8', b = '#FFFFFF') {
  const c = canvas(128, 128);
  const x = c.getContext('2d')!;
  for (let i = 0; i < 4; i++)
    for (let j = 0; j < 4; j++) {
      x.fillStyle = (i + j) % 2 ? b : a;
      x.fillRect(i * 32, j * 32, 32, 32);
    }
  return canvasTex(c, rx, ry);
}
