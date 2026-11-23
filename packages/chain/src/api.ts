// globalThis.FallRushChain — the polling-friendly API the Unity .jslib bridge calls.
// Async calls return Promise<string> (JSON); everything else is synchronous.
import type { Clock } from './clock.ts';
import { realClock } from './clock.ts';
import { DEFAULTS, parseConfig } from './config.ts';
import type { ChainConfig } from './config.ts';
import { EventLog, Stats, WorldIntake } from './intake.ts';
import type { KV } from './storage.ts';
import { browserKV } from './storage.ts';
import { phantomConnect, phantomSignMessage, phantomState, Wallet } from './wallet.ts';
import type { Ctx, Transport } from './transport.ts';
import { errMsg } from './transport.ts';
import { EmulatorTransport } from './emulator.ts';
import type { WSCtor } from './emulator.ts';
import { RollupTransport } from './rollup.ts';
import type { ConnFactory } from './rollup.ts';

export const BUNDLE_VERSION = '0.1.0';

export interface Deps {
  clock?: Clock;
  kv?: KV;
  WebSocket?: WSCtor;
  connFactory?: ConnFactory;
}

export function createChain(deps: Deps = {}) {
  const clock = deps.clock ?? realClock;
  let kvInfo: { kv: KV; persistent: boolean } | null = deps.kv ? { kv: deps.kv, persistent: true } : null;
  let cfg: ChainConfig | null = null;
  let ctx: Ctx | null = null;
  let transport: Transport | null = null;
  const fallbackEvents = new EventLog(DEFAULTS.maxEvents, clock);

  const need = () => { if (!ctx || !transport) throw new Error('FallRushChain: call init() first'); return { ctx, transport }; };
  const ok = (o: Record<string, unknown>) => JSON.stringify({ ok: true, ...o });
  const fail = (e: unknown) => JSON.stringify({ ok: false, error: errMsg(e) });

  const api = {
    version: BUNDLE_VERSION,

    async init(configJson: string): Promise<string> {
      try {
        if (transport) { transport.stop(); transport = null; }
        cfg = parseConfig(configJson);
        if (!kvInfo) kvInfo = browserKV();
        const wallet = new Wallet(kvInfo.kv, cfg.storage?.burnerKey ?? DEFAULTS.burnerKey, cfg.storage?.sessionKey ?? DEFAULTS.sessionKey, () => clock.wallMs());
        const events = new EventLog(cfg.queue?.maxEvents ?? DEFAULTS.maxEvents, clock);
        const stats = new Stats();
        const intake = new WorldIntake(cfg.world, cfg.queue?.maxWorlds ?? DEFAULTS.maxWorlds, clock, events, stats);
        ctx = { cfg, clock, events, stats, intake, wallet };
        if (cfg.transport === 'emulator') {
          const WS = deps.WebSocket ?? (globalThis as unknown as { WebSocket?: WSCtor }).WebSocket;
          if (!WS) throw new Error('no WebSocket available');
          transport = new EmulatorTransport(ctx, WS);
        } else {
          const f = deps.connFactory ?? (await import('./web3conn.ts')).web3ConnFactory;
          transport = new RollupTransport(ctx, f);
        }
        if (wallet.burnerCreated) events.push({ type: 'burnerCreated', pubkey: wallet.burner.publicKey.toBase58() });
        await transport.start();
        return ok({ version: BUNDLE_VERSION, name: cfg.name, transport: cfg.transport, persistentStorage: kvInfo.persistent, ...JSON.parse(api.wallet()) });
      } catch (e) {
        return fail(e);
      }
    },

    wallet(): string {
      if (!ctx) return JSON.stringify({ ok: false, error: 'not initialised', phantom: phantomState() });
      const w = ctx.wallet;
      return JSON.stringify({ burner: w.burner.publicKey.toBase58(), sessionPubkey: w.session.publicKey.toBase58(), session: { token: w.sessionMeta.token, validUntil: w.sessionMeta.validUntil, authority: w.sessionMeta.authority }, phantom: phantomState() });
    },

    exportBurner(): string { return need().ctx.wallet.exportBurner(); },

    importBurner(secretB58: string): string {
      try { need().ctx.wallet.importBurner(secretB58); return ok({ burner: ctx!.wallet.burner.publicKey.toBase58() }); } catch (e) { return fail(e); }
    },

    phantomConnect,
    phantomSignMessage,

    async session(action: string, argsJson?: string): Promise<string> {
      try {
        const { transport } = need();
        const r = await transport.session(action, argsJson ? JSON.parse(argsJson) : {});
        return JSON.stringify(r);
      } catch (e) { return fail(e); }
    },

    /** P4 economy: chips | state | bet {k, outcome, amount} | drop {kind} | receipt {match} | claim {asset, to}. */
    async econ(action: string, argsJson?: string): Promise<string> {
      try {
        const { transport } = need();
        if (!transport.econ) return JSON.stringify({ ok: false, error: 'no economy on this transport (' + transport.kind + ')' });
        return JSON.stringify(await transport.econ(action, argsJson ? JSON.parse(argsJson) : {}));
      } catch (e) { return fail(e); }
    },

    async join(argsJson?: string): Promise<string> {
      try {
        const { transport } = need();
        return JSON.stringify(await transport.join(argsJson ? JSON.parse(argsJson) : {}));
      } catch (e) { return fail(e); }
    },

    /** InputBatch bytes (wire-v0 §2). Returns the local txid (u32); 0 when not initialised. */
    sendInputs(bytes: Uint8Array): number {
      if (!transport) return 0;
      return transport.sendInputs(bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes as ArrayLike<number>));
    },

    subscribe(addr: string): string { try { need().transport.subscribe(addr || '$world'); return ok({}); } catch (e) { return fail(e); } },
    unsubscribe(addr: string): string { try { need().transport.unsubscribe(addr || '$world'); return ok({}); } catch (e) { return fail(e); } },

    /** Framed copy of queued Worlds into `into`: per entry u32 len | f64 recvMs | u32 seq | WorldView bytes. Returns bytes written. */
    drainWorlds(into: Uint8Array, maxItems = 64): number { return ctx ? ctx.intake.drain(into, maxItems) : 0; },
    pendingWorlds(): number { return ctx ? ctx.intake.length : 0; },
    latestWorldBase64(): string { return ctx ? ctx.intake.latestBase64() : ''; },
    latestTick(): number { return ctx ? ctx.stats.lastTick : -1; },

    drainEvents(): string { return JSON.stringify((ctx ? ctx.events : fallbackEvents).drain()); },

    stats(): string {
      if (!ctx || !transport) return JSON.stringify({ ok: false, error: 'not initialised' });
      return JSON.stringify({ ...ctx.stats.snapshot(clock.now()), queued: ctx.intake.length, eventsDropped: ctx.events.dropped, ...transport.status() });
    },

    /** Node relay helpers (rollup only): base balances; fund the burner from a local keypair (secret base58). */
    async devBalances(): Promise<string> {
      try { const t = need().transport as unknown as { balances?: () => Promise<Record<string, unknown>> }; if (!t.balances) throw new Error('rollup transport only'); return ok(await t.balances()); } catch (e) { return fail(e); }
    },
    async devFund(fromSecretB58: string, lamports: number): Promise<string> {
      try { const t = need().transport as unknown as { fundBurner?: (s: string, l: number) => Promise<Record<string, unknown>> }; if (!t.fundBurner) throw new Error('rollup transport only'); return JSON.stringify(await t.fundBurner(fromSecretB58, lamports)); } catch (e) { return fail(e); }
    },

    stop(): void { if (transport) transport.stop(); transport = null; },
  };
  return api;
}

export type FallRushChainApi = ReturnType<typeof createChain>;
