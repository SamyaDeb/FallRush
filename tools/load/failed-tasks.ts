// List recent failed transactions touching World 0 on the rollup and show why they failed.
import { er, pda } from "./lib.ts";
const sigs = await er.getSignaturesForAddress(pda.world(0), { limit: 1000 }, "confirmed");
const failed = sigs.filter((s) => s.err);
const kinds: Record<string, number> = {};
for (const s of failed) { const k = JSON.stringify(s.err).slice(0, 80); kinds[k] = (kinds[k] ?? 0) + 1; }
console.log("recent", sigs.length, "failed", failed.length, kinds);
for (const s of failed.slice(0, 6)) {
  const t = await er.getTransaction(s.signature, { maxSupportedTransactionVersion: 0, commitment: "confirmed" }).catch(() => null);
  const payer = t?.transaction.message.staticAccountKeys?.[0]?.toBase58().slice(0, 6);
  const logs = (t?.meta?.logMessages ?? []).filter((l) => !l.includes("consumption"));
  console.log("--", s.signature.slice(0, 10), "payer", payer, "time", s.blockTime, JSON.stringify(s.err));
  console.log("   " + logs.slice(-5).join("\n   "));
}
process.exit(0);
