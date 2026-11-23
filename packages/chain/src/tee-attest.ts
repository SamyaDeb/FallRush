// P6: fallrush-tee.js (loaded on demand by the chain bundle, `window.FallRushTee.verify(url)`): verifies in the
// browser that the sealed book's RPC (MagicBlock's Private Ephemeral Rollup) runs inside an Intel TDX enclave — the
// same check as ER SDK 0.18 `verifyTeeRpcIntegrity` (a fresh 64-byte challenge → the TEE's quote → Phala's DCAP
// verifier with Intel collateral from Phala's PCCS → the quote's report data must equal the challenge).
// Built from @phala/dcap-qvl 0.3.9 (pinned in tools/p6/package.json) by scripts/build.mjs.
// @ts-ignore — plain CommonJS, no types
import { getCollateral, verify, Quote } from '@phala/dcap-qvl';

const PCCS = 'https://pccs.phala.network/tdx/certification/v4';
async function verifyTee(rpcUrl: string): Promise<{ tcb: string }> {
  const ch = crypto.getRandomValues(new Uint8Array(64));
  const b64 = btoa(String.fromCharCode(...ch));
  const r = await fetch(`${rpcUrl}/quote?challenge=${encodeURIComponent(b64)}`);
  const j = await r.json() as { quote?: string; error?: string };
  if (r.status !== 200 || !j.quote) throw new Error(j.error ?? 'no quote');
  const raw = Uint8Array.from(atob(j.quote), (c) => c.charCodeAt(0));
  const coll = await getCollateral(PCCS, raw);
  const v = verify(raw, coll, Math.floor(Date.now() / 1000));
  const q = Quote.parse(raw);
  const td10 = q.report.asTd10();
  const td15 = td10 ? null : q.report.asTd15();
  const rd: Uint8Array | null = td10 ? td10.reportData : td15 ? td15.base.reportData : null;
  if (!rd || rd.length < 64) throw new Error('unsupported quote report');
  for (let i = 0; i < 64; i++) if (rd[i] !== ch[i]) throw new Error('quote report data does not match the challenge');
  return { tcb: String(v?.status ?? 'ok') };
}
(globalThis as any).FallRushTee = { verify: verifyTee };
