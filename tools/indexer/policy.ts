// harden: the indexer's pure decisions (no RPC, no side effects at import), unit-tested in policy.test.ts:
//   node --experimental-strip-types --no-warnings --test tools/indexer/policy.test.ts
export const PUBLIC_PHASES = new Set(["LobbyOpen", "Countdown"]);
/** The game server alone looks free: up, public, empty lobby. */
export const serverIdle = (sv: any) => !!(sv.up && sv.url && !sv.room && !sv.inMatch && PUBLIC_PHASES.has(sv.phase) && (sv.humans ?? 0) === 0);
/** harden #6: an arena server's keeper must be idle too (its post-match tail, or a chain match the server dropped,
 * still holds the arena while the server already shows an empty lobby). Classic-only servers have no keeper. */
export const keeperIdle = (sv: any) => sv.arena == null || !!sv.classic || (!!sv.keeper?.alive && sv.keeper.stage === "idle");
export const idle = (sv: any) => serverIdle(sv) && keeperIdle(sv);

export type Low = { authority: number; treasury: number; funder: number };
export const LOW_DEFAULTS: Low = { authority: 300_000_000, treasury: 100_000_000, funder: 200_000_000 };
/** harden #1: low-balance flags (a known balance below its threshold; -1 / null = unknown, never low). */
export function lowFlags(b: { authorityLamports: number | null; treasuryLamports: number | null; funderLamports: number | null }, t: Low = LOW_DEFAULTS) {
  const low = (v: number | null, th: number) => v != null && v >= 0 && v < th;
  const f = { authority: low(b.authorityLamports, t.authority), treasury: low(b.treasuryLamports, t.treasury), funder: low(b.funderLamports, t.funder) };
  return { ...f, any: f.authority || f.treasury || f.funder };
}
/** harden #2: the season board is paused when any arena keeper says so (commit cap) or runs with SEASONS=0. */
export function seasonPause(keepers: any[]): { paused: boolean; reason: string | null } {
  const k = keepers.find((x) => x && (x.seasonsPaused === true || x.seasons === false));
  return { paused: !!k, reason: k ? (k.seasonsPausedReason ?? "paused") : null };
}
