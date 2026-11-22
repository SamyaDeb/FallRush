// CU per tick() part on the rollup: the last `n` transactions touching the World account, with the round they ran in.
// Used by run.ts every CU_EVERY_MS (devnet: the task's executions are ordinary rollup transactions).
import { er } from "./lib.ts";
import type { PublicKey } from "@solana/web3.js";
export async function sampleCu(world: PublicKey, n = 60): Promise<{ cu: number; fallback: boolean; ok: boolean }[]> {
  const sigs = await er.getSignaturesForAddress(world, { limit: n }).catch(() => []);
  const out: { cu: number; fallback: boolean; ok: boolean }[] = [];
  const txs = await er.getTransactions(sigs.map((s) => s.signature), { maxSupportedTransactionVersion: 0, commitment: "confirmed" } as any).catch(() => []);
  for (const t of txs as any[]) {
    if (!t?.meta) continue;
    const logs: string[] = t.meta.logMessages ?? [];
    if (!logs.some((l) => l.includes("Instruction: Tick"))) continue;
    const m = logs.map((l) => l.match(/4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4 consumed (\d+) of (\d+)/)).find((x) => x);
    if (!m) continue;
    const noop = logs.some((l) => l.includes("tick no-op"));
    if (noop) continue;
    out.push({ cu: Number(m[1]), fallback: Number(m[2]) > 400_000, ok: !t.meta.err });
  }
  return out;
}
