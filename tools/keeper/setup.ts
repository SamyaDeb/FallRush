// P2 setup (idempotent): config (local only), arena `ARENA` (default 0) with PARTS parts, 12 InputBufs, the 5 map
// blobs (upload in 900 B chunks, sealed against maps/map_blob_hashes_v1.txt), arena maps, delegation to the ER.
//   P2_NET=local|devnet ARENA=0 PARTS=4 node --experimental-strip-types tools/keeper/setup.ts
import { PublicKey, SystemProgram, Keypair } from "@solana/web3.js";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { NET, EP, base, er, wallet, pb, pe, pda, key, sendBase, sendEr, ROOT, MAP_STEMS, PROGRAM_ID, sha256, N, sleep } from "./lib.ts";

const ARENA = Number(process.env.ARENA ?? 0);
const PARTS = Number(process.env.PARTS ?? 4);
const DELEGATION = new PublicKey("DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh");
const MAP_HDR = 80, CHUNK = 900;
const out: any = { net: NET, arena: ARENA, parts: PARTS, sigs: {} as Record<string, string> };

const hashes = Object.fromEntries(readFileSync(join(ROOT, "maps/map_blob_hashes_v1.txt"), "utf8").split("\n").filter((l) => l.trim() && !l.startsWith("#")).map((l) => { const [h, f] = l.trim().split(/\s+/); return [f.replace(/^.*\//, "").replace(".mapblob", ""), h]; }));

async function exists(conn: any, k: PublicKey) { return (await conn.getAccountInfo(k)) !== null; }

// 1. config (local stack only; devnet has it from M0)
const cfg = pda.config();
if (!(await exists(base, cfg))) {
  if (NET !== "local") throw new Error("config missing on devnet");
  const shared = await import(join(ROOT, "packages/shared/src/index.ts"));
  const programData = PublicKey.findProgramAddressSync([PROGRAM_ID.toBuffer()], new PublicKey("BPFLoaderUpgradeab1e11111111111111111111111"))[0];
  const ix = await pb.methods.initializeConfig(wallet.publicKey, 1, [...shared.rulesetV1Hash()], 1, [...shared.mapPoolV1Hash()]).accountsPartial({ admin: wallet.publicKey, programData }).instruction();
  out.sigs.config = await sendBase([ix], [wallet], "config");
}
// 2. arena + inputs
const arena = pda.arena(ARENA);
if (!(await exists(base, arena)) && !(await exists(er, arena))) {
  out.sigs.arena = await sendBase([await pb.methods.initArena(ARENA, PARTS).accountsPartial({ authority: wallet.publicKey, arena, world: pda.world(arena), eventLog: pda.evlog(arena) }).instruction()], [wallet], "init_arena");
}
for (let s = 0; s < N; s++) {
  if (!(await exists(base, pda.input(arena, s)))) {
    out.sigs[`input${s}`] = await sendBase([await pb.methods.initInput(ARENA, s).accountsPartial({ authority: wallet.publicKey, arena, input: pda.input(arena, s) }).instruction()], [wallet], `init_input ${s}`);
  }
}
// 3. maps
const maps: PublicKey[] = [];
for (const stem of MAP_STEMS) {
  const blob = readFileSync(join(ROOT, `maps/${stem}.mapblob`));
  const want = hashes[stem];
  if (sha256(blob).toString("hex") !== want) throw new Error(`${stem}: blob hash differs from map_blob_hashes_v1.txt`);
  const kp = key(`map-${stem}`);
  maps.push(kp.publicKey);
  let info = await base.getAccountInfo(kp.publicKey);
  if (info && info.data[44] === 1) { console.log(`map ${stem}: sealed`); continue; }
  if (!info) {
    const space = MAP_HDR + blob.length;
    const lamports = await base.getMinimumBalanceForRentExemption(space);
    out.sigs[`map_${stem}_create`] = await sendBase([SystemProgram.createAccount({ fromPubkey: wallet.publicKey, newAccountPubkey: kp.publicKey, lamports, space, programId: PROGRAM_ID })], [wallet, kp], `create ${stem}`);
    for (let i = 0; i < 30 && !(info = await base.getAccountInfo(kp.publicKey)); i++) await sleep(1000);
  }
  const mapId = stem === "Lobby" ? 254 : MAP_STEMS.indexOf(stem);
  for (let o = 0; o < blob.length; o += CHUNK) {
    const have = info!.data.subarray(MAP_HDR + o, MAP_HDR + Math.min(blob.length, o + CHUNK));
    if (info!.data[0] !== 0 && have.equals(blob.subarray(o, o + CHUNK))) continue; // resumable upload
    const ix = await pb.methods.uploadMapChunk(mapId, o, blob.length, Buffer.from(blob.subarray(o, o + CHUNK))).accountsPartial({ authority: wallet.publicKey, map: kp.publicKey }).instruction();
    await sendBase([ix], [wallet], `${stem} chunk ${o}`);
    if (o % (CHUNK * 10) === 0) console.log(`map ${stem}: ${o}/${blob.length}`);
  }
  out.sigs[`map_${stem}_seal`] = await sendBase([await pb.methods.sealMap([...Buffer.from(want, "hex")]).accountsPartial({ authority: wallet.publicKey, map: kp.publicKey }).instruction()], [wallet], `seal ${stem}`);
  console.log(`map ${stem}: sealed (${blob.length} B)`);
}
// 4. arena settings (base, before delegation)
const arenaInfo = await base.getAccountInfo(arena);
if (arenaInfo && arenaInfo.owner.equals(PROGRAM_ID)) {
  out.sigs.setArena = await sendBase([await pb.methods.setArena(maps, PARTS, NET === "local" ? 50 : 10).accountsPartial({ authority: wallet.publicKey, arena }).instruction()], [wallet], "set_arena");
}
// 5. delegation: arena, world, event log, inputs
const val = [{ pubkey: EP.validator, isSigner: false, isWritable: false }];
const toDelegate: [number, number, PublicKey][] = [[0, 0, arena], [1, 0, pda.world(arena)], [2, 0, pda.evlog(arena)], ...Array.from({ length: N }, (_, s) => [3, s, pda.input(arena, s)] as [number, number, PublicKey])];
for (const [kind, slot, k] of toDelegate) {
  const info = await base.getAccountInfo(k);
  if (info && info.owner.equals(DELEGATION)) continue;
  const ix = await pb.methods.delegateSim(kind, ARENA, slot).accountsPartial({ authority: wallet.publicKey, pda: k }).remainingAccounts(val).instruction();
  out.sigs[`delegate_${kind}_${slot}`] = await sendBase([ix], [wallet], `delegate ${kind} ${slot}`);
}
// wait for the rollup to see the arena
for (let i = 0; i < 60 && !(await exists(er, arena)); i++) await sleep(1000);
console.log(JSON.stringify({ ...out, arenaKey: arena.toBase58(), maps: maps.map((m) => m.toBase58()) }, null, 1));
process.exit(0);
