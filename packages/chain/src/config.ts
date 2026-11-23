// Chain config (packages/chain/config/*.json). Every address, seed, discriminator and offset lives here, so a new
// program layout (P2's WorldAcc) only changes JSON. Field reference: packages/chain/README.md.

export type SeedSpec =
  | { utf8: string }
  | { hex: string }
  | { pubkey: string } // base58 or a "$ref"
  | { u8: number | string } // number or "$ref" resolving to a number
  | { u16le: number | string }
  | { u32le: number | string }
  | { u64le: number | string };

export interface AccountSpec {
  name?: string;
  /** base58 address or "$ref" (see README: refs) */
  address?: string;
  /** PDA: seeds + program (base58 or "$ref"; default = the instruction's program) */
  pda?: { seeds: SeedSpec[]; programId?: string };
  signer?: boolean;
  writable?: boolean;
  /** Anchor optional account: when the ref is unset, the program id is passed instead (Anchor's `None`). */
  optional?: boolean;
}

export type ArgSpec =
  | { type: 'raw'; from: string } // bytes copied as-is (e.g. "$batch")
  | { type: 'vec'; from: string } // borsh Vec<u8>: u32 LE length + bytes
  | { type: 'u8' | 'u16' | 'u32' | 'u64' | 'i64'; value?: number | string; from?: string }
  | { type: 'bool'; value?: boolean; from?: string }
  | { type: 'pubkey'; from: string }
  | { type: 'string'; from: string }; // borsh String: u32 LE length + utf8

export interface InstructionSpec {
  programId: string;
  /** 8-byte discriminator as 16 hex chars */
  discriminator: string;
  accounts: AccountSpec[];
  args: ArgSpec[];
}

export interface WorldLayout {
  /** byte offset of the World (rush_core::World::to_bytes) inside the account */
  offset: number;
  /** offset of the World length field, or null = (account length - offset) */
  lenOffset: number | null;
  /** width of the length field: 2 (u16, emulator) or 4 (u32, P2 WorldAcc). Default 2. */
  lenBytes?: 2 | 4;
  /** u8 that must be 0 for a complete tick (P2 `part_pending`); other snapshots (tick slices) are dropped. */
  completeOffset?: number | null;
  /** u8 director phase; with donePhase it fills WorldView flags when flagsOffset is null (bit0 live, bit1 over). */
  phaseOffset?: number | null;
  donePhase?: number;
  tickOffset: number;
  appliedTickOffset: number | null;
  appliedTickCount?: number; // default 12
  coreVersionOffset: number | null;
  /** width of the core version field: 4 (default) or 2 (P4 `WorldAcc.core_version`, LE u16 = rush_core::VERSION) */
  coreVersionBytes?: 2 | 4;
  flagsOffset: number | null;
  arenaSeqOffset?: number | null;
  /** optional magic bytes at offset 0 (hex) to reject foreign accounts */
  magicHex?: string | null;
}

export interface ChainConfig {
  version: 1;
  name: string;
  transport: 'emulator' | 'rollup';
  /** named addresses usable as "$name" refs (world, arena, map, …) */
  addresses: Record<string, string>;
  world: WorldLayout;
  storage?: { burnerKey?: string; sessionKey?: string };
  queue?: { maxWorlds?: number; maxEvents?: number };
  emulator?: {
    url: string;
    pollMs?: number; // backup {"t":"get"} interval while subscribed (1000)
    pingMs?: number; // RTT probe interval (1000)
    joinTimeoutMs?: number; // 5000
    reconnect?: { minMs?: number; maxMs?: number };
  };
  rollup?: {
    erHttp: string;
    erWs?: string | null;
    /** P3-K: subscribe with a plain accountSubscribe WebSocket (not web3.js onAccountChange). */
    rawWs?: boolean;
    /** Redundant raw sockets (default 2) and the silence after which one is reopened (default 600 ms). */
    rawSockets?: number;
    rawStallMs?: number;
    baseHttp: string;
    baseWs?: string | null;
    commitment?: 'processed' | 'confirmed' | 'finalized';
    baseTxPerSec?: number; // token bucket rate (≤ 1.5)
    baseBurst?: number; // token bucket capacity (1)
    confirmPollMs?: number; // getSignatureStatuses cadence (50)
    confirmBatch?: number; // signatures per call (≤ 256)
    confirmTimeoutMs?: number; // give up on a signature (5000)
    accountPollMs?: number; // backup getAccountInfo (1000)
    stallMs?: number; // re-subscribe if WS silent while polls advance (2000)
    rttPollMs?: number; // getSlot RTT probe (2000)
    blockhashRefreshMs?: number; // ER blockhash refresh (10000)
    clonerRetryMs?: number; // keep retrying "pending request owner … Cloner" this long (5000)
    clonerRetryEveryMs?: number; // 250
    inputClonerRetryMs?: number; // for input batches (stale fast): 300
    computeUnitLimit?: number | null; // add SetComputeUnitLimit to submit_input when set
    submitInput: InstructionSpec;
    /** P2 program join flow (docs/proof/P2-onchain-sim.md §2, §10). When set, join() uses it. */
    p2?: P2Config | null;
    /** P4: the rollup validator (delegation of the player's ChipBalance) */
    validator?: string;
    join?: {
      ix?: InstructionSpec | null;
      layer?: 'er' | 'base';
      /** read our slot from an account: entries of `stride` bytes starting at `offset`, key (32 B) at keyOffset */
      slotTable?: { account: string; offset: number; stride: number; count: number; keyOffset: number; key: 'authority' | 'session' } | null;
      map?: string;
      rate?: number;
      confirmTimeoutMs?: number;
    };
  };
  session: {
    programId: string; // gum session-keys (KeyspM2ss…)
    targetProgram: string; // base58 or "$ref"
    validitySec?: number; // ≤ 7 days (program limit)
    topUpLamports?: number; // create_session_v2 `lamports` (default program value 0.01 SOL)
    topUp?: boolean;
  };
}

export interface P2Config {
  /** Arena id (PDA ["arena", id]); addresses.arena overrides the derived PDA */
  arenaId: number;
  arenaLayout: { matchOffset: number; ownersOffset: number; walletsOffset: number; statusOffset: number; humanMaskOffset: number; minLen: number };
  arenaStatus: { lobby: number; running: number };
  matchLayout: { matchIdOffset: number; statusOffset: number; playerCountOffset: number; currentRoundOffset: number; roundTargetsOffset: number; slotsOffset: number; slotStride: number; minLen: number };
  /** Match.slots[s].state values */
  slotState: { alive: number };
  joinLobby: InstructionSpec;
  cosmetic?: number;
  /** create / reuse a SessionTokenV2 and join with the session signer (else the burner joins and signs inputs) */
  useSession?: boolean;
  watchMs?: number; // Arena / Match poll while joining (250)
  joinRetryMs?: number; // retry join_lobby while the rollup clones accounts (10000)
  startTimeoutMs?: number; // give up waiting for start_final (900000)
  lobbyTimeoutMs?: number; // give up waiting for open_lobby (900000)
  /** program error codes (IDL) for mapping confirm errors */
  errors?: { inputOutOfWindow?: number; slotTaken?: number; alreadyJoined?: number };
}

export const DEFAULTS = {
  maxWorlds: 64,
  maxEvents: 512,
  burnerKey: 'fallrush.burner.v1',
  sessionKey: 'fallrush.session.v1',
};

export function parseConfig(json: string): ChainConfig {
  const c = JSON.parse(json) as ChainConfig;
  const need = (cond: unknown, what: string) => {
    if (!cond) throw new Error('config: ' + what);
  };
  need(c && c.version === 1, 'version must be 1');
  need(c.transport === 'emulator' || c.transport === 'rollup', 'transport must be emulator|rollup');
  need(c.world && typeof c.world.offset === 'number' && typeof c.world.tickOffset === 'number', 'world.offset/tickOffset');
  need(c.session && c.session.programId && c.session.targetProgram, 'session.programId/targetProgram');
  c.addresses = c.addresses || {};
  if (c.transport === 'emulator') need(c.emulator && c.emulator.url, 'emulator.url');
  if (c.transport === 'rollup') {
    need(c.rollup && c.rollup.erHttp && c.rollup.baseHttp, 'rollup.erHttp/baseHttp');
    need(c.rollup!.submitInput && c.rollup!.submitInput.discriminator, 'rollup.submitInput');
    if ((c.rollup!.baseTxPerSec ?? 1.5) > 1.5) throw new Error('config: rollup.baseTxPerSec must be ≤ 1.5');
  }
  return c;
}
