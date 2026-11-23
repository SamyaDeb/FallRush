// P4 0.4: set the arena's forward input window (LEAD=0 → the program default 8).
import { pe, pda, wallet, sendEr } from "../keeper/lib.ts";
const arena = pda.arena(Number(process.env.ARENA ?? 2));
const r = await sendEr([await pe.methods.setInputLead(Number(process.env.LEAD ?? 0)).accountsPartial({ authority: wallet.publicKey, arena, world: pda.world(arena) }).instruction()], [wallet], "set_input_lead");
console.log(JSON.stringify(r)); process.exit(r.err ? 1 : 0);
