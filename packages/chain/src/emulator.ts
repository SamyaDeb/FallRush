// Emulator transport: browser WebSocket per docs/proof/p3/wire-v0.md §4.
import type { Ctx, JoinArgs, JoinResult, Transport } from './transport.ts';
import { Backoff, errMsg } from './transport.ts';
import { CONFIRM_STATUS, decodeConfirmFrame, encodeSubmitFrame, OP_CONFIRM, OP_WORLD, OP_WORLD_POLL, validateInputBatch } from './wire.ts';

export interface WSLike {
  binaryType: string;
  readyState: number;
  send(data: string | Uint8Array | ArrayBuffer): void;
  close(code?: number, reason?: string): void;
  onopen: ((ev: unknown) => void) | null;
  onclose: ((ev: unknown) => void) | null;
  onerror: ((ev: unknown) => void) | null;
  onmessage: ((ev: { data: unknown }) => void) | null;
}
export type WSCtor = new (url: string) => WSLike;

const OPEN = 1;

export class EmulatorTransport implements Transport {
  readonly kind = 'emulator' as const;
  private ctx: Ctx;
  private WS: WSCtor;
  private ws: WSLike | null = null;
  private stopped = false;
  private backoff: Backoff;
  private everOpened = false;
  private openWaiters: (() => void)[] = [];
  private txid = 0;
  private sent = new Map<number, number>(); // txid → send time
  private joinPending: { resolve: (r: JoinResult) => void; timer: unknown } | null = null;
  private joinArgs: JoinArgs | null = null;
  private joined: JoinResult | null = null;
  private subs = new Set<string>();
  private pollTimer: unknown = null;
  private pingTimer: unknown = null;
  private reconnectTimer: unknown = null;
  private sessionState: Record<string, unknown> = { state: 'none' };

  constructor(ctx: Ctx, WS: WSCtor) {
    this.ctx = ctx;
    this.WS = WS;
    const r = ctx.cfg.emulator!.reconnect || {};
    this.backoff = new Backoff(r.minMs ?? 250, r.maxMs ?? 5000);
  }

  private get ecfg() { return this.ctx.cfg.emulator!; }

  start(): Promise<void> {
    this.connect();
    this.pingTimer = this.ctx.clock.setInterval(() => this.ping(), this.ecfg.pingMs ?? 1000);
    this.pollTimer = this.ctx.clock.setInterval(() => this.poll(), this.ecfg.pollMs ?? 1000);
    // resolve on first open, or after 3 s regardless (init must not hang; reconnect keeps trying)
    return new Promise((resolve) => {
      if (this.ws && this.ws.readyState === OPEN) return resolve();
      const t = this.ctx.clock.setTimeout(resolve, 3000);
      this.openWaiters.push(() => { this.ctx.clock.clearTimeout(t); resolve(); });
    });
  }

  private connect(): void {
    if (this.stopped) return;
    let ws: WSLike;
    try { ws = new this.WS(this.ecfg.url); } catch (e) { this.ctx.events.push({ type: 'error', where: 'ws', why: errMsg(e) }); this.scheduleReconnect(); return; }
    ws.binaryType = 'arraybuffer';
    this.ws = ws;
    ws.onopen = () => {
      this.backoff.reset();
      if (this.everOpened) {
        this.ctx.stats.reconnects++;
        this.ctx.events.push({ type: 'wsReconnect', transport: 'emulator' });
        if (this.joinArgs && this.joined) this.sendJoin(this.joinArgs); // restore our slot
        if (this.subs.size) this.poll(); // resync immediately
      } else {
        this.ctx.events.push({ type: 'wsOpen', transport: 'emulator' });
      }
      this.everOpened = true;
      const w = this.openWaiters; this.openWaiters = [];
      w.forEach((f) => f());
    };
    ws.onmessage = (ev) => this.onMessage(ev.data);
    ws.onerror = () => { /* onclose follows */ };
    ws.onclose = () => {
      if (this.ws !== ws) return;
      this.ws = null;
      if (this.stopped) return;
      this.ctx.events.push({ type: 'wsClosed', transport: 'emulator' });
      this.scheduleReconnect();
    };
  }

  private scheduleReconnect() {
    if (this.stopped || this.reconnectTimer !== null) return;
    const ms = this.backoff.next();
    this.reconnectTimer = this.ctx.clock.setTimeout(() => { this.reconnectTimer = null; this.connect(); }, ms);
  }

  private sendText(o: unknown): boolean {
    if (!this.ws || this.ws.readyState !== OPEN) return false;
    this.ws.send(JSON.stringify(o));
    return true;
  }

  private ping() { this.sendText({ t: 'ping', c: this.ctx.clock.now() }); this.expireSent(); }

  private poll() {
    if (!this.subs.size) return;
    if (this.sendText({ t: 'get' })) this.ctx.stats.polls++;
  }

  private expireSent() {
    const now = this.ctx.clock.now();
    for (const [id, t] of this.sent) {
      if (now - t > 10000) {
        this.sent.delete(id);
        this.ctx.stats.confirmTimeouts++;
        this.ctx.events.push({ type: 'confirm', txid: id, status: 'timeout', ms: null });
      }
    }
  }

  private onMessage(data: unknown) {
    if (typeof data === 'string') {
      let m: Record<string, unknown>;
      try { m = JSON.parse(data); } catch { return; }
      this.onControl(m);
      return;
    }
    let b: Uint8Array;
    if (data instanceof ArrayBuffer || Object.prototype.toString.call(data) === '[object ArrayBuffer]') b = new Uint8Array(data as ArrayBuffer);
    else if (ArrayBuffer.isView(data)) b = new Uint8Array((data as Uint8Array).buffer, (data as Uint8Array).byteOffset, (data as Uint8Array).byteLength);
    else return;
    if (!b.length) return;
    const op = b[0];
    if (op === OP_CONFIRM) {
      const c = decodeConfirmFrame(b);
      if (!c) return;
      const t0 = this.sent.get(c.txid);
      this.sent.delete(c.txid);
      const ms = t0 === undefined ? null : Math.round((this.ctx.clock.now() - t0) * 10) / 10;
      const status = CONFIRM_STATUS[c.status] ?? 'err' + c.status;
      if (c.status === 0) this.ctx.stats.confirmOk++; else this.ctx.stats.confirmBad++;
      if (ms !== null) this.ctx.stats.addConfirm(ms);
      this.ctx.events.push({ type: 'confirm', txid: c.txid, status, chainTick: c.chainTick, ms });
    } else if (op === OP_WORLD || op === OP_WORLD_POLL) {
      if (b.length < 5 || !this.subs.size) return;
      const seq = new DataView(b.buffer, b.byteOffset, b.byteLength).getUint32(1, true);
      this.ctx.intake.ingest(b.subarray(5), op === OP_WORLD ? 'ws' : 'poll', seq);
    }
  }

  private onControl(m: Record<string, unknown>) {
    switch (m.t) {
      case 'pong': {
        const c = Number(m.c);
        if (Number.isFinite(c)) this.ctx.stats.addRtt(this.ctx.clock.now() - c);
        break;
      }
      case 'joined': {
        const r: JoinResult = { ok: true, slot: m.slot as number, arena: m.arena as string, map: m.map as string, rate: m.rate as number, coreVersion: m.coreVersion as number, roster: (m.roster as unknown[]) || [], world: 'world' };
        const wasRejoin = !this.joinPending && this.joined;
        this.joined = r;
        if (wasRejoin) this.ctx.events.push({ type: 'rejoined', ...r });
        this.finishJoin(r);
        break;
      }
      case 'full': this.finishJoin({ ok: false, error: 'full' }); break;
      case 'err': this.finishJoin({ ok: false, error: String(m.why ?? 'err') }); this.ctx.events.push({ type: 'error', where: 'emulator', why: m.why }); break;
      default: this.ctx.events.push({ type: 'emulator', msg: m });
    }
  }

  private finishJoin(r: JoinResult) {
    const p = this.joinPending;
    if (!p) return;
    this.joinPending = null;
    this.ctx.clock.clearTimeout(p.timer);
    p.resolve(r);
  }

  private sendJoin(a: JoinArgs): boolean {
    const m: Record<string, unknown> = { t: 'join', name: a.name ?? 'player', session: this.ctx.wallet.session.publicKey.toBase58(), authority: this.ctx.wallet.burner.publicKey.toBase58(), spectate: !!a.spectate };
    if (a.slot !== undefined) m.slot = a.slot;
    if (a.match !== undefined) m.match = a.match;
    return this.sendText(m);
  }

  async join(args: JoinArgs): Promise<JoinResult> {
    if (this.joinPending) return { ok: false, error: 'join already pending' };
    if (!this.ws || this.ws.readyState !== OPEN) {
      await new Promise<void>((res) => { const t = this.ctx.clock.setTimeout(res, 2000); this.openWaiters.push(() => { this.ctx.clock.clearTimeout(t); res(); }); });
    }
    this.joinArgs = args;
    return new Promise<JoinResult>((resolve) => {
      const timer = this.ctx.clock.setTimeout(() => this.finishJoin({ ok: false, error: 'join timeout' }), this.ecfg.joinTimeoutMs ?? 5000);
      this.joinPending = { resolve, timer };
      if (!this.sendJoin(args)) this.finishJoin({ ok: false, error: 'not connected' });
    });
  }

  sendInputs(batch: Uint8Array): number {
    const id = (this.txid = (this.txid + 1) >>> 0 || 1);
    const bad = validateInputBatch(batch);
    if (bad) { this.ctx.stats.sendErrors++; this.ctx.events.push({ type: 'error', where: 'sendInputs', txid: id, why: bad }); return id; }
    if (!this.ws || this.ws.readyState !== OPEN) {
      this.ctx.stats.sendErrors++;
      this.ctx.events.push({ type: 'error', where: 'sendInputs', txid: id, why: 'not connected' });
      return id;
    }
    this.ws.send(encodeSubmitFrame(id, batch));
    this.sent.set(id, this.ctx.clock.now());
    this.ctx.stats.inputs.hit(this.ctx.clock.now());
    return id;
  }

  subscribe(addr: string): void {
    if (!this.subs.size) this.ctx.intake.reset();
    this.subs.add(addr);
    this.poll();
  }

  unsubscribe(addr: string): void { this.subs.delete(addr); }

  /** The emulator has no session program: session actions are simulated locally (state only). */
  async session(action: string, args: Record<string, unknown>): Promise<Record<string, unknown>> {
    const w = this.ctx.wallet;
    const nowSec = Math.floor(this.ctx.clock.wallMs() / 1000);
    if (action === 'create') {
      const validUntil = nowSec + Math.min(Number(args.validitySec ?? this.ctx.cfg.session.validitySec ?? 3600), 7 * 86400);
      w.saveSessionMeta({ authority: w.burner.publicKey.toBase58(), feePayer: w.burner.publicKey.toBase58(), validUntil, token: 'emulated' });
      this.sessionState = { state: 'active', validUntil, emulated: true };
    } else if (action === 'topup') {
      this.sessionState = { ...this.sessionState, toppedUp: Number(args.lamports ?? 0), emulated: true };
    } else if (action === 'revoke') {
      w.rotateSession(this.ctx.clock.wallMs());
      this.sessionState = { state: 'revoked', emulated: true };
    } else if (action !== 'status') {
      return { ok: false, error: 'unknown session action ' + action };
    }
    if (action !== 'status') this.ctx.events.push({ type: 'session', action, ...this.sessionState });
    const vu = w.sessionMeta.validUntil;
    return { ok: true, emulated: true, sessionPubkey: w.session.publicKey.toBase58(), validUntil: vu, expired: vu > 0 && nowSec >= vu, ...this.sessionState };
  }

  status() { return { transport: 'emulator', connected: !!this.ws && this.ws.readyState === OPEN, joined: this.joined, subs: [...this.subs], url: this.ecfg.url }; }

  stop(): void {
    this.stopped = true;
    const c = this.ctx.clock;
    c.clearInterval(this.pingTimer); c.clearInterval(this.pollTimer);
    if (this.reconnectTimer !== null) c.clearTimeout(this.reconnectTimer);
    if (this.ws) { const ws = this.ws; this.ws = null; try { ws.close(); } catch { /* ignore */ } }
  }
}
