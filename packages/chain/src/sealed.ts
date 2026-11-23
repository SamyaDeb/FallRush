// P6 sealed Champion bets in the browser bundle (WebGL game + /live): the pick goes to the arena's private book on
// MagicBlock's TEE rollup (Private Ephemeral Rollup: nobody can read it until close), the stake + a sha256 commitment
// to the public rollup (devnet-as). The salt is kept in localStorage (and in the key backup) so the bettor can reveal
// it themselves if the book's reveal ever fails (commit-reveal fallback). programs/fall_rush/src/p6.rs.
import { PublicKey, Transaction, TransactionInstruction } from '@solana/web3.js';
import type { AccountMeta, Keypair } from '@solana/web3.js';
import { sha256 } from '@noble/hashes/sha256';
import { ed25519 } from '@noble/curves/ed25519';
import { b58encode } from './b58.ts';
import type { KV } from './storage.ts';

/** sha256("global:p6")[0..8] (IDL). */
export const IX_P6 = Uint8Array.from([49, 80, 216, 3, 131, 169, 74, 12]);
export const OP6 = { COMMIT: 3, PICK: 5, REVEAL: 8 } as const;
export const DEFAULT_TEE = 'https://devnet-tee.magicblock.app';
const te = new TextEncoder();

export function sealedPdas(program: PublicKey, arena: PublicKey) {
  const P = (s: Uint8Array[]) => PublicKey.findProgramAddressSync(s, program)[0];
  return { escrow: P([te.encode('sealed'), arena.toBytes()]), book: P([te.encode('sbook'), arena.toBytes()]) };
}

export function p6Ix(program: PublicKey, signer: PublicKey, op: number, a: bigint | number, b: bigint | number, data: Uint8Array, accs: AccountMeta[]): TransactionInstruction {
  const d = new Uint8Array(8 + 1 + 16 + 32);
  d.set(IX_P6, 0);
  d[8] = op;
  const dv = new DataView(d.buffer);
  dv.setBigUint64(9, BigInt(a), true);
  dv.setBigUint64(17, BigInt(b), true);
  d.set(data.subarray(0, 32), 25);
  return new TransactionInstruction({ programId: program, keys: [{ pubkey: signer, isSigner: true, isWritable: true }, ...accs], data: Buffer.from(d) });
}

/** sha256(outcome ‖ amount_le ‖ salt ‖ wallet) = p6::commitment. */
export function commitment(outcome: number, amount: bigint, salt: Uint8Array, wallet: PublicKey): Uint8Array {
  const b = new Uint8Array(1 + 8 + 16 + 32);
  b[0] = outcome;
  new DataView(b.buffer).setBigUint64(1, amount, true);
  b.set(salt, 9);
  b.set(wallet.toBytes(), 25);
  return sha256(b);
}

const rdU64 = (d: Uint8Array, o: number) => new DataView(d.buffer, d.byteOffset, d.byteLength).getBigUint64(o, true);
const hex = (a: Uint8Array) => Array.from(a, (x) => x.toString(16).padStart(2, '0')).join('');
export function decodeEscrow(d: Uint8Array) {
  const n = d[8 + 88];
  const entries = [];
  for (let i = 0; i < n; i++) { const o = 8 + 96 + 80 * i; entries.push({ i, wallet: new PublicKey(d.subarray(o, o + 32)).toBase58(), amount: rdU64(d, o + 32), hash: hex(d.subarray(o + 40, o + 72)), outcome: d[o + 72], state: d[o + 73] }); }
  return { match: new PublicKey(d.subarray(40, 72)).toBase58(), closedAt: Number(new DataView(d.buffer, d.byteOffset).getBigInt64(8 + 64, true)), pool: rdU64(d, 8 + 72), forfeit: rdU64(d, 8 + 80), n, nRevealed: d[8 + 89], state: d[8 + 90], nForfeit: d[8 + 91], entries };
}

// ---- the salts (localStorage: fr.sealed.<wallet> = [{match, outcome, amount, salt, hash, at}]) ---------------------
export interface SavedPick { match: string; outcome: number; amount: string; salt: string; hash: string; at: number; pick?: string; commit?: string }
export const sealedKey = (wallet: string) => `fr.sealed.${wallet}`;
export function savedPicks(kv: KV, wallet: string): SavedPick[] { try { return JSON.parse(kv.getItem(sealedKey(wallet)) ?? '[]'); } catch { return []; } }
export function savePick(kv: KV, wallet: string, p: SavedPick) {
  const xs = savedPicks(kv, wallet).filter((x) => x.hash !== p.hash);
  xs.push(p);
  kv.setItem(sealedKey(wallet), JSON.stringify(xs.slice(-40)));
}

// ---- TEE (private rollup) auth: the session key signs the challenge (no popup) ------------------------------------
const tokens = new Map<string, { token: string; exp: number }>();
export async function teeToken(teeUrl: string, kp: Keypair): Promise<string> {
  const k = kp.publicKey.toBase58();
  const c = tokens.get(k);
  if (c && c.exp > Date.now() + 60_000) return c.token;
  const ch = await (await fetch(`${teeUrl}/auth/challenge?pubkey=${k}`)).json() as { challenge?: string; error?: string };
  if (!ch.challenge) throw new Error('TEE challenge: ' + (ch.error ?? 'none'));
  const sig = ed25519.sign(te.encode(ch.challenge), kp.secretKey.subarray(0, 32));
  const r = await fetch(`${teeUrl}/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ pubkey: k, challenge: ch.challenge, signature: b58encode(sig) }) });
  const j = await r.json() as { token?: string; expiresAt?: number; error?: string };
  if (r.status !== 200 || !j.token) throw new Error('TEE login: ' + (j.error ?? r.status));
  tokens.set(k, { token: j.token, exp: j.expiresAt ?? Date.now() + 86_400_000 });
  return j.token;
}

async function rpc(url: string, method: string, params: unknown[]) {
  const r = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) });
  const j = await r.json() as { result?: any; error?: { message?: string } };
  if (j.error) throw new Error(j.error.message ?? 'rpc error');
  return j.result;
}

/** Sends one instruction to the TEE rollup signed by `kp` (fee payer: its rollup-cloned lamports), confirms by polling. */
export async function sendTee(teeUrl: string, kp: Keypair, ix: TransactionInstruction): Promise<{ ok: boolean; sig: string; ms: number; err?: unknown }> {
  const t0 = Date.now();
  const url = `${teeUrl}?token=${await teeToken(teeUrl, kp)}`;
  let last: unknown = '';
  for (let n = 0; n < 6; n++) {
    try {
      const bh = (await rpc(url, 'getLatestBlockhash', [{ commitment: 'processed' }])).value.blockhash as string;
      const tx = new Transaction({ feePayer: kp.publicKey, recentBlockhash: bh }).add(ix);
      tx.sign(kp);
      const sig = await rpc(url, 'sendTransaction', [Buffer.from(tx.serialize()).toString('base64'), { encoding: 'base64', skipPreflight: true }]) as string;
      for (let i = 0; i < 75; i++) {
        const st = (await rpc(url, 'getSignatureStatuses', [[sig]])).value[0];
        if (st) return st.err ? { ok: false, sig, ms: Date.now() - t0, err: st.err } : { ok: true, sig, ms: Date.now() - t0 };
        await new Promise((r) => setTimeout(r, 200));
      }
      return { ok: false, sig, ms: Date.now() - t0, err: 'unconfirmed (check the book before retrying)' };
    } catch (e) { last = String((e as Error)?.message ?? e); await new Promise((r) => setTimeout(r, 600)); }
  }
  return { ok: false, sig: '', ms: Date.now() - t0, err: last };
}

/** Optional: the TEE's attestation, verified in this browser by `fallrush-tee.js` (Intel TDX quote via Phala's PCCS;
 * loaded on demand next to the bundle, `window.FallRushTee.verify(url)`). */
export async function attest(teeUrl: string, base: string): Promise<{ ok: boolean; ms: number; error?: string }> {
  const t0 = Date.now();
  try {
    const g = globalThis as { FallRushTee?: { verify(u: string): Promise<void> }; document?: any };
    if (!g.FallRushTee && g.document) {
      await new Promise<void>((res, rej) => { const s = g.document.createElement('script'); s.src = base + 'fallrush-tee.js'; s.onload = () => res(); s.onerror = () => rej(new Error('fallrush-tee.js not loaded')); g.document.head.appendChild(s); });
    }
    if (!g.FallRushTee) throw new Error('attestation module unavailable');
    await g.FallRushTee.verify(teeUrl);
    return { ok: true, ms: Date.now() - t0 };
  } catch (e) { return { ok: false, ms: Date.now() - t0, error: String((e as Error)?.message ?? e) }; }
}
