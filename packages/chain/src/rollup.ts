// Rollup transport: web3.js Connection to the MagicBlock ER (devnet-as) + a base-layer Connection.
import { ComputeBudgetProgram, Keypair, PublicKey, SystemProgram, Transaction } from '@solana/web3.js';
import type { TransactionInstruction } from '@solana/web3.js';
import type { Ctx, JoinArgs, JoinResult, Transport } from './transport.ts';
import { Backoff, errMsg } from './transport.ts';
import type { Clock } from './clock.ts';
import { sleep } from './clock.ts';
import { TokenBucket } from './throttle.ts';
import { buildInstruction, resolvePubkey } from './ix.ts';
import type { Refs } from './ix.ts';
import { createSessionV2Ix, decodeSessionTokenV2, isExpired, MAX_VALIDITY_SEC, revokeSessionV2Ix, sessionTokenV2Pda } from './session.ts';
import { parseWorldAccount, validateInputBatch } from './wire.ts';
import { getPhantom, phantomPublicKey } from './wallet.ts';
import { b58encode } from './b58.ts';

/** The subset of web3.js Connection we use (so tests can pass a mock). */
export interface ConnLike {
  getLatestBlockhash(commitment?: string): Promise<{ blockhash: string; lastValidBlockHeight: number }>;
  sendRawTransaction(raw: Uint8Array | Buffer, opts?: { skipPreflight?: boolean; maxRetries?: number; preflightCommitment?: string }): Promise<string>;
  getSignatureStatuses(sigs: string[]): Promise<{ value: ({ err: unknown; confirmationStatus?: string | null; slot?: number } | null)[] }>;
  onAccountChange(pk: PublicKey, cb: (info: { data: Uint8Array }, ctx: { slot: number }) => void, commitment?: string): number;
  removeAccountChangeListener(id: number): Promise<void>;
  getAccountInfo(pk: PublicKey, commitment?: string): Promise<{ data: Uint8Array; lamports?: number } | null>;
  getSlot(commitment?: string): Promise<number>;
  getBalance(pk: PublicKey, commitment?: string): Promise<number>;
}
export type ConnFactory = (http: string, ws: string | null | undefined, commitment: string) => ConnLike;

export function isClonerError(e: unknown): boolean {
  const s = typeof e === 'string' ? e : errMsg(e) + ' ' + safeJson(e);
  return /pending request owner|Cloner/i.test(s);
}
function safeJson(v: unknown): string { try { return JSON.stringify(v) ?? ''; } catch { return ''; } }

interface Pending { txid: number; sentMs: number; kind: string; resolve?: (r: { ok: boolean; err?: unknown; ms: number }) => void; timeoutMs: number }

/** One poller per connection: getSignatureStatuses in batches every `everyMs`, one request in flight. */
export class ConfirmPoller {
  private pending = new Map<string, Pending>();
  private inFlight = false;
  private timer: unknown = null;
  private conn: () => ConnLike;
  private ctx: Ctx;
  private batch: number;
  private label: string;
  calls = 0;
  constructor(conn: () => ConnLike, ctx: Ctx, everyMs: number, batch: number, label: string) {
    this.conn = conn; this.ctx = ctx; this.batch = Math.min(256, batch); this.label = label;
    this.timer = ctx.clock.setInterval(() => { void this.tick(); }, everyMs);
  }
  get size() { return this.pending.size; }
  track(sig: string, p: Pending): void { this.pending.set(sig, p); }
  wait(sig: string, txid: number, kind: string, timeoutMs: number): Promise<{ ok: boolean; err?: unknown; ms: number }> {
    return new Promise((resolve) => this.track(sig, { txid, sentMs: this.ctx.clock.now(), kind, resolve, timeoutMs }));
  }
  async tick(): Promise<void> {
    if (this.inFlight || !this.pending.size) return;
    const now = this.ctx.clock.now();
    for (const [sig, p] of this.pending) {
      if (now - p.sentMs > p.timeoutMs) {
        this.pending.delete(sig);
        this.ctx.stats.confirmTimeouts++;
        this.ctx.events.push({ type: 'confirm', txid: p.txid, kind: p.kind, status: 'timeout', ms: null, layer: this.label });
        p.resolve?.({ ok: false, err: 'timeout', ms: now - p.sentMs });
      }
    }
    const sigs = [...this.pending.keys()].slice(0, this.batch);
    if (!sigs.length) return;
    this.inFlight = true;
    this.calls++;
    try {
      const r = await this.conn().getSignatureStatuses(sigs);
      const t = this.ctx.clock.now();
      sigs.forEach((sig, i) => {
        const p = this.pending.get(sig);
        if (!p) return;
        const st = r.value[i];
        this.pending.delete(sig);
        if (!st) { this.pending.set(sig, p); return; } // not seen yet: rotate to the back
        const ms = Math.round((t - p.sentMs) * 10) / 10;
        const ok = st.err == null;
        if (ok) { this.ctx.stats.confirmOk++; if (p.kind === 'input') this.ctx.stats.addConfirm(ms); } else this.ctx.stats.confirmBad++;
        this.ctx.events.push({ type: 'confirm', txid: p.txid, kind: p.kind, status: ok ? 'ok' : 'err', err: ok ? undefined : safeJson(st.err), ms, layer: this.label });
        p.resolve?.({ ok, err: st.err ?? undefined, ms });
      });
    } catch (e) {
      this.ctx.events.push({ type: 'error', where: 'confirmPoll', layer: this.label, why: errMsg(e) });
    } finally {
      this.inFlight = false;
    }
  }
  stop() { this.ctx.clock.clearInterval(this.timer); }
}

interface Sub { addr: string; pk: PublicKey; id: number | null; lastWsMs: number; nextResubMs: number; stalls: number; polling: boolean }

export class RollupTransport implements Transport {
  readonly kind = 'rollup' as const;
  private ctx: Ctx;
  private factory: ConnFactory;
  er: ConnLike;
  base: ConnLike;
  bucket: TokenBucket;
  erPoller: ConfirmPoller;
  basePoller: ConfirmPoller;
  private blockhash: string | null = null;
  private baseBlockhash: { v: string; at: number } | null = null;
  private txid = 0;
  private subs = new Map<string, Sub>();
  private timers: unknown[] = [];
  private stopped = false;
  private backoff: Backoff;
  private erGen = 0;
  private joined: JoinResult | null = null;
  private clock: Clock;

  constructor(ctx: Ctx, factory: ConnFactory) {
    this.ctx = ctx; this.factory = factory; this.clock = ctx.clock;
    const r = this.rc;
    const cm = r.commitment ?? 'processed';
    this.er = factory(r.erHttp, r.erWs, cm);
    this.base = factory(r.baseHttp, r.baseWs, 'confirmed');
    this.bucket = new TokenBucket(Math.min(1.5, r.baseTxPerSec ?? 1.5), r.baseBurst ?? 1, ctx.clock);
    this.erPoller = new ConfirmPoller(() => this.er, ctx, r.confirmPollMs ?? 50, r.confirmBatch ?? 256, 'er');
    this.basePoller = new ConfirmPoller(() => this.base, ctx, 500, 64, 'base');
    this.backoff = new Backoff(r.stallMs ?? 2000, 30000);
  }

  private get rc() { return this.ctx.cfg.rollup!; }
  private get cm() { return this.rc.commitment ?? 'processed'; }

  async start(): Promise<void> {
    await this.refreshBlockhash();
    const c = this.clock;
    this.timers.push(c.setInterval(() => { void this.refreshBlockhash(); }, this.rc.blockhashRefreshMs ?? 10000));
    this.timers.push(c.setInterval(() => { void this.pollAccounts(); }, this.rc.accountPollMs ?? 1000));
    this.timers.push(c.setInterval(() => { void this.rttProbe(); }, this.rc.rttPollMs ?? 2000));
    void this.rttProbe();
  }

  private async refreshBlockhash(): Promise<void> {
    try { this.blockhash = (await this.er.getLatestBlockhash(this.cm)).blockhash; } catch (e) { this.ctx.events.push({ type: 'error', where: 'erBlockhash', why: errMsg(e) }); }
  }

  private async rttProbe(): Promise<void> {
    const t0 = this.clock.now();
    try { await this.er.getSlot(this.cm); this.ctx.stats.addRtt(this.clock.now() - t0); } catch { /* counted by stalls */ }
  }

  // ---- refs for config-driven instructions -----------------------------------------------------------------------
  refs(extra: Refs = {}): Refs {
    const w = this.ctx.wallet;
    const cfg = this.ctx.cfg;
    const r: Refs = { ...cfg.addresses };
    const authority = w.sessionMeta.authority ? new PublicKey(w.sessionMeta.authority) : w.burner.publicKey;
    r.burner = w.burner.publicKey;
    r.authority = authority;
    r.session = w.session.publicKey;
    r.systemProgram = SystemProgram.programId;
    r.sessionProgram = new PublicKey(cfg.session.programId);
    const target = resolvePubkey(cfg.session.targetProgram, r);
    r.targetProgram = target;
    r.sessionToken = sessionTokenV2Pda(r.sessionProgram as PublicKey, target, w.session.publicKey, authority);
    if (this.joined && this.joined.slot !== undefined) r.slot = this.joined.slot;
    return { ...r, ...extra };
  }

  private signersFor(ixs: TransactionInstruction[], external: PublicKey | null = null): Keypair[] {
    const w = this.ctx.wallet;
    const out: Keypair[] = [];
    for (const ix of ixs) for (const k of ix.keys) {
      if (!k.isSigner || (external && k.pubkey.equals(external))) continue;
      const kp = k.pubkey.equals(w.session.publicKey) ? w.session : k.pubkey.equals(w.burner.publicKey) ? w.burner : null;
      if (!kp) throw new Error('no local key for signer ' + k.pubkey.toBase58());
      if (!out.includes(kp)) out.push(kp);
    }
    return out;
  }

  /** Build + sign an ER transaction; fee payer = session key (ER transactions are gasless/cheap). Synchronous. */
  private buildErTx(ixs: TransactionInstruction[]): { raw: Uint8Array; sig: string } {
    if (!this.blockhash) throw new Error('no ER blockhash yet');
    const w = this.ctx.wallet;
    const tx = new Transaction({ feePayer: w.session.publicKey, recentBlockhash: this.blockhash });
    if (this.rc.computeUnitLimit) tx.add(ComputeBudgetProgram.setComputeUnitLimit({ units: this.rc.computeUnitLimit }));
    tx.add(...ixs);
    const signers = this.signersFor(ixs);
    if (!signers.includes(w.session)) signers.unshift(w.session);
    tx.sign(...signers);
    return { raw: tx.serialize(), sig: b58encode(tx.signature!) };
  }

  /** sendRawTransaction with the P0 quirk-7 retry ("pending request owner … Cloner") until `retryMs` elapsed. */
  private async sendWithClonerRetry(conn: ConnLike, raw: Uint8Array, retryMs: number, skipPreflight: boolean): Promise<string> {
    const t0 = this.clock.now();
    let tries = 0;
    for (;;) {
      try { return await conn.sendRawTransaction(raw, { skipPreflight, maxRetries: 0 }); } catch (e) {
        tries++;
        if (!isClonerError(e) || this.clock.now() - t0 + (this.rc.clonerRetryEveryMs ?? 250) > retryMs) throw e;
        if (tries === 1) this.ctx.events.push({ type: 'clonerRetry', note: 'rollup still cloning; retrying' });
        await sleep(this.clock, this.rc.clonerRetryEveryMs ?? 250);
      }
    }
  }

  sendInputs(batch: Uint8Array): number {
    const id = (this.txid = (this.txid + 1) >>> 0 || 1);
    const bad = validateInputBatch(batch);
    const fail = (why: string) => { this.ctx.stats.sendErrors++; this.ctx.events.push({ type: 'error', where: 'sendInputs', txid: id, why }); };
    if (bad) { fail(bad); return id; }
    let built: { raw: Uint8Array; sig: string };
    try { built = this.buildErTx([buildInstruction(this.rc.submitInput, this.refs({ batch, slot: batch[0] }))]); } catch (e) { fail(errMsg(e)); return id; }
    const now = this.clock.now();
    this.ctx.stats.inputs.hit(now);
    this.erPoller.track(built.sig, { txid: id, sentMs: now, kind: 'input', timeoutMs: this.rc.confirmTimeoutMs ?? 5000 });
    this.sendWithClonerRetry(this.er, built.raw, this.rc.inputClonerRetryMs ?? 300, true).catch((e) => fail(errMsg(e)));
    return id;
  }

  // ---- subscription with backup poll + stall watchdog -------------------------------------------------------------
  subscribe(addr: string): void {
    if (this.subs.has(addr)) return;
    let pk: PublicKey;
    try { pk = resolvePubkey(addr, this.refs()); } catch (e) { this.ctx.events.push({ type: 'error', where: 'subscribe', why: errMsg(e) }); return; }
    if (!this.subs.size) this.ctx.intake.reset();
    const s: Sub = { addr, pk, id: null, lastWsMs: this.clock.now(), nextResubMs: 0, stalls: 0, polling: false };
    this.subs.set(addr, s);
    this.attach(s);
    void this.pollOne(s);
  }

  private attach(s: Sub) {
    const gen = this.erGen;
    s.id = this.er.onAccountChange(s.pk, (info) => {
      if (gen !== this.erGen || !this.subs.has(s.addr)) return;
      s.lastWsMs = this.clock.now();
      if (s.stalls) { s.stalls = 0; this.backoff.reset(); }
      this.ctx.intake.ingest(toU8(info.data), 'ws');
    }, this.cm);
  }

  private detach(s: Sub) {
    if (s.id !== null) { const id = s.id; s.id = null; this.er.removeAccountChangeListener(id).catch(() => { /* old socket */ }); }
  }

  unsubscribe(addr: string): void {
    const s = this.subs.get(addr);
    if (!s) return;
    this.detach(s);
    this.subs.delete(addr);
  }

  private async pollOne(s: Sub): Promise<void> {
    if (s.polling) return;
    s.polling = true;
    try {
      this.ctx.stats.polls++;
      const info = await this.er.getAccountInfo(s.pk, this.cm);
      if (info && this.subs.has(s.addr)) this.ctx.intake.ingest(toU8(info.data), 'poll');
    } catch (e) {
      this.ctx.events.push({ type: 'error', where: 'accountPoll', why: errMsg(e) });
    } finally { s.polling = false; }
  }

  async pollAccounts(): Promise<void> {
    const now = this.clock.now();
    for (const s of this.subs.values()) {
      void this.pollOne(s);
      const stall = this.rc.stallMs ?? 2000;
      if (now - s.lastWsMs > stall && now >= s.nextResubMs) this.recover(s, now);
    }
  }

  /** WS silent: re-subscribe; after 3 consecutive stalls rebuild the ER Connection (new socket). */
  private recover(s: Sub, now: number) {
    s.stalls++;
    s.nextResubMs = now + this.backoff.next();
    this.ctx.stats.reconnects++;
    if (s.stalls >= 3) {
      this.erGen++;
      for (const x of this.subs.values()) this.detach(x);
      const r = this.rc;
      this.er = this.factory(r.erHttp, r.erWs, this.cm);
      for (const x of this.subs.values()) { x.stalls = 0; this.attach(x); }
      this.ctx.events.push({ type: 'wsReconnect', transport: 'rollup', how: 'newConnection' });
      void this.refreshBlockhash();
    } else {
      this.detach(s);
      this.attach(s);
      this.ctx.events.push({ type: 'wsReconnect', transport: 'rollup', how: 'resubscribe', silentMs: Math.round(now - s.lastWsMs) });
    }
  }

  // ---- join ----------------------------------------------------------------------------------------------------
  async join(args: JoinArgs): Promise<JoinResult> {
    const j = this.rc.join || {};
    const cfg = this.ctx.cfg;
    try {
      if (j.ix) {
        const ix = buildInstruction(j.ix, this.refs({ name: args.name ?? 'player', slot: args.slot ?? 0 }));
        const layer = j.layer ?? 'er';
        let sig: string;
        if (layer === 'er') {
          const b = this.buildErTx([ix]);
          await this.sendWithClonerRetry(this.er, b.raw, this.rc.clonerRetryMs ?? 5000, false);
          sig = b.sig;
          const r = await this.erPoller.wait(sig, 0, 'join', j.confirmTimeoutMs ?? 15000);
          if (!r.ok) return { ok: false, error: 'join tx failed: ' + safeJson(r.err) };
        } else {
          const r = await this.sendBase([ix], 'join');
          if (!r.ok) return { ok: false, error: 'join tx failed: ' + safeJson(r.err) };
        }
      }
      let slot = args.slot;
      let roster: unknown[] = [];
      if (j.slotTable) {
        const t = j.slotTable;
        const refs = this.refs();
        const info = await this.er.getAccountInfo(resolvePubkey(t.account, refs), this.cm);
        if (!info) return { ok: false, error: 'slot table account not found' };
        const data = toU8(info.data);
        const me = (t.key === 'session' ? this.ctx.wallet.session.publicKey : (refs.authority as PublicKey)).toBytes();
        for (let i = 0; i < t.count; i++) {
          const o = t.offset + i * t.stride + t.keyOffset;
          const k = data.subarray(o, o + 32);
          const empty = k.every((x) => x === 0);
          roster.push({ slot: i, key: empty ? null : new PublicKey(k).toBase58() });
          if (!empty && k.every((x, n) => x === me[n])) slot = i;
        }
      }
      if (slot === undefined && !args.spectate) return { ok: false, error: 'slot unknown (no join.slotTable and no args.slot)' };
      let coreVersion = 0;
      const worldAddr = cfg.addresses.world;
      if (worldAddr) {
        const w = await this.er.getAccountInfo(new PublicKey(worldAddr), this.cm);
        const p = w ? parseWorldAccount(toU8(w.data), cfg.world) : null;
        if (p) coreVersion = new DataView(p.view.buffer).getUint32(4, true);
      }
      const res: JoinResult = { ok: true, slot: args.spectate ? 255 : slot, arena: cfg.addresses.arena ?? worldAddr, map: args.map ?? j.map ?? '', rate: j.rate ?? 30, coreVersion, roster, world: worldAddr };
      this.joined = res;
      return res;
    } catch (e) {
      return { ok: false, error: errMsg(e) };
    }
  }

  // ---- base layer (throttled) -------------------------------------------------------------------------------------
  private async baseBlockhashCached(): Promise<string> {
    const now = this.clock.now();
    if (this.baseBlockhash && now - this.baseBlockhash.at < 20000) return this.baseBlockhash.v;
    const v = (await this.base.getLatestBlockhash('confirmed')).blockhash;
    this.baseBlockhash = { v, at: now };
    return v;
  }

  /** Throttled (token bucket ≤ 1.5 tx/s) base-layer send + confirm. Fee payer = the authority. */
  async sendBase(ixs: TransactionInstruction[], kind: string, extraSigners: Keypair[] = [], phantom = false): Promise<{ ok: boolean; err?: unknown; sig?: string; ms?: number }> {
    return this.bucket.schedule(async () => {
      const w = this.ctx.wallet;
      const feePayer = phantom ? phantomPublicKey()! : w.burner.publicKey;
      const tx = new Transaction({ feePayer, recentBlockhash: await this.baseBlockhashCached() });
      tx.add(...ixs);
      const local = this.signersFor(ixs, phantom ? feePayer : null);
      for (const k of extraSigners) if (!local.includes(k)) local.push(k);
      if (!phantom && !local.includes(w.burner)) local.unshift(w.burner);
      if (local.length) tx.partialSign(...local);
      let signed = tx;
      if (phantom) {
        const p = getPhantom();
        if (!p || !p.signTransaction) throw new Error('Phantom cannot sign transactions');
        signed = await p.signTransaction(tx);
      }
      const raw = signed.serialize();
      const sig = await this.sendWithClonerRetry(this.base, raw, 0, false);
      const r = await this.basePoller.wait(sig, 0, kind, 60000);
      return { ...r, sig };
    });
  }

  // ---- session keys (SessionTokenV2) ----------------------------------------------------------------------------
  async session(action: string, args: Record<string, unknown>): Promise<Record<string, unknown>> {
    const w = this.ctx.wallet;
    const cfg = this.ctx.cfg;
    const programId = new PublicKey(cfg.session.programId);
    const nowSec = Math.floor(this.clock.wallMs() / 1000);
    try {
      if (action === 'create') {
        const phantom = args.authority === 'phantom';
        const authority = phantom ? phantomPublicKey() : w.burner.publicKey;
        if (!authority) return { ok: false, error: 'Phantom not connected' };
        if (w.sessionMeta.validUntil && nowSec < w.sessionMeta.validUntil && !args.force) return { ok: true, note: 'session already active', ...this.sessionInfo(nowSec) };
        if (w.sessionMeta.token) w.rotateSession(this.clock.wallMs()); // never reuse a signer whose token may exist
        const validity = Math.min(Number(args.validitySec ?? cfg.session.validitySec ?? 3600), MAX_VALIDITY_SEC - 120);
        const validUntil = nowSec + validity;
        const target = resolvePubkey(cfg.session.targetProgram, this.refs());
        const topUp = (args.topUp as boolean | undefined) ?? cfg.session.topUp ?? true;
        const lamports = (args.lamports as number | undefined) ?? cfg.session.topUpLamports ?? null;
        const { ix, token } = createSessionV2Ix({ programId, targetProgram: target, sessionSigner: w.session.publicKey, feePayer: authority, authority, topUp, validUntil, lamports });
        this.ctx.events.push({ type: 'session', state: 'creating' });
        const r = await this.sendBase([ix], 'session.create', [w.session], phantom);
        if (!r.ok) { this.ctx.events.push({ type: 'session', state: 'error', why: safeJson(r.err) }); return { ok: false, error: safeJson(r.err), sig: r.sig }; }
        w.saveSessionMeta({ authority: authority.toBase58(), feePayer: authority.toBase58(), validUntil, token: token.toBase58() });
        this.ctx.events.push({ type: 'session', state: 'active', validUntil });
        return { ok: true, sig: r.sig, ...this.sessionInfo(nowSec) };
      }
      if (action === 'topup') {
        const lamports = Number(args.lamports ?? cfg.session.topUpLamports ?? 10_000_000);
        const ix = SystemProgram.transfer({ fromPubkey: w.burner.publicKey, toPubkey: w.session.publicKey, lamports });
        const r = await this.sendBase([ix], 'session.topup');
        this.ctx.events.push({ type: 'session', state: r.ok ? 'toppedUp' : 'error', lamports });
        return { ok: r.ok, sig: r.sig, error: r.ok ? undefined : safeJson(r.err) };
      }
      if (action === 'revoke') {
        const m = w.sessionMeta;
        if (!m.token || !m.authority || !m.feePayer) return { ok: false, error: 'no session token' };
        const authority = new PublicKey(m.authority);
        const phantom = !authority.equals(w.burner.publicKey);
        const active = nowSec < m.validUntil;
        const ix = revokeSessionV2Ix(new PublicKey(m.token), new PublicKey(m.feePayer), authority, active, programId);
        const r = await this.sendBase([ix], 'session.revoke', [], phantom);
        if (r.ok) { w.rotateSession(this.clock.wallMs()); this.ctx.events.push({ type: 'session', state: 'revoked' }); }
        return { ok: r.ok, sig: r.sig, error: r.ok ? undefined : safeJson(r.err) };
      }
      if (action === 'status') {
        const info = this.sessionInfo(nowSec);
        const m = w.sessionMeta;
        let onChain: Record<string, unknown> | null = null;
        if (m.token) {
          const acc = await this.base.getAccountInfo(new PublicKey(m.token), 'confirmed');
          const t = acc ? decodeSessionTokenV2(toU8(acc.data)) : null;
          onChain = t ? { exists: true, validUntil: Number(t.validUntil), expired: isExpired(t, nowSec), authority: t.authority.toBase58(), sessionSigner: t.sessionSigner.toBase58(), targetProgram: t.targetProgram.toBase58() } : { exists: false };
        }
        let sessionLamports: number | null = null;
        try { sessionLamports = await this.base.getBalance(w.session.publicKey, 'confirmed'); } catch { /* offline */ }
        return { ok: true, ...info, onChain, sessionLamports };
      }
      return { ok: false, error: 'unknown session action ' + action };
    } catch (e) {
      return { ok: false, error: errMsg(e) };
    }
  }

  private sessionInfo(nowSec: number) {
    const m = this.ctx.wallet.sessionMeta;
    return { sessionPubkey: this.ctx.wallet.session.publicKey.toBase58(), token: m.token, authority: m.authority, validUntil: m.validUntil, expired: m.validUntil > 0 && nowSec >= m.validUntil, state: !m.token ? 'none' : nowSec >= m.validUntil ? 'expired' : 'active' };
  }

  status() {
    return { transport: 'rollup', blockhash: !!this.blockhash, subs: [...this.subs.keys()], pendingConfirms: this.erPoller.size, baseQueued: this.bucket.queued, joined: this.joined };
  }

  stop(): void {
    this.stopped = true;
    for (const t of this.timers) this.clock.clearInterval(t);
    this.erPoller.stop(); this.basePoller.stop();
    for (const s of this.subs.values()) this.detach(s);
    this.subs.clear();
  }
}

function toU8(d: Uint8Array | Buffer | ArrayBuffer): Uint8Array {
  if (d instanceof Uint8Array) return new Uint8Array(d.buffer, d.byteOffset, d.byteLength);
  return new Uint8Array(d);
}
