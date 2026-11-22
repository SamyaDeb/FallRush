// Burner wallet (persistent, localStorage) + ephemeral session keypair + optional Phantom (window.solana).
// Secrets never go to console/events/stats; only exportBurner() returns the burner secret.
import { Keypair, PublicKey } from '@solana/web3.js';
import { b58decode, b58encode } from './b58.ts';
import type { KV } from './storage.ts';

export interface StoredSession {
  v: 1;
  secret: string; // base58 64-byte secret key of the session signer
  authority: string | null; // who created the token (burner or Phantom)
  feePayer: string | null;
  validUntil: number; // unix seconds, 0 = not created on chain yet
  token: string | null; // SessionTokenV2 PDA
  createdMs: number;
}

export class Wallet {
  burner: Keypair;
  session: Keypair;
  sessionMeta: StoredSession;
  burnerCreated = false;

  private kv: KV;
  private burnerKey: string;
  private sessionKey: string;
  constructor(kv: KV, burnerKey: string, sessionKey: string, wallMs: () => number) {
    this.kv = kv; this.burnerKey = burnerKey; this.sessionKey = sessionKey;
    const raw = kv.getItem(burnerKey);
    let burner: Keypair | null = null;
    if (raw) {
      try { burner = Keypair.fromSecretKey(b58decode(raw)); } catch { burner = null; }
    }
    if (!burner) {
      burner = Keypair.generate();
      kv.setItem(burnerKey, b58encode(burner.secretKey));
      this.burnerCreated = true;
    }
    this.burner = burner;

    let meta: StoredSession | null = null;
    const sraw = kv.getItem(sessionKey);
    if (sraw) {
      try { meta = JSON.parse(sraw) as StoredSession; if (meta.v !== 1) meta = null; } catch { meta = null; }
    }
    let session: Keypair | null = null;
    if (meta) { try { session = Keypair.fromSecretKey(b58decode(meta.secret)); } catch { session = null; } }
    if (!session || !meta) {
      session = Keypair.generate();
      meta = { v: 1, secret: b58encode(session.secretKey), authority: null, feePayer: null, validUntil: 0, token: null, createdMs: wallMs() };
      kv.setItem(sessionKey, JSON.stringify(meta));
    }
    this.session = session;
    this.sessionMeta = meta;
  }

  exportBurner(): string { return b58encode(this.burner.secretKey); }

  /** Replace the burner (user pasted a secret). */
  importBurner(secretB58: string): void {
    const kp = Keypair.fromSecretKey(b58decode(secretB58));
    this.burner = kp;
    this.kv.setItem(this.burnerKey, b58encode(kp.secretKey));
  }

  saveSessionMeta(patch: Partial<StoredSession>): void {
    this.sessionMeta = { ...this.sessionMeta, ...patch };
    this.kv.setItem(this.sessionKey, JSON.stringify(this.sessionMeta));
  }

  /** New ephemeral session signer (after revoke / expiry). */
  rotateSession(wallMs: number): void {
    this.session = Keypair.generate();
    this.sessionMeta = { v: 1, secret: b58encode(this.session.secretKey), authority: null, feePayer: null, validUntil: 0, token: null, createdMs: wallMs };
    this.kv.setItem(this.sessionKey, JSON.stringify(this.sessionMeta));
  }
}

// ---- Phantom (optional; window.solana only) ----------------------------------------------------------------------
export interface PhantomLike {
  isPhantom?: boolean;
  isConnected?: boolean;
  publicKey?: { toBase58(): string; toBytes?(): Uint8Array } | null;
  connect(opts?: { onlyIfTrusted?: boolean }): Promise<{ publicKey: { toBase58(): string } }>;
  signMessage(msg: Uint8Array, display?: string): Promise<{ signature: Uint8Array }>;
  signTransaction?<T>(tx: T): Promise<T>;
}

export function getPhantom(): PhantomLike | null {
  const s = (globalThis as { solana?: PhantomLike }).solana;
  return s && typeof s.connect === 'function' ? s : null;
}

export function phantomState() {
  const p = getPhantom();
  return { available: !!p, connected: !!(p && p.isConnected && p.publicKey), pubkey: p && p.publicKey ? p.publicKey.toBase58() : null };
}

export async function phantomConnect(): Promise<string> {
  const p = getPhantom();
  if (!p) return JSON.stringify({ ok: false, error: 'Phantom not available' });
  try {
    const r = await p.connect();
    return JSON.stringify({ ok: true, pubkey: r.publicKey.toBase58() });
  } catch (e) {
    return JSON.stringify({ ok: false, error: String((e as Error)?.message ?? e) });
  }
}

export async function phantomSignMessage(text: string): Promise<string> {
  const p = getPhantom();
  if (!p) return JSON.stringify({ ok: false, error: 'Phantom not available' });
  try {
    const r = await p.signMessage(new TextEncoder().encode(text), 'utf8');
    return JSON.stringify({ ok: true, pubkey: p.publicKey ? p.publicKey.toBase58() : null, signature: b58encode(r.signature) });
  } catch (e) {
    return JSON.stringify({ ok: false, error: String((e as Error)?.message ?? e) });
  }
}

export function phantomPublicKey(): PublicKey | null {
  const p = getPhantom();
  return p && p.publicKey ? new PublicKey(p.publicKey.toBase58()) : null;
}
