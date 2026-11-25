// harden: the keeper daemon's decisions as pure functions (no RPC, no keys, no side effects at import), so they can be
// unit-tested without devnet (tools/keeper/policy.test.ts: node --experimental-strip-types --test tools/keeper/policy.test.ts).
// daemon.ts wires them to the chain. Nothing here touches the simulation (rush-core / World bytes / slice semantics).

// ------------------------------------------------------------------ balances (#1)
export const LAMPORTS_PER_SOL = 1_000_000_000;
/** Core asset rent paid by the treasury per minted Crown / capsule (measured 3.84M–3.88M; 4.2M leaves margin). */
export const ASSET_RENT_LAMPORTS = 4_200_000;
export const TREASURY_FLOOR_LAMPORTS = 20_000_000;
/** review: the default top-up target: above the 0.1 SOL low threshold, so a topped-up treasury never reads "low". */
export const TREASURY_TOPUP_DEFAULT = 150_000_000;

/** Assets the receipt still owes (Crown + one capsule per seeded human slot not minted yet). */
export function owedCount(r: { flags: number; champion: number; humanMask: number; mintedMask: number } | null | undefined): number {
  if (!r) return 0;
  const crown = (r.flags & (4 | 16)) === 0 && r.champion < 12 ? 1 : 0;
  let caps = 0;
  if (r.flags & 2) for (let s = 0; s < 12; s++) if ((r.humanMask >> s) & 1 && !((r.mintedMask >> s) & 1)) caps++;
  return crown + caps;
}

/** Lamports to send to the treasury before a finalize that will mint `owed` assets (0 = enough). The treasury is topped
 * up to `target` (or to the need, whichever is larger) when it is below max(floor, owed × asset rent). */
export function treasuryTopUp(balance: number, owed: number, target = 60_000_000, floor = TREASURY_FLOOR_LAMPORTS): number {
  if (!Number.isFinite(balance) || balance < 0) return 0; // unknown balance: do not guess (the finalize falls back)
  const need = Math.max(floor, owed * ASSET_RENT_LAMPORTS);
  if (balance >= need) return 0;
  return Math.max(target, need) - balance;
}

export type Balances = { authorityLamports: number | null; treasuryLamports: number | null; funderLamports: number | null };
export const LOW_DEFAULTS = { authority: 0.3 * LAMPORTS_PER_SOL, treasury: 0.1 * LAMPORTS_PER_SOL, funder: 0.2 * LAMPORTS_PER_SOL };
/** lowBalance flags: a wallet is low when its balance is known (≥ 0) and below its threshold. */
export function lowBalance(b: Balances, t = LOW_DEFAULTS) {
  const low = (v: number | null, th: number) => v != null && v >= 0 && v < th;
  const flags = { authority: low(b.authorityLamports, t.authority), treasury: low(b.treasuryLamports, t.treasury), funder: low(b.funderLamports, t.funder) };
  return { ...flags, any: flags.authority || flags.treasury || flags.funder };
}

// ------------------------------------------------------------------ seasons (#2)
/** MagicBlock: a delegated account without a fee payer stops committing after 10 commits. */
export const COMMIT_CAP = 10;
/** delegation-metadata account (delegation program): last_update_nonce is the u64 at offset 8. */
export function delegationNonce(data: Uint8Array | null | undefined): number | null {
  if (!data || data.length < 16) return null;
  return Number(Buffer.from(data.buffer, data.byteOffset, data.byteLength).readBigUInt64LE(8));
}
export function commitCapped(nonces: (number | null)[], cap = COMMIT_CAP): boolean {
  return nonces.some((n) => n != null && n >= cap);
}

// ------------------------------------------------------------------ the market maker (#7)
export type MmView = { status: string; currentRound: number; flags: number; alive: number[] };
/** Which pools the market maker seeds now. humanSlots = the roster's human slots (null = unknown: no human filter);
 * worldRound / worldPhase = the World's round (byte 139) and the byte the hybrid Final gate reads (199). */
export function mmSeeds(m: MmView, o: { judge: boolean; full: boolean; humanSlots: number[] | null; worldRound?: number; worldPhase?: number }): { tag: string; k: number }[] {
  const out: { tag: string; k: number }[] = [];
  const humanAlive = o.humanSlots == null || o.humanSlots.some((s) => m.alive.includes(s));
  if (m.currentRound === 0 && ["created", "dealing", "dealt"].includes(m.status)) {
    out.push({ tag: "champion", k: 0 });
    if (!o.judge) out.push({ tag: "q1", k: 2 });
  }
  // a Qualify(r) pool only when round r can still be played (≥ 2 alive: a crowned-early match leaves it unsettleable,
  // prog-qualify-unplayed-round) and a human is still alive (no seeded pools on a bot-only remainder)
  if (!o.judge && m.status === "roundResolved" && m.alive.length >= 2 && humanAlive) {
    if (m.currentRound === 1) out.push({ tag: "q2", k: 3 });
    if (m.currentRound === 2) out.push({ tag: "q3", k: 4 });
  }
  // FinalWinner: the hybrid Final (flag 0x40) or a running full on-chain match (0x20 without 0x40)
  const finalMode = (m.flags & 0x40) !== 0 || (o.full && (m.flags & 0x20) !== 0);
  if (finalMode && humanAlive && o.worldRound === 4 && o.worldPhase != null && o.worldPhase < 3) out.push({ tag: "final", k: 1 });
  return out;
}

// ------------------------------------------------------------------ server abandon (#6)
/** True once the Classic server has, for `sustainMs`, reported (fresh status) that it no longer plays `matchHex`. */
export function orphanDetector(matchHex: string, sustainMs = 10_000, freshMs = 10_000) {
  let since = 0;
  return (sv: { t?: number; inMatch?: boolean; match?: string } | null, now = Date.now()): boolean => {
    const gone = !!sv && now - (sv.t ?? 0) < freshMs && (!sv.inMatch || (!!sv.match && sv.match !== matchHex));
    if (!gone) { since = 0; return false; }
    if (!since) since = now;
    return now - since >= sustainMs;
  };
}

// ------------------------------------------------------------------ retries, deadlines, precheck (#4, #5, #7)
export const RETRY_BACKOFF_S = [2, 10, 30, 60, 120];
export const ENSURE_BACKOFF_MS = [1000, 2000, 5000, 10_000];
/** runFinal's deadline: min(t0 + cap, firstStartedAt + firstCap) (full: 35 min / 20 min). */
export function finalDeadline(t0: number, full: boolean, firstStartedAt?: number | null): number {
  const cap = t0 + (full ? 35 : 20) * 60_000;
  return full && firstStartedAt ? Math.min(cap, firstStartedAt + 20 * 60_000) : cap;
}
/** The server already played this match on Classic (or ended it) and no Match exists on-chain: nothing to do. */
export function skipClassic(serverState: string | null, onchainAll: boolean, matchExists: boolean): boolean {
  return onchainAll && !matchExists && serverState != null && ["fallback", "done", "aborted"].includes(serverState);
}
export const isMarketBusy = (e: unknown) => /MarketBusy|0x1791|"Custom":6033\b|custom program error: 6033\b/.test(String((e as any)?.message ?? e));
export const isAlreadyFinal = (e: unknown) => /AlreadyFinal|0x1780|"Custom":6016\b/.test(String((e as any)?.message ?? e));

// ------------------------------------------------------------------ ensureFinal (#4)
export type EnsureDeps = {
  /** the arena's status (2 = RUNNING) + whether it runs this match + the World phase */
  arena: () => Promise<{ running: boolean; runsThis: boolean; phase: number }>;
  /** the Match's status name on the rollup (null = unreadable) */
  matchStatus: () => Promise<string | null>;
  abortArena: () => Promise<unknown>;
  abortMatch: (reason: number) => Promise<unknown>;
  sleep: (ms: number) => Promise<unknown>;
  log: (o: object) => void;
};
export const DONE_PHASE = 11;
/**
 * Never leave a non-final Match with bets: stop the arena when it runs this match (retried until it does not), then
 * abort the Match (retried; AlreadyFinal = ok). Guard: a RUNNING arena on this match with phase < DONE is never
 * stopped unless `force` (a server fallback / orphan / deadline fired). Returns the outcome; throws only when the
 * retries are exhausted.
 */
export async function ensureFinalCore(d: EnsureDeps, reason: number, force: boolean, tries = 8): Promise<"final" | "aborted" | "guarded"> {
  const a = await d.arena();
  if (a.running && a.runsThis && a.phase < DONE_PHASE) {
    if (!force) { d.log({ ev: "ensure_final_guarded", reason, phase: a.phase }); return "guarded"; }
    for (let i = 0; ; i++) {
      try { await d.abortArena(); } catch (e: any) { d.log({ ev: "abort_arena_retry", try: i, err: String(e?.message ?? e).slice(0, 200) }); }
      const b = await d.arena().catch(() => null);
      if (b && !(b.running && b.runsThis)) break;
      if (i + 1 >= tries) throw new Error("ensureFinal: abort_arena did not land");
      await d.sleep(ENSURE_BACKOFF_MS[Math.min(i, ENSURE_BACKOFF_MS.length - 1)]);
    }
  }
  for (let i = 0; ; i++) {
    const s = await d.matchStatus().catch(() => null);
    if (s === "completed" || s === "aborted") return i === 0 ? "final" : "aborted";
    if (s != null) {
      try { await d.abortMatch(reason); }
      catch (e: any) {
        if (!isAlreadyFinal(e)) d.log({ ev: "abort_match_retry", try: i, err: String(e?.message ?? e).slice(0, 200) });
      }
    }
    if (i + 1 >= tries) throw new Error("ensureFinal: abort_match did not land");
    await d.sleep(ENSURE_BACKOFF_MS[Math.min(i, ENSURE_BACKOFF_MS.length - 1)]);
  }
}

// ------------------------------------------------------------------ runFinal cleanup (#5)
/** Everything runFinal started, stopped whatever happened (each step isolated: one failure never skips the next). */
export async function cleanupFinal(o: { crank: { stop: () => Promise<any> } | null; unsubscribe: () => Promise<unknown>; tasks: string[]; cancel: (t: string) => Promise<unknown>; log?: (o: object) => void }) {
  let crankStats: any = null;
  if (o.crank) { try { crankStats = await o.crank.stop(); } catch (e: any) { o.log?.({ ev: "crank_stop_failed", err: String(e?.message ?? e).slice(0, 200) }); } }
  try { await o.unsubscribe(); } catch {}
  for (const t of o.tasks.splice(0)) { try { await o.cancel(t); } catch {} }
  return crankStats;
}

// ------------------------------------------------------------------ summary merge (#7)
const empty = (v: unknown) => v == null || (typeof v === "object" && Object.keys(v as object).length === 0);
/** A re-driven match never wipes what an earlier run recorded: new non-empty fields win, empty ones keep the old value;
 * `sigs` merge key by key; `startedAt` stays the first one. */
export function mergeSummary(old: any, cur: any): any {
  if (!old || typeof old !== "object") return cur;
  const out: any = { ...old };
  for (const [k, v] of Object.entries(cur)) {
    if (k === "sigs") { out.sigs = { ...(old.sigs ?? {}) }; for (const [s, x] of Object.entries(v ?? {})) if (x) out.sigs[s] = x; continue; }
    if (empty(v) && !empty(old[k])) continue;
    out[k] = v;
  }
  if (old.startedAt) out.startedAt = old.startedAt;
  if (old.startedAt && cur.startedAt && cur.startedAt !== old.startedAt) out.redrivenAt = cur.startedAt;
  return out;
}

// ------------------------------------------------------------------ MagicBlock version (#11)
/** The MagicBlock core version from a getVersion result (the ER reports magicblock-core next to solana-core). */
export function mbCore(v: any): string | null {
  if (!v || typeof v !== "object") return null;
  return v["magicblock-core"] ?? v["magicblockCore"] ?? v["core"] ?? v["version"] ?? null;
}

// ------------------------------------------------------------------ stall recovery (#5)
export type StallDeps = {
  /** cancel one tracked task id (errors are swallowed here) */
  cancel: (t: string) => Promise<unknown>;
  /** schedule one timed task and return its id (errors are swallowed here: the next stall retries) */
  schedule: () => Promise<string>;
  /** hybrid drive mode: a fresh blockhash + one fire-and-forget tick() send */
  blockhash: () => Promise<string>;
  sendTick: (bh: string, nonce: number) => void;
  gaveUp: () => boolean;
  sleep: (ms: number) => Promise<unknown>;
  now?: () => number;
  log?: (o: object) => void;
};
/**
 * After STALL_MS without World progress: cancel the tracked tasks, (hybrid / opt-in only) drive tick() for driveMs,
 * then schedule `nTasks` fresh tasks. Never throws: a failed blockhash keeps the last one (no blockhash at all = no
 * drive), a failed schedule is logged and retried at the next stall. In full mode the crank already drives the World,
 * so `flood` is false there by default (no 125 tx/s tick() burst next to the crank; the watch loop stays responsive).
 */
export async function stallRecover(d: StallDeps, tasks: string[], o: { nTasks: number; flood: boolean; driveMs: number; gapMs?: number }): Promise<{ sent: number; scheduled: number; bhErrors: number; gaveUp: boolean }> {
  const now = d.now ?? Date.now;
  for (const t of tasks.splice(0)) { try { await d.cancel(t); } catch {} }
  let sent = 0, bhErrors = 0, gave = false;
  if (o.flood && o.driveMs > 0) {
    let bh = "", bhAt = 0;
    const d0 = now();
    while (now() - d0 < o.driveMs) {
      if (d.gaveUp()) { gave = true; break; }
      if (!bh || now() - bhAt > 1500) {
        try { bh = await d.blockhash(); } catch { bhErrors++; }
        bhAt = now();
      }
      if (bh) { d.sendTick(bh, sent); sent++; }
      await d.sleep(o.gapMs ?? 8);
    }
  }
  let scheduled = 0;
  for (let k = 0; k < o.nTasks; k++) {
    try { tasks.push(await d.schedule()); scheduled++; } catch (e: any) { d.log?.({ ev: "reschedule_failed", err: String(e?.message ?? e).slice(0, 200) }); }
  }
  return { sent, scheduled, bhErrors, gaveUp: gave };
}
