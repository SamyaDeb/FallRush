/**
 * Round qualification targets from player count P (GDD §4.1, ruleset_v1.json).
 * Integer-only so the on-chain derivation (`programs/fall_rush/src/state.rs::round_targets`)
 * is bit-identical: ceil(a * pct / 100) = (a * pct + 99) / 100.
 */
export const MIN_PLAYERS = 4;
export const MAX_PLAYERS = 20;

export function roundTargets(p: number): [number, number, number, number] {
  if (!Number.isInteger(p) || p < MIN_PLAYERS || p > MAX_PLAYERS) throw new Error(`player count out of range: ${p}`);
  const r1 = Math.floor((p * 66 + 99) / 100);
  const r2 = Math.floor((r1 * 62 + 99) / 100);
  const r3 = Math.min(3, r2 - 1);
  return [r1, r2, r3, 1];
}
