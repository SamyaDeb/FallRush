/**
 * The story's shared timeline: chapter windows over the scroll progress (0..1) and the knockout schedule. The 3D
 * scene (StoryCanvas), the DOM copy (Story.tsx) and the "Rushlings left" counter all read these, so they agree.
 */
export type ChapterId = 'fall' | 'run' | 'goo' | 'star' | 'lid' | 'crown';

export interface ChapterSpan {
  id: ChapterId;
  a: number;
  b: number;
}

export const CHAPTERS: ChapterSpan[] = [
  { id: 'fall', a: 0.0, b: 0.15 },
  { id: 'run', a: 0.15, b: 0.33 },
  { id: 'goo', a: 0.33, b: 0.5 },
  { id: 'star', a: 0.5, b: 0.64 },
  { id: 'lid', a: 0.64, b: 0.85 },
  { id: 'crown', a: 0.85, b: 1.0 },
];

/** Half-width of the round-card wipe between two chapters (in story progress); the camera cuts at its middle. */
export const ZONE = 0.03;

/** Chapter-local progress 0..1 (0 and 1 are held through the fly-over zones). */
export function localP(p: number, c: ChapterSpan) {
  const a = c.a === 0 ? 0 : c.a + ZONE;
  const b = c.b === 1 ? 1 : c.b - ZONE;
  return p <= a ? 0 : p >= b ? 1 : (p - a) / (b - a);
}

export function chapterAt(p: number): number {
  for (let i = 0; i < CHAPTERS.length; i++) if (p < CHAPTERS[i].b) return i;
  return CHAPTERS.length - 1;
}

/**
 * The cast: 12 Rushlings, slot 0 (Bolt) takes the crown. `out` = [chapter, local progress] of the knockout.
 * Round 1 Capsule Run 12 → 8, Round 2 Goo Tower 8 → 5, Round 3 Star Fountain 5 → 3, Final Lid Flip 3 → 1.
 */
export const KNOCKOUTS: Record<number, [ChapterId, number]> = {
  11: ['run', 0.3],
  10: ['run', 0.52],
  9: ['run', 0.58],
  8: ['run', 0.8],
  7: ['goo', 0.38],
  6: ['goo', 0.58],
  5: ['goo', 0.76],
  4: ['star', 0.5],
  3: ['star', 0.76],
  2: ['lid', 0.5],
  1: ['lid', 0.74],
};

const chapterIndex = (id: ChapterId) => CHAPTERS.findIndex((c) => c.id === id);

/** Is slot `i` knocked out at story progress p? */
export function isOut(i: number, p: number) {
  const k = KNOCKOUTS[i];
  if (!k) return false;
  const c = CHAPTERS[chapterIndex(k[0])];
  const ci = chapterAt(p);
  if (ci > chapterIndex(k[0])) return true;
  if (ci < chapterIndex(k[0])) return false;
  return localP(p, c) >= k[1];
}

export function aliveAt(p: number) {
  let n = 12;
  for (let i = 0; i < 12; i++) if (isOut(i, p)) n--;
  return n;
}

export const ROUND_LABEL: Record<ChapterId, string> = {
  fall: 'Drop in',
  run: 'Round 1 · Race',
  goo: 'Round 2 · Survival',
  star: 'Round 3 · Skill',
  lid: 'The Final · on-chain',
  crown: 'Champion',
};

/** The round cards that wipe across between chapters (index = the chapter being entered). */
export const ROUND_CARD: Partial<Record<ChapterId, { eyebrow: string; title: string; line: string; theme: 'pink' | 'sun' | 'night' | 'gold' }>> = {
  run: { eyebrow: 'ROUND 1 · RACE', title: 'Capsule Run', line: '12 start · 8 go through', theme: 'pink' },
  goo: { eyebrow: 'ROUND 2 · SURVIVAL', title: 'Goo Tower', line: '8 climb · 5 make the summit', theme: 'sun' },
  star: { eyebrow: 'ROUND 3 · SKILL', title: 'Star Fountain', line: '5 scramble · 3 reach the Final', theme: 'pink' },
  lid: { eyebrow: 'THE FINAL · ON-CHAIN', title: 'Lid Flip', line: '3 left · 1 crown · computed on-chain, tick by tick', theme: 'night' },
  crown: { eyebrow: 'AND THE CROWN GOES TO…', title: 'Bolt!', line: 'champion of show #1', theme: 'gold' },
};
