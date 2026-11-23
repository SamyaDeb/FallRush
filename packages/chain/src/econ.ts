// P4 economy in the browser bundle (and the Mac relay): RUSH chips (ChipBalance PDA: init + delegate by the burner,
// faucet on the rollup), Rush Markets (read, bet), Chaos Drops (read, buy), the receipt, and "Claim with Phantom"
// (Metaplex Core TransferV1 signed by the burner that owns the asset). Every write is a fall_rush instruction:
// `econ(op, a, b)` (programs/fall_rush/src/econ.rs), signed by the session key when the burner holds a valid
// SessionTokenV2 (no popup), else by the burner itself. Devnet test chips only, no real-money value.
import { PublicKey, SystemProgram, TransactionInstruction } from '@solana/web3.js';
import type { AccountMeta, Keypair } from '@solana/web3.js';

/** sha256("global:econ")[0..8] (IDL). */
export const IX_ECON = Uint8Array.from([0xf5, 0xf1, 0x56, 0x68, 0xf0, 0x7d, 0x24, 0x3a]);
export const OP = { INIT_CHIPS: 1, FAUCET: 2, CASHOUT: 3, MINT_CASHOUT: 4, PLACE_BET: 7, SETTLE: 8, BUY_DROP: 9 } as const;
export const ONE = 1_000_000n;
export const DROP_PRICE = [0, 50, 40, 30, 40];
export const DROP_NAMES = ['', 'LID FRENZY', 'BUMPER RAIN', 'WIND GUST', 'SWEEPER RUSH'];
export const MARKET_NAMES = ['Champion', 'Final winner', 'Qualify R1', 'Qualify R2', 'Qualify R3'];
export const LABELS = ['', 'program-settled', 'server-recorded', 'Classic fallback (refund)', 'aborted (refund)', 'no winner (refund)'];
export const CORE = new PublicKey('CoREENxT6tW1HoK8ypY1SxRMZTcVPm7R94rH4PZNhX7d');
export const DELEGATION = new PublicKey('DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh');
const te = new TextEncoder();

export function econPdas(program: PublicKey, arena: PublicKey, arenaId: number) {
  const P = (s: Uint8Array[]) => PublicKey.findProgramAddressSync(s, program)[0];
  return {
    chips: (w: PublicKey) => P([te.encode('chips'), w.toBytes()]),
    market: (k: number) => P([te.encode('market'), Uint8Array.of(arenaId), Uint8Array.of(k)]),
    drops: P([te.encode('drops'), arena.toBytes()]),
    crown: (m: PublicKey) => P([te.encode('crown'), m.toBytes()]),
    capsule: (m: PublicKey, s: number) => P([te.encode('capsule'), m.toBytes(), Uint8Array.of(s)]),
    match: (id: Uint8Array) => P([te.encode('match'), id]),
  };
}

export function econIx(program: PublicKey, signer: PublicKey, op: number, a: bigint | number, b: bigint | number, accs: AccountMeta[]): TransactionInstruction {
  const d = new Uint8Array(8 + 1 + 16);
  d.set(IX_ECON, 0);
  d[8] = op;
  const dv = new DataView(d.buffer);
  dv.setBigUint64(9, BigInt(a), true);
  dv.setBigUint64(17, BigInt(b), true);
  return new TransactionInstruction({ programId: program, keys: [{ pubkey: signer, isSigner: true, isWritable: true }, ...accs], data: Buffer.from(d) });
}

/** delegate_sim(kind 6 = chips, 0, 0) with the validator: the burner delegates its own ChipBalance. */
export function delegateChipsIx(program: PublicKey, payer: PublicKey, chips: PublicKey, validator: PublicKey, discDelegateSim: Uint8Array): TransactionInstruction {
  const seeds = (s: string, ...x: Uint8Array[]) => PublicKey.findProgramAddressSync([te.encode(s), ...x], DELEGATION)[0];
  const ownerProgram = program;
  const buffer = PublicKey.findProgramAddressSync([te.encode('buffer'), chips.toBytes()], program)[0];
  const record = seeds('delegation', chips.toBytes());
  const meta = seeds('delegation-metadata', chips.toBytes());
  const d = new Uint8Array(8 + 3);
  d.set(discDelegateSim, 0);
  d[8] = 6; d[9] = 0; d[10] = 0;
  // DelegateSim accounts (Anchor #[delegate], fall_rush lib.rs): authority, config, buffer_pda, delegation_record_pda,
  // delegation_metadata_pda, pda, owner_program, delegation_program, system_program; + the validator
  const config = PublicKey.findProgramAddressSync([te.encode('config')], program)[0];
  return new TransactionInstruction({
    programId: program,
    keys: [
      { pubkey: payer, isSigner: true, isWritable: true },
      { pubkey: config, isSigner: false, isWritable: false },
      { pubkey: buffer, isSigner: false, isWritable: true },
      { pubkey: record, isSigner: false, isWritable: true },
      { pubkey: meta, isSigner: false, isWritable: true },
      { pubkey: chips, isSigner: false, isWritable: true },
      { pubkey: ownerProgram, isSigner: false, isWritable: false },
      { pubkey: DELEGATION, isSigner: false, isWritable: false },
      { pubkey: SystemProgram.programId, isSigner: false, isWritable: false },
      { pubkey: validator, isSigner: false, isWritable: false },
    ],
    data: Buffer.from(d),
  });
}

// ---- decoders (zero-copy layouts) ---------------------------------------------------------------------------------
const rdU64 = (d: Uint8Array, o: number) => new DataView(d.buffer, d.byteOffset, d.byteLength).getBigUint64(o, true);
const rdU16 = (d: Uint8Array, o: number) => d[o] | (d[o + 1] << 8);
const rdU32 = (d: Uint8Array, o: number) => new DataView(d.buffer, d.byteOffset, d.byteLength).getUint32(o, true);

export function decodeChips(d: Uint8Array) {
  return { wallet: new PublicKey(d.subarray(8, 40)).toBase58(), balance: rdU64(d, 40), faucetTotal: rdU64(d, 48), cashedOut: rdU64(d, 56), won: rdU64(d, 64), spent: rdU64(d, 72), faucetAt: Number(new DataView(d.buffer, d.byteOffset).getBigInt64(80, true)) };
}

export function decodeMarket(d: Uint8Array) {
  const stake: bigint[] = [];
  for (let i = 0; i < 12; i++) stake.push(rdU64(d, 104 + 8 * i));
  const n = d[207];
  const bets = [];
  for (let i = 0; i < n; i++) { const b = 222 + 48 * i; bets.push({ wallet: new PublicKey(d.subarray(b, b + 32)).toBase58(), amount: rdU64(d, b + 32), outcome: d[b + 40], paid: d[b + 41] }); }
  return { match: new PublicKey(d.subarray(8, 40)).toBase58(), pool: rdU64(d, 72), winningStake: rdU64(d, 80), paidTotal: rdU64(d, 88), stake, winners: rdU16(d, 200), kind: d[203], round: d[204], status: d[205], label: d[206], nBets: n, refund: d[209], bets };
}

export function decodeDrops(d: Uint8Array) {
  const count = d[92];
  const buys = [];
  for (let i = 0; i < count; i++) { const b = 96 + 40 * i; buys.push({ by: new PublicKey(d.subarray(b, b + 32)).toBase58(), tick: rdU32(d, b + 32), kind: d[b + 36] }); }
  return { match: new PublicKey(d.subarray(40, 72)).toBase58(), pot: rdU64(d, 72), lastTick: rdU32(d, 88), count, buys };
}

export function decodeReceipt(d: Uint8Array) {
  const r = 295;
  const hex = (a: number, b: number) => Array.from(d.subarray(a, b), (x) => x.toString(16).padStart(2, '0')).join('');
  const markets = [];
  for (let k = 0; k < 5; k++) { const b = r + 617 + 20 * k; markets.push({ pool: rdU64(d, b), winningStake: rdU64(d, b + 8), winners: rdU16(d, b + 16), label: d[b + 18], status: d[b + 19] }); }
  const wallets = [];
  for (let s = 0; s < 12; s++) wallets.push(new PublicKey(d.subarray(r + 209 + 32 * s, r + 241 + 32 * s)).toBase58());
  return {
    status: d[26], flags: d[27], champion: d[294], round: d[129],
    receipt: { flags: d[r], trust: d[r + 1], coreVersion: rdU16(d, r + 2), humanMask: rdU16(d, r + 4), mintedMask: rdU16(d, r + 6), drops: d[r + 8], dropSpent: rdU64(d, r + 9),
      mapHash: hex(r + 17, r + 49), preFinal: hex(r + 49, r + 81), finalTranscript: hex(r + 81, r + 113), dropLog: hex(r + 113, r + 145), capsuleSeed: hex(r + 145, r + 177),
      crown: new PublicKey(d.subarray(r + 177, r + 209)).toBase58(), wallets, placements: Array.from(d.subarray(r + 593, r + 605)), rarity: Array.from(d.subarray(r + 605, r + 617)), markets },
  };
}

/** Metaplex Core TransferV1 (discriminator 14, compression proof None): the owner moves its asset to `to`. */
export function coreTransferIx(asset: PublicKey, owner: PublicKey, payer: PublicKey, to: PublicKey): TransactionInstruction {
  return new TransactionInstruction({
    programId: CORE,
    keys: [
      { pubkey: asset, isSigner: false, isWritable: true },
      { pubkey: CORE, isSigner: false, isWritable: false }, // collection: none
      { pubkey: payer, isSigner: true, isWritable: true },
      { pubkey: owner, isSigner: true, isWritable: false }, // authority = the current owner
      { pubkey: to, isSigner: false, isWritable: false },
      { pubkey: SystemProgram.programId, isSigner: false, isWritable: false },
      { pubkey: CORE, isSigner: false, isWritable: false }, // log wrapper: none
    ],
    data: Buffer.from([14, 0]),
  });
}

// ---- P5: real RUSH SPL (cash-out, transfers) ----------------------------------------------------------------------
export const RUSH_MINT = new PublicKey('3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw');
export const TOKEN = new PublicKey('TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA');
export const ATA_PROGRAM = new PublicKey('ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL');
export const ataOf = (owner: PublicKey, mint: PublicKey) => PublicKey.findProgramAddressSync([owner.toBytes(), TOKEN.toBytes(), mint.toBytes()], ATA_PROGRAM)[0];

/** econ(MINT_CASHOUT) on Solana (permissionless; `payer` pays the stamp + ATA rent). Accounts (econ.rs
 * op_mint_cashout): chips, cashout stamp, mint, mint authority PDA, wallet, wallet ATA, token, ATA program, system. */
export function mintCashoutIx(program: PublicKey, payer: PublicKey, wallet: PublicKey): TransactionInstruction {
  const P = (s: Uint8Array[]) => PublicKey.findProgramAddressSync(s, program)[0];
  const a = (pubkey: PublicKey, isWritable: boolean) => ({ pubkey, isSigner: false, isWritable });
  return econIx(program, payer, OP.MINT_CASHOUT, 0, 0, [
    a(P([te.encode('chips'), wallet.toBytes()]), false), a(P([te.encode('cashout'), wallet.toBytes()]), true), a(RUSH_MINT, true),
    a(P([te.encode('rush_mint_auth')]), false), a(wallet, false), a(ataOf(wallet, RUSH_MINT), true), a(TOKEN, false), a(ATA_PROGRAM, false), a(SystemProgram.programId, false),
  ]);
}

/** Create the recipient's RUSH ATA (idempotent) + SPL TransferChecked from `from`'s ATA. */
export function rushTransferIxs(from: PublicKey, to: PublicKey, amount: bigint): TransactionInstruction[] {
  const src = ataOf(from, RUSH_MINT), dst = ataOf(to, RUSH_MINT);
  const create = new TransactionInstruction({
    programId: ATA_PROGRAM,
    keys: [
      { pubkey: from, isSigner: true, isWritable: true }, { pubkey: dst, isSigner: false, isWritable: true }, { pubkey: to, isSigner: false, isWritable: false },
      { pubkey: RUSH_MINT, isSigner: false, isWritable: false }, { pubkey: SystemProgram.programId, isSigner: false, isWritable: false }, { pubkey: TOKEN, isSigner: false, isWritable: false },
    ],
    data: Buffer.from([1]),
  });
  const d = new Uint8Array(10);
  d[0] = 12; // TransferChecked
  new DataView(d.buffer).setBigUint64(1, amount, true);
  d[9] = 6;
  const xfer = new TransactionInstruction({
    programId: TOKEN,
    keys: [{ pubkey: src, isSigner: false, isWritable: true }, { pubkey: RUSH_MINT, isSigner: false, isWritable: false }, { pubkey: dst, isSigner: false, isWritable: true }, { pubkey: from, isSigner: true, isWritable: false }],
    data: Buffer.from(d),
  });
  return [create, xfer];
}

export type Signer = Keypair;
