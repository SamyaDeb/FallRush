/**
 * Device quality tier, decided once per page load.
 *  - low:  phones / tablets (coarse pointer), small screens, or weak machines (≤ 4 cores or ≤ 4 GB memory hint)
 *  - high: laptops and desktops
 * AdaptiveDpr (common.tsx) still steps resolution down at runtime if frames get slow on either tier.
 */
export type Tier = 'low' | 'high';

export interface Quality {
  tier: Tier;
  /** [min, max] device pixel ratio for the canvas. */
  dpr: [number, number];
  shadows: boolean;
  shadowMap: number;
  islands: number;
  clouds: number;
  confetti: number;
  antialias: boolean;
}

function detect(): Quality {
  if (typeof window === 'undefined') return HIGH;
  const coarse = window.matchMedia?.('(pointer: coarse)').matches ?? false;
  const small = Math.min(window.innerWidth, window.innerHeight) < 600;
  const nav = navigator as Navigator & { deviceMemory?: number };
  const weak = (nav.hardwareConcurrency || 8) <= 4 || (nav.deviceMemory || 8) <= 4;
  return coarse || small || weak ? LOW : HIGH;
}

const HIGH: Quality = { tier: 'high', dpr: [1, 1.75], shadows: true, shadowMap: 1024, islands: 22, clouds: 12, confetti: 160, antialias: true };
const LOW: Quality = { tier: 'low', dpr: [1, 1.5], shadows: false, shadowMap: 512, islands: 12, clouds: 7, confetti: 70, antialias: true };

export const QUALITY: Quality = detect();
