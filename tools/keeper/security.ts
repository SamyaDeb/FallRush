// P2.8 security suite (local stack, arena SEC_ARENA=1). Each check prints PASS / FAIL; exit code = failures.
//   P2_NET=local ARENA=1 tsx tools/keeper/security.ts
import { Keypair, PublicKey, SystemProgram, LAMPORTS_PER_SOL, Transaction, TransactionInstruction } from "@solana/web3.js";
import { createHash } from "node:crypto";
import { randomBytes } from "node:crypto";
import { writeFileSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { NET, EP, base, er, wallet, pb, pe, pda, PROGRAM_ID, sendBase, sendEr, ROOT, MAGIC_CONTEXT, N, sleep, decodeWorld, tickAccounts, cu, BN } from "./lib.ts";

const ARENA = Number(process.env.ARENA ?? 1);
const arena = pda.arena(ARENA), world = pda.world(arena);
const IDL_ERRORS: any[] = JSON.parse(readFileSync(join(ROOT, "programs/target/idl/fall_rush.json"), "utf8")).errors;
const results: { check: string; pass: boolean; detail: string }[] = [];
const check = (name: string, pass: boolean, detail = "") => { results.push({ check: name, pass, detail }); console.log(`${pass ? "PASS" : "FAIL"} ${name} ${detail}`); };
const expectFail = async (name: string, p: Promise<{ err?: string }>, want: RegExp) => { const r = await p; check(name, !!r.err && want.test(r.err), r.err ? r.err.slice(0, 120) : "succeeded"); };
const errName = (code: number) => (IDL_ERRORS.find((e: any) => e.code === code)?.name ?? String(code));
const customCode = (err?: string) => { const m = err?.match(/"Custom":(\d+)/); return m ? Number(m[1]) : undefined; };
const expectCode = async (name: string, p: Promise<{ err?: string }>, want: string) => { const r = await p; const c = customCode(r.err); const got = c === undefined ? (r.err ?? "succeeded") : errName(c); check(name, got.toLowerCase() === want.toLowerCase(), got); };

const a0: any = await pe.account.arena.fetch(arena);
const maps: PublicKey[] = a0.maps;
const inputs = Array.from({ length: N }, (_, s) => pda.input(arena, s));
const inputMetas = inputs.map((pubkey) => ({ pubkey, isSigner: false, isWritable: true }));
// players (fee payers on the rollup are free; fund on base so they exist)
const p1 = Keypair.generate(), p2 = Keypair.generate();
for (const p of [p1, p2]) await sendBase([SystemProgram.transfer({ fromPubkey: wallet.publicKey, toPubkey: p.publicKey, lamports: LAMPORTS_PER_SOL / 10 })]);
await sleep(2000);
if (a0.status === 2) await sendEr([await pe.methods.abortArena().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()]);
// a match for this arena
const matchId = randomBytes(16), matchPda = pda.match(matchId);
await sendBase([await pb.methods.createMatch([...matchId], [...randomBytes(32)], N).accountsPartial({ authority: wallet.publicKey }).instruction()]);
for (let i = 0; i < 60; i++) { const ai = await base.getAccountInfo(matchPda, "confirmed").catch(() => null); if (ai && ai.owner.equals(PROGRAM_ID)) break; await sleep(500); } // RPC lag
await sendBase([await pb.methods.delegateMatch([...matchId]).accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).remainingAccounts([{ pubkey: EP.validator, isSigner: false, isWritable: false }]).instruction()]);
await sendEr([await pe.methods.openLobby(matchPda).accountsPartial({ authority: wallet.publicKey, arena, world }).remainingAccounts(inputMetas).instruction()], [wallet], "open_lobby", 40);

// --- lobby
const join_ = async (who: Keypair, slot: number) => sendEr([await pe.methods.joinLobby(slot, 1, who.publicKey).accountsPartial({ player: who.publicKey, arena, input: inputs[slot], sessionToken: null }).instruction()], [who], "join", 3);
check("join_lobby slot 0 (player 1)", !(await join_(p1, 0)).err);
await expectCode("double join (same signer, another slot)", join_(p1, 1), "AlreadyJoined");
await expectCode("join a taken slot", join_(p2, 0), "SlotTaken");
await expectCode("join with another slot's InputBuf", (async () => sendEr([await pe.methods.joinLobby(2, 1, p2.publicKey).accountsPartial({ player: p2.publicKey, arena, input: inputs[3], sessionToken: null }).instruction()], [p2], "join", 3))(), "InvalidSlot");
// --- session keys (SessionTokenV2, session-keys 3.1.1; program cloned from devnet into the local stack)
const SESSION_PROGRAM = new PublicKey("KeyspM2ssCJbqUhQ4k7sveSiY4WjnYsrXkC8oDbwde5");
const SYS = new PublicKey("11111111111111111111111111111111");
const tokenPda = (signer: PublicKey, authority: PublicKey) => PublicKey.findProgramAddressSync([Buffer.from("session_token_v2"), PROGRAM_ID.toBuffer(), signer.toBuffer(), authority.toBuffer()], SESSION_PROGRAM)[0];
const disc = (n: string) => createHash("sha256").update(`global:${n}`).digest().subarray(0, 8);
const createSession = async (owner: Keypair, signer: Keypair, validUntil: number) => {
  const data = Buffer.concat([disc("create_session_v2"), Buffer.from([1, 0]), Buffer.from([1]), (() => { const b = Buffer.alloc(8); b.writeBigInt64LE(BigInt(validUntil)); return b; })(), Buffer.from([0])]);
  const keys = [
    { pubkey: tokenPda(signer.publicKey, owner.publicKey), isSigner: false, isWritable: true },
    { pubkey: signer.publicKey, isSigner: true, isWritable: true },
    { pubkey: owner.publicKey, isSigner: true, isWritable: true },
    { pubkey: owner.publicKey, isSigner: true, isWritable: false },
    { pubkey: PROGRAM_ID, isSigner: false, isWritable: false },
    { pubkey: SYS, isSigner: false, isWritable: false },
  ];
  // the authority is both fee payer and authority: dedupe the metas
  const ix = new TransactionInstruction({ programId: SESSION_PROGRAM, keys, data });
  return sendBase([ix], [owner, signer], "create_session");
};
const revokeSession = async (owner: Keypair, signer: Keypair) => sendBase([new TransactionInstruction({ programId: SESSION_PROGRAM, data: disc("revoke_session_v2"), keys: [
  { pubkey: tokenPda(signer.publicKey, owner.publicKey), isSigner: false, isWritable: true },
  { pubkey: owner.publicKey, isSigner: false, isWritable: true },
  { pubkey: owner.publicKey, isSigner: true, isWritable: false },
  { pubkey: SYS, isSigner: false, isWritable: false },
] })], [owner], "revoke_session");
const joinS = async (signer: Keypair, wallet_: PublicKey, slot: number, token: PublicKey | null) => sendEr([await pe.methods.joinLobby(slot, 1, wallet_).accountsPartial({ player: signer.publicKey, arena, input: inputs[slot], sessionToken: token }).instruction()], [signer], "joinS", 3);
const now = Math.floor(Date.now() / 1000);
const p3 = Keypair.generate(), s3 = Keypair.generate(), s3b = Keypair.generate(), p4 = Keypair.generate(), s4 = Keypair.generate();
for (const p of [p3, p4]) await sendBase([SystemProgram.transfer({ fromPubkey: wallet.publicKey, toPubkey: p.publicKey, lamports: LAMPORTS_PER_SOL / 10 })]);
await createSession(p3, s3b, now - 60); // already expired
await createSession(p3, s3, now + 25); // expires during the test
await createSession(p4, s4, now + 3600);
await sleep(2500);
await expectCode("join with an expired session token", joinS(s3b, p3.publicKey, 6, tokenPda(s3b.publicKey, p3.publicKey)), "SessionInvalid");
await expectCode("join: session signer without a token", joinS(s4, p4.publicKey, 6, null), "Unauthorized");
await expectCode("join: token of another wallet", joinS(s4, p3.publicKey, 6, tokenPda(s4.publicKey, p4.publicKey)), "SessionInvalid");
check("join with a valid session token (slot 6)", !(await joinS(s3, p3.publicKey, 6, tokenPda(s3.publicKey, p3.publicKey))).err);
check("join with a valid session token (slot 7)", !(await joinS(s4, p4.publicKey, 7, tokenPda(s4.publicKey, p4.publicKey))).err);
// --- commits / tick before the match runs
await expectCode("commit_round before any round", (async () => sendEr([await pe.methods.commitRound(1).accountsPartial({ payer: wallet.publicKey, matchAccount: matchPda }).instruction()], [wallet], "commit", 3))(), "NothingToCommit");
const tickMetas = tickAccounts(arena, matchPda, maps);
const tickTx = async (metas: any[]) => sendEr([cu(1_400_000), await pe.methods.tick().remainingAccounts(metas).instruction()], [wallet], "tick", 3);
let w0 = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
check("tick while the arena is not running: no-op success", !(await tickTx(tickMetas)).err && decodeWorld((await er.getAccountInfo(world))!.data as Buffer).tick === w0.tick);
// --- VRF + start
await expectCode("start_sim before the deal (Match not Dealt)", (async () => sendEr([cu(400_000), await pe.methods.startSim().accountsPartial({ authority: wallet.publicKey, arena, world, matchAccount: matchPda, lobbyMap: maps[4] }).instruction()], [wallet], "start", 3))(), "InvalidStatus");
const fake = Keypair.generate();
const vrfFake = await sendEr([await pe.methods.dealCallback([...randomBytes(32)]).accountsPartial({ vrfProgramIdentity: fake.publicKey, matchAccount: matchPda }).instruction()], [wallet, fake], "cb", 2);
check("deal_callback from a non-VRF signer rejected", !!vrfFake.err, vrfFake.err?.slice(0, 80) ?? "succeeded");
await sendEr([await pe.methods.requestDeal().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda, oracleQueue: EP.vrfQueue }).instruction()]);
for (let i = 0; i < 100; i++) { const m: any = await pe.account.match.fetch(matchPda); if (Object.keys(m.status)[0] === "dealt") break; await sleep(300); }
check("start_sim after the VRF deal", !(await sendEr([cu(400_000), await pe.methods.startSim().accountsPartial({ authority: wallet.publicKey, arena, world, matchAccount: matchPda, lobbyMap: maps[4] }).instruction()])).err);
await expectCode("join after the roster is locked (claiming a bot slot)", join_(p2, 5), "BadArenaState");
// NotDue (real-time gate): right after the start, 3 tick() in one transaction (one slot) run the first tick only;
// the 2 others are NotDue no-ops (tick n may start once n/30 s of slot clock have passed)
{
  const tm = tickAccounts(arena, matchPda, maps);
  const wd0 = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
  const tIx = await pe.methods.tick().remainingAccounts(tm).instruction();
  const r = await sendEr([cu(1_400_000), tIx, tIx, tIx], [wallet], "3 ticks", 3);
  const wd1 = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
  check("early tick() is a NotDue no-op (3 tick() in the first slot: one tick, the rest NotDue / its slices)", !r.err && wd1.tick === wd0.tick + 1 && wd1.notDue >= wd0.notDue + 1n, `tick ${wd0.tick}→${wd1.tick}, not_due ${wd0.notDue}→${wd1.notDue} ${r.err ?? ""}`);
}
// --- inputs
w0 = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
const sub = async (who: Keypair, slot: number, start: number, batch: number[], token: PublicKey | null = null) => sendEr([await pe.methods.submitInput(slot, start, batch).accountsPartial({ owner: who.publicKey, input: inputs[slot], world, sessionToken: token }).instruction()], [who], "input", 3);
check("submit_input by the slot owner", !(await sub(p1, 0, w0.tick + 3, [10, 20, 1, 0, 10, 20, 1, 0, 10, 20, 1, 0])).err);
await expectCode("submit_input signed by another key", sub(p2, 0, w0.tick + 3, Array(12).fill(0)), "Unauthorized");
await expectCode("input tick out of window (+50)", sub(p1, 0, w0.tick + 50, Array(12).fill(0)), "InputOutOfWindow");
// advance the World past the window (fallback ticks; 1 tick = `parts` executions)
for (let i = 0; i < 4 * 14; i++) await sendEr([cu(1_400_000), await pe.methods.tick().remainingAccounts(tickAccounts(arena, matchPda, maps)).instruction()], [wallet], "tick", 2);
const wt = decodeWorld((await er.getAccountInfo(world))!.data as Buffer).tick;
await expectCode(`input tick out of window (−10 at tick ${wt})`, sub(p1, 0, wt - 10, Array(12).fill(0)), "InputOutOfWindow");
// P4 0.4: the forward window (default +8; set_input_lead 8..13 by the authority only)
const lead = async (who: Keypair, l: number) => sendEr([await pe.methods.setInputLead(l).accountsPartial({ authority: who.publicKey, arena, world }).instruction()], [who], "lead", 3);
await expectCode("input at T+12 with the default window (+8)", sub(p1, 0, wt + 12, Array(12).fill(0)), "InputOutOfWindow");
await expectCode("set_input_lead by a non-authority", lead(p1, 12), "Unauthorized");
await expectCode("set_input_lead beyond the 16-entry ring (14)", lead(wallet, 14), "InputOutOfWindow");
check("set_input_lead(12) by the authority", !(await lead(wallet, 12)).err);
const wt2 = decodeWorld((await er.getAccountInfo(world))!.data as Buffer).tick;
check("input at T+12 with the +12 window", !(await sub(p1, 0, wt2 + 12, Array(12).fill(0))).err);
await expectCode("input at T+13 with the +12 window", sub(p1, 0, wt2 + 13, Array(12).fill(0)), "InputOutOfWindow");
await lead(wallet, 0);
const hs = await sub(p1, 0, wt + 2, [0x80, 0x80, 0xff, 0x7f, 0x80, 0x80, 0xff, 0x7f, 0x80, 0x80, 0xff, 0x7f]);
const ib: any = await pe.account.inputBuf.fetch(inputs[0]);
const e = ib.ring[(wt + 2) % 16];
if (hs.err) console.log("hostile submit failed:", hs.err);
const mx = (e.input[0] << 24) >> 24, my = (e.input[1] << 24) >> 24;
check("sanitiser clamps hostile values (stick −128,−128, buttons 0xFF)", mx * mx + my * my <= 127 * 127 + 254 && e.input[2] !== 0xff, `stored ${JSON.stringify(e.input)}`);
// session-key inputs: valid, without the token, after revocation, after expiry
w0 = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
check("submit_input by a session signer with its token", !(await sub(s4, 7, w0.tick + 3, Array(12).fill(0), tokenPda(s4.publicKey, p4.publicKey))).err);
await expectCode("submit_input by a session signer without its token", sub(s4, 7, w0.tick + 3, Array(12).fill(0), null), "SessionInvalid");
await revokeSession(p4, s4);
let revoked: any = { err: undefined };
for (let i = 0; i < 20; i++) { await sleep(1000); const wt2 = decodeWorld((await er.getAccountInfo(world))!.data as Buffer).tick; revoked = await sub(s4, 7, wt2 + 3, Array(12).fill(0), tokenPda(s4.publicKey, p4.publicKey)); if (revoked.err) break; }
check("submit_input after the session token is revoked (rollup sees the closed token)", !!revoked.err, revoked.err?.slice(0, 100) ?? "still accepted after 20 s");
const waitS = Math.max(0, now + 27 - Math.floor(Date.now() / 1000));
await sleep(waitS * 1000);
const wt3 = decodeWorld((await er.getAccountInfo(world))!.data as Buffer).tick;
await expectCode("submit_input after the session token expired", sub(s3, 6, wt3 + 3, Array(12).fill(0), tokenPda(s3.publicKey, p3.publicKey)), "SessionInvalid");
// --- hot tick robustness (from a tick boundary: a continuation slice does not read the InputBufs)
for (let i = 0; i < 12; i++) { const w = decodeWorld((await er.getAccountInfo(world))!.data as Buffer); if (w.partPending === 0) break; await sendEr([cu(1_400_000), await pe.methods.tick().remainingAccounts(tickAccounts(arena, matchPda, maps)).instruction()], [wallet], "finish", 2); }
const wBefore = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
const noop = async (name: string, metas: any[]) => { const r = await tickTx(metas); const w = decodeWorld((await er.getAccountInfo(world))!.data as Buffer); check(name, !r.err && w.tick === wBefore.tick && w.partPending === wBefore.partPending, r.err ?? `code ${w.lastCode}`); };
await noop("tick with no accounts: no-op success", []);
await noop("tick with 5 accounts: no-op success", tickMetas.slice(0, 5));
await noop("tick with reordered maps: no-op success", [...tickMetas.slice(0, 4), tickMetas[5], tickMetas[4], ...tickMetas.slice(6)]);
await noop("tick with a wrong (read-only) match account: no-op success", [...tickMetas.slice(0, 3), { pubkey: Keypair.generate().publicKey, isSigner: false, isWritable: false }, ...tickMetas.slice(4)]);
await noop("tick with 8 foreign accounts inserted: hostile length / order", [...tickMetas.slice(0, 9), ...Array.from({ length: 8 }, () => ({ pubkey: Keypair.generate().publicKey, isSigner: false, isWritable: false })), ...tickMetas.slice(9)]);
// --- task ids
const sched = async () => sendEr([cu(400_000), await pe.methods.scheduleTicks(new BN(1000), new BN(2)).accountsPartial({ payer: wallet.publicKey, arena }).remainingAccounts(tickMetas).instruction()]);
await sched(); const t1 = ((await pe.account.arena.fetch(arena)) as any).taskId.toString();
await sched(); const t2 = ((await pe.account.arena.fetch(arena)) as any).taskId.toString();
check("task ids are unique per schedule (arena nonce)", t1 !== t2, `${t1} vs ${t2}`);
for (const t of [t1, t2]) await sendEr([await pe.methods.cancelTicks(new BN(t)).accountsPartial({ payer: wallet.publicKey }).remainingAccounts([{ pubkey: MAGIC_CONTEXT, isSigner: false, isWritable: true }]).instruction()], [wallet], "cancel", 2);
// --- an arena cannot re-enter an old match / start twice
await expectCode("start_sim while running", (async () => sendEr([cu(400_000), await pe.methods.startSim().accountsPartial({ authority: wallet.publicKey, arena, world, matchAccount: matchPda, lobbyMap: maps[4] }).instruction()], [wallet], "start", 3))(), "BadArenaState");
await sendEr([await pe.methods.abortArena().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()]);
await sendEr([await pe.methods.openLobby(matchPda).accountsPartial({ authority: wallet.publicKey, arena, world }).remainingAccounts(inputMetas).instruction()]);
await expectCode("re-enter an old (already dealt and started) match", (async () => sendEr([cu(400_000), await pe.methods.startSim().accountsPartial({ authority: wallet.publicKey, arena, world, matchAccount: matchPda, lobbyMap: maps[4] }).instruction()], [wallet], "start", 3))(), "InvalidStatus");
await expectCode("finalize_match before Completed", (async () => sendEr([await pe.methods.finalizeMatch().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).instruction()], [wallet], "fin", 3))(), "InvalidStatus");
await expectCode("open_lobby by a non-authority", (async () => sendEr([await pe.methods.openLobby(matchPda).accountsPartial({ authority: p1.publicKey, arena, world }).remainingAccounts(inputMetas).instruction()], [p1], "lobby", 3))(), "Unauthorized");
await sendEr([await pe.methods.abortArena().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()]);
const fails = results.filter((r) => !r.pass).length;
writeFileSync(join(ROOT, `docs/proof/p2/security-${NET}.json`), JSON.stringify({ net: NET, at: new Date().toISOString(), passed: results.length - fails, failed: fails, results }, null, 1));
console.log(`security: ${results.length - fails}/${results.length} pass`);
process.exit(fails);
