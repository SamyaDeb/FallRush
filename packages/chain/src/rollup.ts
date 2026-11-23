// Rollup transport: web3.js Connection to the MagicBlock ER (devnet-as) + a base-layer Connection.
import { ComputeBudgetProgram, Keypair, PublicKey, SystemProgram, Transaction } from '@solana/web3.js';
import * as Econ from './econ.ts';
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
import { customCode, decodeArena, decodeMatch, pickSlot, syntheticFinalists, toP2Batch } from './p2.ts';
import type { P2Config } from './config.ts';
import { b58decode, b58encode, hexToBytes } from './b58.ts';

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

const DELEGATION_ID = new PublicKey('DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh');

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

interface Sub { addr: string; pk: PublicKey; id: number | null; lastWsMs: number; nextResubMs: number; stalls: number; polling: boolean; raw?: any[]; rawLast?: number[] }

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
  /** who signs join_lobby / submit_input: the session signer (with its token) or the burner */
  private player: Keypair | null = null;
  private joinedWithToken = false;
  private joinPromise: Promise<JoinResult> | null = null;
  receipt: Record<string, unknown> = {};
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
    if (r.p2) {
      const a = ctx.cfg.addresses;
      const prog = resolvePubkey(ctx.cfg.addresses.program ? '$program' : ctx.cfg.session.targetProgram, a);
      const arena = a.arena ? new PublicKey(a.arena) : PublicKey.findProgramAddressSync([new TextEncoder().encode('arena'), Uint8Array.of(r.p2.arenaId)], prog)[0];
      a.arena = arena.toBase58();
      if (!a.world) a.world = PublicKey.findProgramAddressSync([new TextEncoder().encode('world'), arena.toBytes()], prog)[0].toBase58();
      this.receipt = { arena: a.arena, world: a.world };
    }
  }

  private get rc() { return this.ctx.cfg.rollup!; }
  private get cm() { return this.rc.commitment ?? 'processed'; }

  async start(): Promise<void> {
    await this.refreshBlockhash();
    const c = this.clock;
    this.timers.push(c.setInterval(() => { void this.refreshBlockhash(); }, this.rc.blockhashRefreshMs ?? 10000));
    this.timers.push(c.setInterval(() => { void this.pollAccounts(); }, this.rc.accountPollMs ?? 1000));
    if (this.rc.rawWs) this.timers.push(c.setInterval(() => { const now = this.clock.now(); for (const x of this.subs.values()) this.rawWatch(x, now); }, 250));
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
    r.player = (this.player ?? w.session).publicKey;
    if (this.player && !this.joinedWithToken) r.sessionToken = undefined; // joined by the burner: no token account
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
    const payer = this.player ?? w.session;
    const tx = new Transaction({ feePayer: payer.publicKey, recentBlockhash: this.blockhash });
    if (this.rc.computeUnitLimit) tx.add(ComputeBudgetProgram.setComputeUnitLimit({ units: this.rc.computeUnitLimit }));
    tx.add(...ixs);
    const signers = this.signersFor(ixs);
    if (!signers.includes(payer)) signers.unshift(payer);
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
    if (this.rc.p2 && !this.joined) { fail('not joined'); return id; }
    try {
      const p = toP2Batch(batch);
      built = this.buildErTx([buildInstruction(this.rc.submitInput, this.refs({ batch, slot: p.slot, start: p.start, batch12: p.batch12 }))]);
    } catch (e) { fail(errMsg(e)); return id; }
    this.receipt.lastInputSig = built.sig;
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
    const WS = (globalThis as any).WebSocket;
    if (this.rc.rawWs && this.rc.erWs && WS) {
      // P3-K: plain accountSubscribe sockets. Measured on devnet-as: the rollup streams ~90 notifications/s (every
      // tick slice) and a raw socket receives them, while web3.js's onAccountChange in this bundle went silent for
      // seconds; a raw socket also went silent at times, so keep RAW_SOCKETS of them (duplicates are dropped by tick)
      // and reopen any socket silent for rawStallMs (the watchdog in pollAccounts / rawWatch).
      const n = this.rc.rawSockets ?? 2;
      s.raw = []; s.rawLast = [];
      for (let k = 0; k < n; k++) this.rawOpen(s, k, gen);
      return;
    }
    s.id = this.er.onAccountChange(s.pk, (info) => {
      if (gen !== this.erGen || !this.subs.has(s.addr)) return;
      s.lastWsMs = this.clock.now();
      if (s.stalls) { s.stalls = 0; this.backoff.reset(); }
      this.ctx.intake.ingest(toU8(info.data), 'ws');
    }, this.cm);
  }

  private rawOpen(s: Sub, k: number, gen: number) {
    const WS = (globalThis as any).WebSocket;
    const ws = new WS(this.rc.erWs);
    s.raw![k] = ws;
    s.rawLast![k] = this.clock.now();
    ws.onopen = () => ws.send(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'accountSubscribe', params: [s.pk.toBase58(), { encoding: 'base64', commitment: this.cm }] }));
    ws.onmessage = (m: { data: unknown }) => {
      if (gen !== this.erGen || !s.raw || s.raw[k] !== ws || !this.subs.has(s.addr)) return;
      const txt = typeof m.data === 'string' ? m.data : String(m.data);
      if (txt.indexOf('accountNotification') < 0) return;
      try {
        const j = JSON.parse(txt);
        const b64 = j?.params?.result?.value?.data?.[0];
        if (typeof b64 !== 'string') return;
        const now = this.clock.now();
        s.rawLast![k] = now;
        s.lastWsMs = now;
        if (s.stalls) { s.stalls = 0; this.backoff.reset(); }
        this.ctx.intake.ingest(toU8(Buffer.from(b64, 'base64')), 'ws');
      } catch (e) { this.ctx.events.push({ type: 'error', where: 'rawWs', why: errMsg(e) }); }
    };
    ws.onerror = () => { /* rawWatch reopens it */ };
  }

  /** Reopens a raw socket that has been silent for rawStallMs (default 600 ms). */
  private rawWatch(s: Sub, now: number) {
    if (!s.raw) return;
    if (now - s.lastWsMs > 10_000) return; // the arena is idle (no ticks): the backup poll covers it, no churn
    const stall = this.rc.rawStallMs ?? 600;
    for (let k = 0; k < s.raw.length; k++) {
      if (now - (s.rawLast![k] ?? 0) <= stall) continue;
      try { s.raw[k].close(); } catch { /* closed */ }
      this.ctx.stats.reconnects++;
      this.ctx.events.push({ type: 'wsReconnect', transport: 'rollup', how: 'rawReopen', socket: k, silentMs: Math.round(now - (s.rawLast![k] ?? 0)) });
      this.rawOpen(s, k, this.erGen);
    }
  }

  private detach(s: Sub) {
    if (s.raw) { const r = s.raw; s.raw = undefined; for (const x of r) { try { x.close(); } catch { /* closed */ } } }
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
      if (s.raw) { this.rawWatch(s, now); continue; }
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
    if (this.rc.p2) {
      if (this.joined) return this.joined; // idempotent (relay re-joins, Unity reconnects)
      if (!this.joinPromise) this.joinPromise = this.p2Join(args, this.rc.p2).finally(() => { this.joinPromise = null; });
      return this.joinPromise;
    }
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
        let { ix, token } = createSessionV2Ix({ programId, targetProgram: target, sessionSigner: w.session.publicKey, feePayer: authority, authority, topUp, validUntil, lamports });
        void token;
        this.ctx.events.push({ type: 'session', state: 'creating' });
        let r: { ok: boolean; err?: unknown; sig?: string };
        try { r = await this.sendBase([ix], 'session.create', [w.session], phantom); } catch (e) { r = { ok: false, err: errMsg(e) }; }
        if (!r.ok) {
          // P4: a create that landed although its confirmation timed out left the token on-chain unsaved ("already in
          // use" on the next try): adopt it when still valid for this signer + authority, else retry with a fresh signer
          try {
            const ex = await this.base.getAccountInfo(token, 'confirmed');
            const t = ex ? decodeSessionTokenV2(toU8(ex.data)) : null;
            if (t && t.sessionSigner.equals(w.session.publicKey) && t.authority.equals(authority) && !isExpired(t, nowSec + 300)) {
              w.saveSessionMeta({ authority: authority.toBase58(), feePayer: t.feePayer.toBase58(), validUntil: Number(t.validUntil), token: token.toBase58() });
              this.ctx.events.push({ type: 'session', state: 'adopted', validUntil: Number(t.validUntil) });
              return { ok: true, note: 'adopted the existing token', ...this.sessionInfo(nowSec) };
            }
            if (t) {
              w.rotateSession(this.clock.wallMs());
              ({ ix, token } = createSessionV2Ix({ programId, targetProgram: target, sessionSigner: w.session.publicKey, feePayer: authority, authority, topUp, validUntil, lamports }));
              try { r = await this.sendBase([ix], 'session.create', [w.session], phantom); } catch (e) { r = { ok: false, err: errMsg(e) }; }
            }
          } catch { /* report the first error */ }
        }
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

  // ---- Node relay helpers (tools/p3/chain-relay.mjs) ------------------------------------------------------------
  /** Base-layer balances of the burner and the session signer (lamports). */
  async balances(): Promise<Record<string, unknown>> {
    const w = this.ctx.wallet;
    const [burner, session] = await Promise.all([this.base.getBalance(w.burner.publicKey, 'confirmed'), this.base.getBalance(w.session.publicKey, 'confirmed').catch(() => 0)]);
    return { burner, session, burnerPubkey: w.burner.publicKey.toBase58(), sessionPubkey: w.session.publicKey.toBase58(), needed: this.neededLamports() };
  }

  /** What the burner spends to create a session: token rent (144 B) + top-up + fees margin. */
  neededLamports(): number {
    const s = this.ctx.cfg.session;
    const rent = (144 + 128) * 3480 * 2; // rent-exempt minimum for a 144-byte account
    return rent + (s.topUp === false ? 0 : s.topUpLamports ?? 10_000_000) + 20_000;
  }

  /** Transfer `lamports` from a local funder keypair (secret base58) to the burner, throttled + confirmed. */
  async fundBurner(fromSecretB58: string, lamports: number): Promise<Record<string, unknown>> {
    const from = Keypair.fromSecretKey(b58decode(fromSecretB58));
    return this.bucket.schedule(async () => {
      const tx = new Transaction({ feePayer: from.publicKey, recentBlockhash: await this.baseBlockhashCached() });
      tx.add(SystemProgram.transfer({ fromPubkey: from.publicKey, toPubkey: this.ctx.wallet.burner.publicKey, lamports }));
      tx.sign(from);
      const sig = await this.base.sendRawTransaction(tx.serialize(), { skipPreflight: false, maxRetries: 3 });
      const r = await this.basePoller.wait(sig, 0, 'fund', 60000);
      return { ok: r.ok, sig, from: from.publicKey.toBase58(), to: this.ctx.wallet.burner.publicKey.toBase58(), lamports, error: r.ok ? undefined : safeJson(r.err) };
    });
  }

  // ---- P2 join flow ---------------------------------------------------------------------------------------------
  private jev(stage: string, o: Record<string, unknown> = {}) { this.ctx.events.push({ type: 'join', stage, ...o }); }

  private async readEr(pk: PublicKey): Promise<Uint8Array | null> {
    try { const i = await this.er.getAccountInfo(pk, this.cm); return i ? toU8(i.data) : null; } catch { return null; }
  }

  /** A SessionTokenV2 for (burner, session signer, this program) that exists on the base layer and is valid ≥ 60 s. */
  private async ensureSession(): Promise<boolean> {
    const w = this.ctx.wallet;
    const nowSec = Math.floor(this.clock.wallMs() / 1000);
    const m = w.sessionMeta;
    if (m.token && m.authority === w.burner.publicKey.toBase58() && m.validUntil > nowSec + 60) {
      try {
        const acc = await this.base.getAccountInfo(new PublicKey(m.token), 'confirmed');
        const t = acc ? decodeSessionTokenV2(toU8(acc.data)) : null;
        if (t && t.sessionSigner.equals(w.session.publicKey) && !isExpired(t, nowSec + 60)) { this.jev('session', { state: 'reused', token: m.token }); return true; }
      } catch { /* fall through: create */ }
    }
    this.jev('session', { state: 'creating' });
    const r = await this.session('create', { force: true });
    if (r.ok) { this.receipt.sessionSig = r.sig; this.receipt.sessionToken = r.token; return true; }
    this.jev('session', { state: 'failed', why: r.error });
    return false;
  }

  /** ER send + confirm, retried while the rollup clones accounts (P0 quirk 7, keeper sendEr's list). */
  private async sendErConfirmed(ix: TransactionInstruction, kind: string, retryMs: number): Promise<{ ok: boolean; sig: string; err?: unknown }> {
    const t0 = this.clock.now();
    for (let n = 0; ; n++) {
      let sig = '', err: unknown;
      try {
        if (n > 0) await this.refreshBlockhash();
        const b = this.buildErTx([ix]);
        sig = b.sig;
        await this.sendWithClonerRetry(this.er, b.raw, Math.max(0, retryMs - (this.clock.now() - t0)), false);
        const r = await this.erPoller.wait(sig, 0, kind, 15000);
        if (r.ok) return { ok: true, sig };
        err = r.err;
      } catch (e) { err = errMsg(e); }
      const s = typeof err === 'string' ? err : safeJson(err);
      if (!/pending|AccountNotFound|cloner|timeout|Blockhash/i.test(s) || this.clock.now() - t0 > retryMs) return { ok: false, sig, err };
      await sleep(this.clock, 600);
    }
  }

  private async p2Join(args: JoinArgs, c: P2Config): Promise<JoinResult> {
    const cfg = this.ctx.cfg;
    const w = this.ctx.wallet;
    const arena = new PublicKey(cfg.addresses.arena);
    const watch = c.watchMs ?? 250;
    const t0 = this.clock.now();
    try {
      // 1. session key (base layer), else the burner signs
      const tok = c.useSession === false ? false : await this.ensureSession();
      this.player = tok ? w.session : w.burner;
      this.joinedWithToken = tok;
      const me = w.burner.publicKey;
      // hybrid: only this match's lobby (Arena.match_account = the Match PDA ["match", id])
      let wantMatch: PublicKey | null = null;
      if (args.match) {
        const id = hexToBytes(args.match);
        if (id.length !== 16) return { ok: false, error: 'match must be 16 bytes (32 hex)' };
        const prog = resolvePubkey('$program', this.refs());
        wantMatch = PublicKey.findProgramAddressSync([new TextEncoder().encode('match'), id], prog)[0];
        this.receipt.match = wantMatch.toBase58();
        this.jev('waitingMatch', { match: wantMatch.toBase58() });
      }
      const ours = (x: NonNullable<ReturnType<typeof decodeArena>>) => !x.matchAccount.equals(PublicKey.default) && (!wantMatch || x.matchAccount.equals(wantMatch));
      // 2. wait for open_lobby (or find our wallet already in the roster)
      let a = null as ReturnType<typeof decodeArena>;
      let slot: number | null = null;
      let announced = false;
      for (;;) {
        const d = await this.readEr(arena);
        a = d ? decodeArena(d, c) : null;
        if (a) {
          const mine = a.wallets.findIndex((k) => k.equals(me));
          if (mine >= 0 && ours(a) && (a.status === c.arenaStatus.lobby || a.status === c.arenaStatus.running)) { slot = mine; this.jev('alreadyJoined', { slot }); break; }
          if (a.status === c.arenaStatus.lobby && ours(a)) break;
          if (wantMatch && a.status === c.arenaStatus.running && ours(a)) { slot = 255; this.jev('missedLobby', { match: wantMatch.toBase58() }); break; } // started without us: spectate
          if (!announced) { announced = true; this.jev('waitingLobby', { arenaStatus: a.status }); }
        }
        if (this.clock.now() - t0 > (c.lobbyTimeoutMs ?? 900000)) return { ok: false, error: 'lobby did not open' };
        await sleep(this.clock, watch);
      }
      const matchPk = a!.matchAccount;
      this.receipt.match = matchPk.toBase58();
      this.jev('lobbyOpen', { arena: arena.toBase58(), match: matchPk.toBase58(), humanMask: a!.humanMask });
      // 3. slot: the given one (hybrid: the Classic finalist) or the lowest free synthetic finalist
      let finalists: number[] | null = null;
      if (args.spectate) slot = 255;
      const readMatch = async () => { const d = await this.readEr(matchPk); return d ? decodeMatch(d, c) : null; };
      for (let attempt = 0; slot === null; attempt++) {
        let want = args.slot ?? null;
        if (want === null) {
          if (!finalists) {
            let m = await readMatch();
            while (!m || m.roundTargets[0] === 0) { if (this.clock.now() - t0 > (c.lobbyTimeoutMs ?? 900000)) return { ok: false, error: 'match not readable' }; await sleep(this.clock, watch); m = await readMatch(); }
            finalists = await syntheticFinalists(m.matchId, m.playerCount, m.roundTargets);
            this.jev('finalists', { finalists });
          }
          want = pickSlot(finalists, a!.humanMask);
          if (want === null) return { ok: false, error: 'full' };
        }
        const ix = buildInstruction(c.joinLobby, this.refs({ slot: want, cosmetic: c.cosmetic ?? 0, wallet: me, player: this.player.publicKey, sessionToken: tok ? (this.refs().sessionToken as PublicKey) : undefined }));
        const r = await this.sendErConfirmed(ix, 'join', c.joinRetryMs ?? 10000);
        if (r.ok) { slot = want; this.receipt.joinSig = r.sig; break; }
        const code = customCode(r.err);
        const d = await this.readEr(arena);
        a = d ? decodeArena(d, c) : a;
        const mine = a ? a.wallets.findIndex((k) => k.equals(me)) : -1;
        if (mine >= 0) { slot = mine; break; }
        if (code === (c.errors?.slotTaken ?? 6023) && args.slot === undefined && attempt < 12) { this.jev('slotTaken', { slot: want }); continue; }
        return { ok: false, error: 'join_lobby failed: ' + safeJson(r.err) };
      }
      this.receipt.slot = slot;
      if (slot !== 255) this.jev('lobbyJoined', { slot, sig: this.receipt.joinSig ?? null, player: this.player.publicKey.toBase58(), withSessionToken: tok });
      // 4. wait for start_final
      for (;;) {
        if (a && a.status === c.arenaStatus.running) break;
        if (a && a.status !== c.arenaStatus.lobby) return { ok: false, error: 'arena left the lobby without starting (status ' + a.status + ')' };
        if (this.clock.now() - t0 > (c.startTimeoutMs ?? 900000)) return { ok: false, error: 'start_final timeout' };
        await sleep(this.clock, watch);
        const d = await this.readEr(arena);
        a = d ? decodeArena(d, c) : a;
      }
      // 5. roster = the finalists (Match slots alive after round 3), human when the arena's human_mask says so
      const m = await readMatch();
      let fin = m ? m.slotStates.map((st, i) => (st === c.slotState.alive ? i : -1)).filter((i) => i >= 0) : [];
      if (fin.length === 0 && finalists) fin = finalists;
      if (slot !== 255 && fin.length && !fin.includes(slot!)) { this.jev('notFinalist', { slot }); slot = 255; } // spectate
      const roster = fin.map((s) => ({ slot: s, kind: ((a!.humanMask >> s) & 1) ? 'human' : 'bot', name: s === slot ? args.name : undefined, wallet: ((a!.humanMask >> s) & 1) ? a!.wallets[s].toBase58() : undefined }));
      this.receipt.finalists = fin;
      // P4 0.1: the core version the arena's match runs (WorldAcc.core_version); the client refuses a mismatch
      let coreVersion = 0;
      if (cfg.addresses.world) {
        const wd = await this.readEr(new PublicKey(cfg.addresses.world));
        const pw = wd ? parseWorldAccount(wd, cfg.world) : null;
        if (pw) coreVersion = new DataView(pw.view.buffer).getUint32(4, true);
      }
      const res: JoinResult = { ok: true, slot: slot!, arena: arena.toBase58(), map: args.map ?? this.rc.join?.map ?? 'CrumbleShelf', rate: this.rc.join?.rate ?? 30, coreVersion, roster, world: cfg.addresses.world };
      (res as Record<string, unknown>).match = matchPk.toBase58();
      this.joined = res;
      this.jev('started', { slot, match: matchPk.toBase58(), finalists: fin, ms: Math.round(this.clock.now() - t0) });
      return res;
    } catch (e) {
      return { ok: false, error: errMsg(e) };
    }
  }

  // ---- P4 economy (econ.ts): chips, markets, drops, receipt, claim, cash-out -------------------------------------
  private econCtx() {
    const cfg = this.ctx.cfg;
    const program = resolvePubkey('$program', this.refs());
    const arena = new PublicKey(cfg.addresses.arena);
    // P5 arena pool: the arena id behind the configured / handed-off arena address (market PDAs are keyed by it)
    let arenaId = this.rc.p2?.arenaId ?? 0;
    const te = new TextEncoder();
    for (let id = 0; id < 32; id++) {
      if (PublicKey.findProgramAddressSync([te.encode('arena'), Uint8Array.of(id)], program)[0].equals(arena)) { arenaId = id; break; }
    }
    return { program, arena, arenaId, P: Econ.econPdas(program, arena, arenaId), world: new PublicKey(cfg.addresses.world) };
  }

  /** Who signs economy instructions for the burner: its session key with a valid token (no popup), else the burner. */
  private econSigner(): { signer: Keypair; token: PublicKey | null } {
    const w = this.ctx.wallet;
    const nowSec = Math.floor(this.clock.wallMs() / 1000);
    const m = w.sessionMeta;
    if (m.token && m.authority === w.burner.publicKey.toBase58() && m.validUntil > nowSec + 30) return { signer: w.session, token: new PublicKey(m.token) };
    return { signer: w.burner, token: null };
  }

  private async econEr(ix: TransactionInstruction, signer: Keypair, kind: string): Promise<{ ok: boolean; sig: string; err?: unknown; ms: number }> {
    const t0 = this.clock.now();
    for (let n = 0; n < 6; n++) {
      try {
        if (n > 0 || !this.blockhash) await this.refreshBlockhash();
        const tx = new Transaction({ feePayer: signer.publicKey, recentBlockhash: this.blockhash! });
        tx.add(ix);
        tx.sign(signer);
        const sig = await this.sendWithClonerRetry(this.er, tx.serialize(), 4000, true);
        const r = await this.erPoller.wait(sig, 0, kind, 15000);
        if (r.ok) return { ok: true, sig, ms: Math.round(this.clock.now() - t0) };
        const e = safeJson(r.err);
        // P5: a confirmation TIMEOUT means the transaction may have landed: never resend (a bet / Chaos Drop would
        // be bought twice, seen on devnet); the caller re-reads the state. Resend only on errors before execution.
        if (/timeout/i.test(e)) return { ok: false, sig, err: 'unconfirmed (check the state before retrying)', ms: Math.round(this.clock.now() - t0) };
        if (!/pending|AccountNotFound|cloner|Blockhash/i.test(e)) return { ok: false, sig, err: r.err, ms: Math.round(this.clock.now() - t0) };
      } catch (e) { if (n === 5) return { ok: false, sig: '', err: errMsg(e), ms: Math.round(this.clock.now() - t0) }; }
      await sleep(this.clock, 500);
    }
    return { ok: false, sig: '', err: 'retries exhausted', ms: Math.round(this.clock.now() - t0) };
  }

  async econ(action: string, args: Record<string, unknown>): Promise<Record<string, unknown>> {
    const w = this.ctx.wallet;
    const me = w.burner.publicKey;
    const E = this.econCtx();
    const acc = (pubkey: PublicKey, isWritable: boolean) => ({ pubkey, isSigner: false, isWritable });
    const big = (o: unknown) => JSON.parse(JSON.stringify(o, (_k, v) => (typeof v === 'bigint' ? v.toString() : v)));
    try {
      if (action === 'chips') {
        const chips = E.P.chips(me);
        const info = await this.base.getAccountInfo(chips, 'confirmed');
        const validator = new PublicKey(this.rc.validator ?? 'MAS1Dt9qreoRMQ14YQuhg8UTZMMzDdKhmkZMECCzk57');
        const del = Econ.delegateChipsIx(E.program, me, chips, validator, Uint8Array.from([0xf1, 0xb8, 0xa5, 0xb0, 0x7b, 0x37, 0xe7, 0x51]));
        let created: string | undefined;
        if (!info) {
          const r = await this.sendBase([Econ.econIx(E.program, me, Econ.OP.INIT_CHIPS, 0, 0, [acc(chips, true), acc(SystemProgram.programId, false)]), del], 'chips_init');
          if (!r.ok) return { ok: false, error: 'chips init: ' + safeJson(r.err) };
          created = r.sig;
        } else if ((info as { owner?: PublicKey }).owner && (info as { owner: PublicKey }).owner.equals(E.program)) {
          const r = await this.sendBase([del], 'chips_delegate');
          if (!r.ok) return { ok: false, error: 'chips delegate: ' + safeJson(r.err) };
        }
        let d: Uint8Array | null = null;
        for (let i = 0; i < 40 && !(d = await this.readEr(chips)); i++) await sleep(this.clock, 500);
        if (!d) return { ok: false, error: 'chips not on the rollup yet' };
        let c = Econ.decodeChips(d);
        let faucet: string | undefined;
        const nowSec = Math.floor(this.clock.wallMs() / 1000);
        if (args.faucet !== false && (c.faucetTotal === 0n || nowSec - c.faucetAt >= 86_400)) {
          const { signer, token } = this.econSigner();
          const accs = [acc(chips, true), ...(token ? [acc(token, false)] : [])];
          const r = await this.econEr(Econ.econIx(E.program, signer.publicKey, Econ.OP.FAUCET, 0, 0, accs), signer, 'faucet');
          if (r.ok) { faucet = r.sig; const d2 = await this.readEr(chips); if (d2) c = Econ.decodeChips(d2); }
          this.ctx.events.push({ type: 'econ', what: 'faucet', ok: r.ok, sig: r.sig, err: r.ok ? undefined : safeJson(r.err) });
        }
        return { ok: true, wallet: me.toBase58(), chips: big(c), created, faucet };
      }
      if (action === 'state') {
        const ad = await this.readEr(E.arena);
        const matchPk = ad && ad.length >= 72 ? new PublicKey(ad.subarray(40, 72)) : null;
        const keys = [E.P.chips(me), ...[0, 1, 2, 3, 4].map((k) => E.P.market(k)), E.P.drops, E.world, ...(matchPk ? [matchPk] : [])];
        const datas = await Promise.all(keys.map((k) => this.readEr(k)));
        const markets = datas.slice(1, 6).map((x, k) => {
          if (!x) return { k, name: Econ.MARKET_NAMES[k], status: -1, stake: [], mine: [], bets: [] };
          const m = Econ.decodeMarket(x);
          const mine = Array(12).fill(0n);
          for (const b of m.bets) if (b.wallet === me.toBase58()) mine[b.outcome] += b.amount;
          return { k, name: Econ.MARKET_NAMES[k], ...m, mine };
        });
        const wd = datas[7];
        const world = wd ? { tick: new DataView(wd.buffer, wd.byteOffset).getUint32(128, true), phase: wd[138], round: wd[139], roundPhase: wd[199], roundStart: new DataView(wd.buffer, wd.byteOffset).getUint32(208 + 18, true) } : null;
        const md = matchPk ? datas[8] : null;
        const match = md && matchPk ? { pda: matchPk.toBase58(), status: md[26], flags: md[27], players: md[128], round: md[129], slots: Array.from({ length: 12 }, (_, i) => md[214 + 4 * i]), champion: md[294] } : null;
        return big({ ok: true, wallet: me.toBase58(), chips: datas[0] ? Econ.decodeChips(datas[0]) : null, markets, drops: datas[6] ? Econ.decodeDrops(datas[6]) : null, world, match, prices: Econ.DROP_PRICE });
      }
      if (action === 'bet' || action === 'drop') {
        const { signer, token } = this.econSigner();
        const ad = await this.readEr(E.arena);
        if (!ad) return { ok: false, error: 'arena not readable' };
        const matchPk = new PublicKey(ad.subarray(40, 72));
        let ix: TransactionInstruction;
        if (action === 'bet') {
          const k = Number(args.k), outcome = Number(args.outcome), amount = BigInt(Math.round(Number(args.amount))) * Econ.ONE;
          ix = Econ.econIx(E.program, signer.publicKey, Econ.OP.PLACE_BET, outcome, amount, [acc(E.P.chips(me), true), acc(E.P.market(k), true), acc(matchPk, false), acc(E.arena, false), acc(E.world, false), ...(token ? [acc(token, false)] : [])]);
        } else {
          ix = Econ.econIx(E.program, signer.publicKey, Econ.OP.BUY_DROP, Number(args.kind), 0, [acc(E.P.chips(me), true), acc(E.P.drops, true), acc(E.arena, false), acc(E.world, false), ...(token ? [acc(token, false)] : [])]);
        }
        const r = await this.econEr(ix, signer, action);
        this.ctx.events.push({ type: 'econ', what: action, ok: r.ok, sig: r.sig, args, err: r.ok ? undefined : safeJson(r.err) });
        return { ok: r.ok, sig: r.sig, ms: r.ms, session: !!token, error: r.ok ? undefined : safeJson(r.err) };
      }
      if (action === 'receipt') {
        const pk = new PublicKey(String(args.match));
        const bd = await this.base.getAccountInfo(pk, 'confirmed');
        const onSolana = !!bd && (bd as { owner?: PublicKey }).owner?.toBase58() === E.program.toBase58();
        const d = onSolana ? toU8(bd!.data) : await this.readEr(pk);
        if (!d) return { ok: false, error: 'match not found' };
        const r = Econ.decodeReceipt(d);
        const crown = E.P.crown(pk).toBase58();
        const capsules = Array.from({ length: 12 }, (_, s) => ((r.receipt.humanMask >> s) & 1 ? E.P.capsule(pk, s).toBase58() : null));
        return big({ ok: true, onSolana, match: pk.toBase58(), ...r, crownAsset: crown, capsuleAssets: capsules, me: me.toBase58() });
      }
      if (action === 'rush') {
        // P5 0.1: the wallet's real RUSH SPL balance on Solana (base units, 6 decimals)
        const owner = args.owner ? new PublicKey(String(args.owner)) : me;
        const ata = Econ.ataOf(owner, Econ.RUSH_MINT);
        const ai = await this.base.getAccountInfo(ata, 'confirmed');
        const ad = ai ? toU8(ai.data) : null;
        const amount = ad && ad.length >= 72 ? new DataView(ad.buffer, ad.byteOffset).getBigUint64(64, true) : 0n;
        return big({ ok: true, owner: owner.toBase58(), ata: ata.toBase58(), amount, exists: !!ai });
      }
      if (action === 'cashout') {
        // P5 0.1 "assets you can sell anytime": chips → cashout (rollup; commits the ChipBalance to Solana) →
        // mint_cashout (Solana, permissionless: mints exactly the committed, not yet minted amount) → real RUSH SPL in
        // the burner's ATA. args.amount in whole RUSH; args.mintOnly = retry the Solana half only.
        const chips = E.P.chips(me);
        const out: Record<string, unknown> = { ok: false };
        if (!args.mintOnly) {
          const amount = BigInt(Math.round(Number(args.amount))) * Econ.ONE;
          if (amount <= 0n) return { ok: false, error: 'amount must be > 0' };
          const { signer, token } = this.econSigner();
          const MAGIC_CONTEXT = new PublicKey('MagicContext1111111111111111111111111111111');
          const MAGIC_PROGRAM = new PublicKey('Magic11111111111111111111111111111111111111');
          const ix = Econ.econIx(E.program, signer.publicKey, Econ.OP.CASHOUT, amount, 0, [acc(chips, true), acc(MAGIC_CONTEXT, true), acc(MAGIC_PROGRAM, false), ...(token ? [acc(token, false)] : [])]);
          const r = await this.econEr(ix, signer, 'cashout');
          this.ctx.events.push({ type: 'econ', what: 'cashout', ok: r.ok, sig: r.sig, err: r.ok ? undefined : safeJson(r.err) });
          if (!r.ok) return { ok: false, stage: 'rollup', sig: r.sig, error: safeJson(r.err) };
          out.cashoutSig = r.sig; out.session = !!token; out.rollupMs = r.ms;
        }
        // wait until Solana holds the committed cashed_out total (the commit lands within seconds)
        const er0 = await this.readEr(chips);
        const want = er0 ? Econ.decodeChips(er0).cashedOut : 0n;
        const t0 = this.clock.now();
        let committed = 0n;
        for (let i = 0; i < 90; i++) {
          const bd = await this.base.getAccountInfo(chips, 'confirmed').catch(() => null);
          if (bd) committed = Econ.decodeChips(toU8(bd.data)).cashedOut;
          if (committed >= want && committed > 0n) break;
          await sleep(this.clock, 1000);
        }
        out.commitMs = Math.round(this.clock.now() - t0); out.committedCashedOut = committed;
        if (committed < want || committed === 0n) return big({ ...out, ok: false, stage: 'commit', error: 'the commit has not reached Solana yet (retry with mintOnly)' });
        const r2 = await this.sendBase([Econ.mintCashoutIx(E.program, me, me)], 'mint_cashout');
        out.mintSig = r2.sig;
        if (!r2.ok) return big({ ...out, ok: false, stage: 'mint', error: safeJson(r2.err) });
        return big({ ...out, ok: true, ata: Econ.ataOf(me, Econ.RUSH_MINT).toBase58() });
      }
      if (action === 'sendRush') {
        // P5: move real RUSH (SPL TransferChecked, burner-signed) to another wallet, e.g. the player's Phantom
        const to = new PublicKey(String(args.to));
        const amount = BigInt(Math.round(Number(args.amount) * 1e6));
        const r = await this.sendBase(Econ.rushTransferIxs(me, to, amount), 'send_rush');
        return { ok: r.ok, sig: r.sig, error: r.ok ? undefined : safeJson(r.err) };
      }
      if (action === 'claim') {
        const asset = new PublicKey(String(args.asset));
        const to = new PublicKey(String(args.to));
        const r = await this.sendBase([Econ.coreTransferIx(asset, me, me, to)], 'claim');
        return { ok: r.ok, sig: r.sig, error: r.ok ? undefined : safeJson(r.err) };
      }
      return { ok: false, error: 'unknown economy action ' + action };
    } catch (e) { return { ok: false, error: errMsg(e) }; }
  }

  status() {
    return { transport: 'rollup', receipt: this.receipt, blockhash: !!this.blockhash, subs: [...this.subs.keys()], pendingConfirms: this.erPoller.size, baseQueued: this.bucket.queued, joined: this.joined };
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
