import { PublicKey } from "@solana/web3.js";
import { base, pda } from "../keeper/lib.ts";
import { tee, p6pda } from "../keeper/p6.ts";
const c = await tee();
for (const id of [2, 3]) { const b = p6pda.book(pda.arena(id)); const p = p6pda.permission(b);
  const [x, y, z] = [await base.getAccountInfo(b), await c.getAccountInfo(b), await c.getAccountInfo(p)];
  console.log(id, "base", x?.lamports, x?.owner.toBase58(), "tee", y?.lamports, "perm", z?.lamports, z?.data.length, z ? Buffer.from(z.data).subarray(0, 40).toString("hex") : null); }
