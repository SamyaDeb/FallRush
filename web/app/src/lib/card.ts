// P7 A2: "Download card" — the match receipt as a 1080 × 1350 PNG drawn on a 2D canvas (the approach of the landing's
// poster, web/site/src/three/poster.ts, in plain TS: fonts ready → draw → toBlob). No 3D, no library.
export interface CardInput {
  match: string; champion: string; championHuman: boolean; when: string;
  rounds: { map: string; mode: string; line: string }[];
  assets: { name: string; color: string; crown: boolean }[];
  rollup: boolean; solana: boolean; replay: boolean | null; sealed: number; drops: number; pools: string;
}
const INK = '#2a1250', GRAPE = '#7b2ff7', SUN = '#ffd21f', PINK = '#ff3ea5', CREAM = '#fff6e6', OK = '#1fa36a';
const HUE: Record<string, [string, string]> = { Race: ['#d6edf9', '#2aa8ec'], Survival: ['#ded1f9', '#a77dff'], Skill: ['#f9edc2', '#f0b800'], Final: ['#f9d1e2', '#ff6fae'] };

function rr(g: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number) { g.beginPath(); g.roundRect(x, y, w, h, r); }
function chunky(g: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number, fill: string, stroke = INK, drop = 10) {
  g.fillStyle = stroke; rr(g, x, y + drop, w, h, r); g.fill();
  g.fillStyle = fill; rr(g, x, y, w, h, r); g.fill();
  g.lineWidth = 6; g.strokeStyle = stroke; rr(g, x, y, w, h, r); g.stroke();
}
function word(g: CanvasRenderingContext2D, text: string, x: number, y: number, size: number, fill: string, align: CanvasTextAlign = 'left') {
  g.font = `${size}px "Lilita One"`; g.textAlign = align; g.lineJoin = 'round';
  g.lineWidth = size * 0.17; g.strokeStyle = GRAPE; g.strokeText(text, x, y + size * 0.08);
  g.fillStyle = INK; g.fillText(text, x, y + size * 0.16);
  g.strokeText(text, x, y); g.fillStyle = fill; g.fillText(text, x, y);
}
function capsule(g: CanvasRenderingContext2D, cx: number, cy: number, s: number, color: string, crown: boolean) {
  g.lineWidth = 6; g.strokeStyle = INK;
  g.fillStyle = crown ? '#fff8dc' : '#fff'; g.beginPath(); g.moveTo(cx - s, cy); g.arc(cx, cy, s, Math.PI, 0); g.closePath(); g.fill(); g.stroke();
  g.fillStyle = color; g.beginPath(); g.moveTo(cx - s, cy); g.arc(cx, cy, s, 0, Math.PI); g.closePath(); g.fill(); g.stroke();
  g.fillStyle = INK; rr(g, cx - s - 4, cy - 5, 2 * s + 8, 10, 5); g.fill();
  g.fillStyle = 'rgba(79,166,236,.3)'; g.beginPath(); g.ellipse(cx - s * 0.45, cy - s * 0.5, s * 0.14, s * 0.24, 0, 0, 7); g.fill();
  if (crown) {
    g.fillStyle = '#ffb703'; g.strokeStyle = INK; g.lineWidth = 4;
    rr(g, cx - s * 0.6, cy - s * 1.32, s * 1.2, s * 0.22, 6); g.fill(); g.stroke();
    for (let i = 0; i < 5; i++) { const x = cx - s * 0.5 + i * s * 0.25, h = i === 2 ? 0.5 : i % 2 ? 0.34 : 0.4; g.beginPath(); g.arc(x, cy - s * (1.32 + h), s * 0.09, 0, 7); g.fill(); g.stroke(); }
  }
}

export async function drawCard(c: CardInput): Promise<Blob> {
  try { await Promise.all([document.fonts.load('400 120px "Lilita One"'), document.fonts.load('400 30px "Varela Round"')]); } catch { /* system fonts */ }
  const W = 1080, H = 1350, cv = document.createElement('canvas');
  cv.width = W; cv.height = H;
  const g = cv.getContext('2d')!;
  const sky = g.createLinearGradient(0, 0, 0, H); sky.addColorStop(0, '#4fa6ec'); sky.addColorStop(0.35, '#66b5f2'); sky.addColorStop(0.75, '#9cd2f5'); sky.addColorStop(1, '#ddeffb');
  g.fillStyle = sky; g.fillRect(0, 0, W, H);
  g.fillStyle = 'rgba(255,255,255,.55)'; for (const [x, y, r] of [[120, 1220, 120], [300, 1260, 90], [900, 1230, 140], [1040, 1180, 80]]) { g.beginPath(); g.arc(x, y, r, 0, 7); g.fill(); }
  g.save(); g.translate(70, 150); g.rotate(-0.05); word(g, 'FALL', 0, 0, 110, '#fff'); word(g, 'RUSH', 300, 0, 110, SUN); g.restore();
  g.font = '30px "Varela Round"'; g.fillStyle = '#fff'; g.textAlign = 'right'; g.fillText('MATCH RECEIPT', W - 70, 110); g.font = '24px "Varela Round"'; g.fillText(c.when, W - 70, 148);
  // champion card
  chunky(g, 60, 210, W - 120, 330, 40, '#fff3c4');
  capsule(g, 230, 400, 92, '#ffd21f', true);
  g.textAlign = 'left'; g.fillStyle = INK; g.font = '30px "Lilita One"'; g.fillText('CHAMPION', 370, 320);
  word(g, c.champion.length > 16 ? c.champion.slice(0, 15) + '…' : c.champion, 370, 410, 76, SUN);
  g.font = '28px "Varela Round"'; g.fillStyle = INK; g.fillText(c.championHuman ? 'won the on-chain Final · the Crown is theirs' : 'a bot won the on-chain Final', 370, 470);
  // rounds
  const rw = (W - 120 - 3 * 18) / 4;
  c.rounds.forEach((r, i) => {
    const [pastel, rim] = HUE[r.mode] ?? ['#e0f9d1', '#5cc22b'];
    const x = 60 + i * (rw + 18), y = 580;
    chunky(g, x, y, rw, 210, 28, pastel, INK, 8);
    g.fillStyle = rim; rr(g, x + 3, y + 3, rw - 6, 52, 25); g.fill();
    g.fillStyle = '#fff'; g.font = '30px "Lilita One"'; g.textAlign = 'center'; g.fillText(i === 3 ? 'FINAL' : `ROUND ${i + 1}`, x + rw / 2, y + 40);
    g.fillStyle = INK; g.font = '32px "Lilita One"'; g.fillText(r.map, x + rw / 2, y + 102);
    g.font = '22px "Varela Round"'; g.fillStyle = '#5f25bd'; g.fillText(r.mode, x + rw / 2, y + 138);
    g.fillStyle = INK; g.fillText(r.line, x + rw / 2, y + 176);
  });
  // verified strip
  chunky(g, 60, 840, W - 120, 120, 60, CREAM);
  const steps: [string, boolean | null][] = [['Rollup', c.rollup], ['Solana', c.solana], ['Replay', c.replay]];
  steps.forEach(([t, ok], i) => {
    const x = 130 + i * 310, y = 900;
    g.fillStyle = ok ? OK : ok === false ? PINK : '#c9c2dc'; g.beginPath(); g.arc(x, y, 30, 0, 7); g.fill();
    g.strokeStyle = '#fff'; g.lineWidth = 8; g.lineCap = 'round'; g.beginPath(); g.moveTo(x - 13, y + 1); g.lineTo(x - 3, y + 11); g.lineTo(x + 15, y - 10); g.stroke();
    g.fillStyle = INK; g.font = '38px "Lilita One"'; g.textAlign = 'left'; g.fillText(t + (ok ? ' ✓' : ok === false ? ' ✗' : ''), x + 46, y + 14);
    if (i < 2) { g.fillStyle = ok ? OK : '#c9c2dc'; rr(g, x + 195, y - 4, 70, 8, 4); g.fill(); }
  });
  // assets + facts
  g.textAlign = 'left'; g.fillStyle = INK; g.font = '30px "Lilita One"'; g.fillText('MINTED', 70, 1030);
  c.assets.slice(0, 4).forEach((a, i) => { capsule(g, 120 + i * 130, 1110, 44, a.color, a.crown); });
  if (!c.assets.length) { g.font = '26px "Varela Round"'; g.fillText('no asset for this match', 70, 1100); }
  g.font = '26px "Varela Round"'; g.fillStyle = INK; g.textAlign = 'right';
  g.fillText(`${c.sealed} sealed bet(s) · ${c.drops} Chaos Drop(s)`, W - 70, 1080); g.fillText(`pools ${c.pools} RUSH`, W - 70, 1120);
  g.font = '22px ui-monospace, Menlo, monospace'; g.fillText(c.match, W - 70, 1170);
  chunky(g, 270, 1220, 540, 80, 40, PINK, INK, 8);
  g.fillStyle = '#fff'; g.font = '40px "Lilita One"'; g.textAlign = 'center'; g.fillText('fallrush.fun', W / 2, 1275);
  return await new Promise((ok) => cv.toBlob((b) => ok(b!), 'image/png'));
}
