import { tee } from "../keeper/p6.ts";
const c = await tee();
const t = await c.getTransaction(process.argv[2], { maxSupportedTransactionVersion: 0, commitment: "confirmed" } as any).catch((e) => String(e));
console.log(JSON.stringify((t as any)?.meta?.logMessages ?? t, null, 1).slice(0, 4000));
