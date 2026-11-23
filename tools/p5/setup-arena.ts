// P5 task 2.1 (idempotent): a pool arena ARENA (default 3) set up like P2's tools/keeper/setup.ts, but REUSING the
// sealed map accounts of FROM (default arena 2: same 5 map blobs, hashes sealed on-chain) instead of uploading new
// ones; then the economy (5 markets + drop queue, delegated) via the keeper's setupEcon. Delegated to devnet-as.
//   P2_NET=devnet ARENA=3 FROM=2 node --experimental-strip-types tools/p5/setup-arena.ts
import { PublicKey } from "@solana/web3.js";
import { EP, base, er, wallet, pb, pe, pda, sendBase, PROGRAM_ID, N, sleep } from "../keeper/lib.ts";
import * as E from "../keeper/econ.ts";
const ARENA = Number(process.env.ARENA ?? 3), FROM = Number(process.env.FROM ?? 2);
const DELEGATION = new PublicKey("DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh");
const out: any = { arena: ARENA, sigs: {} };
const src: any = await pe.account.arena.fetch(pda.arena(FROM));
const maps: PublicKey[] = src.maps;
const arena = pda.arena(ARENA);
const exists = async (c: any, k: PublicKey) => (await c.getAccountInfo(k)) !== null;
if (!(await exists(base, arena)) && !(await exists(er, arena))) {
  out.sigs.arena = await sendBase([await pb.methods.initArena(ARENA, src.parts).accountsPartial({ authority: wallet.publicKey, arena, world: pda.world(arena), eventLog: pda.evlog(arena) }).instruction()], [wallet], "init_arena");
  for (let i = 0; i < 60 && !(await exists(base, arena)); i++) await sleep(1000);
}
for (let s = 0; s < N; s++) if (!(await exists(base, pda.input(arena, s)))) out.sigs[`input${s}`] = await sendBase([await pb.methods.initInput(ARENA, s).accountsPartial({ authority: wallet.publicKey, arena, input: pda.input(arena, s) }).instruction()], [wallet], `init_input ${s}`);
const ai = await base.getAccountInfo(arena);
if (ai && ai.owner.equals(PROGRAM_ID)) out.sigs.setArena = await sendBase([await pb.methods.setArena(maps, src.parts, 10).accountsPartial({ authority: wallet.publicKey, arena }).instruction()], [wallet], "set_arena");
const val = [{ pubkey: EP.validator, isSigner: false, isWritable: false }];
const todo: [number, number, PublicKey][] = [[0, 0, arena], [1, 0, pda.world(arena)], [2, 0, pda.evlog(arena)], ...Array.from({ length: N }, (_, s) => [3, s, pda.input(arena, s)] as [number, number, PublicKey])];
for (const [kind, slot, k] of todo) {
  const i = await base.getAccountInfo(k);
  if (i && i.owner.equals(DELEGATION)) continue;
  out.sigs[`delegate_${kind}_${slot}`] = await sendBase([await pb.methods.delegateSim(kind, ARENA, slot).accountsPartial({ authority: wallet.publicKey, pda: k }).remainingAccounts(val).instruction()], [wallet], `delegate ${kind} ${slot}`);
}
for (let i = 0; i < 60 && !(await exists(er, arena)); i++) await sleep(1000);
out.econ = await E.setupEcon(ARENA);
console.log(JSON.stringify({ ...out, arenaKey: arena.toBase58(), world: pda.world(arena).toBase58(), maps: maps.map((m) => m.toBase58()) }, null, 1));
process.exit(0);
