// fullchain deploy step: point arenas ARENAS (default 2,3) at the current sealed Goo Tower blob (map slot 1, deal id 1).
// Only the Goo Tower slot changes (the live hybrid Final reads slots 3 + 4 only). Checks the new account first.
//   P2_NET=devnet ARENAS=2,3 node --experimental-strip-types tools/fullchain/set-goo.ts
import { PublicKey } from "@solana/web3.js";
import { base, pe, pda, wallet, sendEr, PROGRAM_ID } from "../keeper/lib.ts";

const GOO = new PublicKey(process.env.GOO ?? "47v2Vx8Fbfnuxxu8Y62tPL3U3wbFmf3yMnsPYPFjarbT");
const WANT = "5966a1cacd37d1503d6512bf487fe65823fd9787d4bee12fd5ee33c18a191946"; // maps/map_blob_hashes_v1.txt GooTower
const g = await base.getAccountInfo(GOO);
if (!g || !g.owner.equals(PROGRAM_ID) || g.data[44] !== 1 || g.data[45] !== 1 || Buffer.from(g.data.subarray(48, 80)).toString("hex") !== WANT) throw new Error("the Goo Tower account is not the sealed current blob");
for (const id of (process.env.ARENAS ?? "2,3").split(",").map(Number)) {
  const arena = pda.arena(id);
  const a: any = await pe.account.arena.fetch(arena);
  const maps: PublicKey[] = [...a.maps];
  const before = maps[1].toBase58();
  if (maps[1].equals(GOO)) { console.log(`arena ${id}: already ${GOO.toBase58()}`); continue; }
  maps[1] = GOO;
  const r = await sendEr([await pe.methods.setArena(maps, a.parts, a.slotMs).accountsPartial({ authority: wallet.publicKey, arena }).instruction()], [wallet], `set_arena ${id}`);
  const after: any = await pe.account.arena.fetch(arena);
  console.log(JSON.stringify({ arena: id, before, after: after.maps[1].toBase58(), sig: r.sig, err: r.err ?? null }));
}
process.exit(0);
