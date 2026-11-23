// Mirrors client-unity/Assets/_Game/Scripts/Shared/CosmeticPresets.cs (looks only, no stats).
export type Emblem = 'bolt' | 'star' | 'heart' | 'flame' | 'diamond' | 'moon';
export type Topper = 'goggles' | 'propeller' | 'chef' | 'headphones';
export type KeyStyle = 'classic' | 'star' | 'gear';

export interface Preset {
  name: string;
  shell: string;
  emblem: Emblem;
  emblemColor: string;
  topper: Topper;
  face: string; // LED glyph colour (face theme)
  key: KeyStyle;
  keyColor: string;
  personality: string;
}

// Face themes (RushlingParts.FaceTint; the landing page uses the brighter mint #5BF0B5 from the brief).
const MINT = '#5BF0B5', CYAN = '#58E1FF', AMBER = '#FFB547', PINK = '#FF7AC6';

export const PRESETS: Preset[] = [
  { name: 'Bolt', shell: '#FF3EA5', emblem: 'bolt', emblemColor: '#FFD21F', topper: 'goggles', face: MINT, key: 'classic', keyColor: '#7B2FF7', personality: 'Goggles down, first off the line.' },
  { name: 'Splash', shell: '#3FA9F5', emblem: 'star', emblemColor: '#FFFFFF', topper: 'propeller', face: CYAN, key: 'star', keyColor: '#FFD21F', personality: 'Spins up the propeller and dives into the chaos.' },
  { name: 'Sunny', shell: '#FFD21F', emblem: 'heart', emblemColor: '#FF3EA5', topper: 'chef', face: PINK, key: 'classic', keyColor: '#FF3EA5', personality: 'Chef hat on, sunny screen, zero fear of mallets.' },
  { name: 'Minty', shell: '#5BE3B5', emblem: 'diamond', emblemColor: '#FFFFFF', topper: 'headphones', face: MINT, key: 'gear', keyColor: '#7B2FF7', personality: 'Headphones on. Hears the hazard before it hits.' },
  { name: 'Grape', shell: '#7B2FF7', emblem: 'moon', emblemColor: '#FFD21F', topper: 'goggles', face: AMBER, key: 'star', keyColor: '#FFD21F', personality: 'Moon emblem, night-shift goggles, sneaky fast.' },
  // Non-playable extras (bots / background racers).
  { name: 'Peach', shell: '#FF8A3D', emblem: 'flame', emblemColor: '#FFFFFF', topper: 'propeller', face: CYAN, key: 'classic', keyColor: '#7B2FF7', personality: '' },
  { name: 'Lagoon', shell: '#2EC4B6', emblem: 'bolt', emblemColor: '#FFFFFF', topper: 'chef', face: PINK, key: 'gear', keyColor: '#FF3EA5', personality: '' },
];

export const PLAYABLE = PRESETS.slice(0, 5);

export const TOPPER_NAME: Record<Topper, string> = {
  goggles: 'goggles',
  propeller: 'propeller cap',
  chef: 'chef hat',
  headphones: 'headphones',
};

export const kitLine = (p: Preset) => `${cap(p.emblem)} emblem · ${TOPPER_NAME[p.topper]} · ${p.key === 'classic' ? 'classic' : p.key} key`;

const cap = (s: string) => s[0].toUpperCase() + s.slice(1);

export const presetByName = (name: string | undefined | null) =>
  PRESETS.find((p) => p.name.toLowerCase() === String(name || '').toLowerCase()) ?? null;
