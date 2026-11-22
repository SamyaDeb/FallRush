// P2.8 security suite (local stack, arena SEC_ARENA=1). Each check prints PASS / FAIL; exit code = failures.
//   P2_NET=local ARENA=1 tsx tools/keeper/security.ts
import { Keypair, PublicKey, SystemProgram, LAMPORTS_PER_SOL, Transaction } from "@solana/web3.js";
import { randomBytes } from "node:crypto";
import { writeFileSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { NET, EP, base, er, wallet, pb, pe, pda, sendBase, sendEr, ROOT, MAGIC_CONTEXT, N, sleep, decodeWorld, tickAccounts, cu, BN } from "./lib.ts";

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
await sendBase([await pb.methods.delegateMatch([...matchId]).accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).remainingAccounts([{ pubkey: EP.validator, isSigner: false, isWritable: false }]).instruction()]);
await sendEr([await pe.methods.openLobby(matchPda).accountsPartial({ authority: wallet.publicKey, arena, world }).remainingAccounts(inputMetas).instruction()], [wallet], "open_lobby", 40);

// --- lobby
const join_ = async (who: Keypair, slot: number) => sendEr([await pe.methods.joinLobby(slot, 1).accountsPartial({ player: who.publicKey, arena, input: inputs[slot] }).instruction()], [who], "join", 3);
check("join_lobby slot 0 (player 1)", !(await join_(p1, 0)).err);
await expectCode("double join (same signer, another slot)", join_(p1, 1), "AlreadyJoined");
await expectCode("join a taken slot", join_(p2, 0), "SlotTaken");
await expectCode("join with another slot's InputBuf", (async () => sendEr([await pe.methods.joinLobby(2, 1).accountsPartial({ player: p2.publicKey, arena, input: inputs[3] }).instruction()], [p2], "join", 3))(), "InvalidSlot");
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
// --- inputs
w0 = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
const sub = async (who: Keypair, slot: number, start: number, batch: number[]) => sendEr([await pe.methods.submitInput(slot, start, batch).accountsPartial({ owner: who.publicKey, input: inputs[slot], world }).instruction()], [who], "input", 3);
check("submit_input by the slot owner", !(await sub(p1, 0, w0.tick + 3, [10, 20, 1, 0, 10, 20, 1, 0, 10, 20, 1, 0])).err);
await expectCode("submit_input signed by another key", sub(p2, 0, w0.tick + 3, Array(12).fill(0)), "Unauthorized");
await expectCode("input tick out of window (+50)", sub(p1, 0, w0.tick + 50, Array(12).fill(0)), "InputOutOfWindow");
// advance the World past the window (fallback ticks; 1 tick = `parts` executions)
for (let i = 0; i < 4 * 14; i++) await sendEr([cu(1_400_000), await pe.methods.tick().remainingAccounts(tickAccounts(arena, matchPda, maps)).instruction()], [wallet], "tick", 2);
const wt = decodeWorld((await er.getAccountInfo(world))!.data as Buffer).tick;
await expectCode(`input tick out of window (−10 at tick ${wt})`, sub(p1, 0, wt - 10, Array(12).fill(0)), "InputOutOfWindow");
await sub(p1, 0, w0.tick + 4, [0x80, 0x80, 0xff, 0x7f, 0x80, 0x80, 0xff, 0x7f, 0x80, 0x80, 0xff, 0x7f]);
const ib: any = await pe.account.inputBuf.fetch(inputs[0]);
const e = ib.ring[(w0.tick + 4) % 16];
const mx = (e.input[0] << 24) >> 24, my = (e.input[1] << 24) >> 24;
check("sanitiser clamps hostile values (stick −128,−128, buttons 0xFF)", mx * mx + my * my <= 127 * 127 + 254 && e.input[2] !== 0xff, `stored ${JSON.stringify(e.input)}`);
// --- hot tick robustness
const wBefore = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
const noop = async (name: string, metas: any[]) => { const r = await tickTx(metas); const w = decodeWorld((await er.getAccountInfo(world))!.data as Buffer); check(name, !r.err && w.tick === wBefore.tick && w.partPending === wBefore.partPending, r.err ?? `code ${w.lastCode}`); };
await noop("tick with no accounts: no-op success", []);
await noop("tick with 5 accounts: no-op success", tickMetas.slice(0, 5));
await noop("tick with reordered maps: no-op success", [...tickMetas.slice(0, 4), tickMetas[5], tickMetas[4], ...tickMetas.slice(6)]);
await noop("tick with a wrong (read-only) match account: no-op success", [...tickMetas.slice(0, 3), { pubkey: Keypair.generate().publicKey, isSigner: false, isWritable: false }, ...tickMetas.slice(4)]);
await noop("tick with 8 foreign accounts inserted: hostile length / order", [...tickMetas.slice(0, 9), ...Array.from({ length: 8 }, () => ({ pubkey: Keypair.generate().publicKey, isSigner: false, isWritable: false })), ...tickMetas.slice(9)]);
// NotDue: once a tick completes, a new tick (part 0) starts at most once per 3 slots — two calls back-to-back
for (let i = 0; i < 8; i++) { const w = decodeWorld((await er.getAccountInfo(world))!.data as Buffer); if (w.partPending === 0) break; await tickTx(tickMetas); }
const wd0 = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
const fire = async () => { const tx = new Transaction().add(cu(1_400_000), await pe.methods.tick().remainingAccounts(tickMetas).instruction()); tx.feePayer = wallet.publicKey; tx.recentBlockhash = (await er.getLatestBlockhash()).blockhash; tx.sign(wallet); return er.sendRawTransaction(tx.serialize(), { skipPreflight: true }); };
await Promise.all([fire(), fire().catch(() => ""), fire().catch(() => "")]);
await sleep(1500);
const wd1 = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
check("early tick() is a NotDue no-op (3 part-0 calls in one slot start one tick)", wd1.tick === wd0.tick + 1 && wd1.notDue > wd0.notDue, `tick ${wd0.tick}→${wd1.tick}, not_due ${wd0.notDue}→${wd1.notDue}`);
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
