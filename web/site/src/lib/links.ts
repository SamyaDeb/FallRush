/**
 * Where the landing sends people. On the VPS (the game URL's front door) the game lives on the same origin, so
 * VITE_PLAY_URL is empty; the Cloudflare build (waitlist.fallrush.fun) sets it to the game URL.
 */
const PLAY_BASE = String(import.meta.env.VITE_PLAY_URL ?? '').replace(/\/$/, '');

/** The game (the separate /play page: Unity WebGL). */
export const PLAY_URL = PLAY_BASE + '/play';
/** Watch a live arena. */
export const LIVE_URL = PLAY_BASE + '/live';
/** The arenas dashboard (the old game home). */
export const ARENAS_URL = PLAY_BASE + '/arenas';
export const receiptUrl = (match: string) => `${PLAY_BASE}/receipt/${match}`;
export const replayUrl = (match: string) => `${PLAY_BASE}/replay/${match}`;

// Devnet ids shown in the on-chain chapter (same as web/app/src/lib/net.ts).
export const PROGRAM = '4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4';
export const VALIDATOR = 'MAS1Dt9qreoRMQ14YQuhg8UTZMMzDdKhmkZMECCzk57';
export const RUSH_MINT = '3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw';
export const explorer = (kind: 'address' | 'tx', id: string) => `https://explorer.solana.com/${kind}/${id}?cluster=devnet`;
