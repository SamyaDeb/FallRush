import { Connection, PublicKey } from "@solana/web3.js";
const er = new Connection("https://devnet-as.magicblock.app", "confirmed");
const PROGRAM = new PublicKey("4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4");
const arena = PublicKey.findProgramAddressSync([Buffer.from("arena"), Buffer.from([2])], PROGRAM)[0];
const inPda = PublicKey.findProgramAddressSync([Buffer.from("in"), arena.toBuffer(), Buffer.from([0])], PROGRAM)[0];
const T0 = Date.parse("2026-11-25T04:04:43Z") / 1000, T1 = Date.parse("2026-11-25T04:10:54Z") / 1000;
let before: string | undefined; let checked = 0;
for (let page = 0; page < 200; page++) {
  const ps = await er.getSignaturesForAddress(inPda, { before, limit: 1000 });
  if (!ps.length) break;
  for (let i = 0; i < ps.length; i++) {
    const x = ps[i]; if (!x.blockTime || x.err) continue;
    if (x.blockTime > T1) continue;
    if (x.blockTime < T0) { console.log("window passed, checked", checked); process.exit(1); }
    if (i % 3) continue;
    const r = await (await fetch("https://devnet-as.magicblock.app", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "getTransaction", params: [x.signature, { encoding: "json", maxSupportedTransactionVersion: 1 }] }) })).json();
    checked++;
    const logs: string[] = r.result?.meta?.logMessages ?? [];
    if (logs.some((l) => /SubmitInput/i.test(l))) { console.log(JSON.stringify({ sig: x.signature, signer: r.result.transaction.message.accountKeys[0], cu: r.result.meta.computeUnitsConsumed, t: new Date(x.blockTime * 1000).toISOString() })); process.exit(0); }
  }
  before = ps[ps.length - 1].signature;
}
console.log("none, checked", checked);
