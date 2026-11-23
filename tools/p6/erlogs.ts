import { er } from "../keeper/lib.ts";
const t = await er.getTransaction(process.argv[2], { maxSupportedTransactionVersion: 0, commitment: "confirmed" } as any).catch((e) => String(e));
console.log(JSON.stringify((t as any)?.meta?.logMessages ?? t, null, 1).slice(0, 5000));
