// P6 carry-over 3 (D-CROWN): scripted "human" players for the on-chain Final, driven by rush-core's own bot brain.
// Each human is the game's chain bundle (packages/chain, the same code the WebGL client runs) with its own burner +
// session key: join_lobby in a Classic finalist's slot (the human path), then submit_input batches (session-key
// signed) whose commands come from `bots::bot_input` (tier REFERENCE) on a local copy of the on-chain World, looked
// ahead LEAD ticks with rush_core (the harness wasm: rush-core-wasm --features harness; the shipped rush_core.wasm is
// not changed). No gameplay change: the program runs the Final exactly as for any human.
//   cd tools/keeper && P2_NET=devnet npx tsx ../p6/crown-harness.ts <match hex> <slot,slot> <out.json>
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import { Keypair, PublicKey, SystemProgram, Transaction, Connection, sendAndConfirmTransaction } from "@solana/web3.js";
import { createChain } from "../../packages/chain/src/api.ts";
import { memoryKV } from "../../packages/chain/src/storage.ts";
import { b58encode } from "../../packages/chain/src/b58.ts";

const ROOT = new URL("../../", import.meta.url).pathname;
const [MATCH, SLOTS, OUT] = process.argv.slice(2);
const slots = SLOTS.split(",").map(Number);
const LEAD = Number(process.env.LEAD ?? 4);
const CFG = JSON.parse(readFileSync(join(ROOT, `packages/chain/config/devnet-${process.env.ARENA_KEY ?? "E9XE7vtQgrWnXLcGMn5FYXAFLb6GmBjrKcZq5wgPcEbG"}.json`), "utf8"));
const WASM = process.env.HARNESS_WASM ?? join(ROOT, "tools/p6/harness/rush_core_harness.wasm");
const base = new Connection("https://api.devnet.solana.com", "confirmed");
const admin = Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(join(homedir(), ".config/solana/id.json"), "utf8"))));
const R: any = { match: MATCH, slots, lead: LEAD, at: new Date().toISOString(), humans: [] };
const save = () => writeFileSync(OUT, JSON.stringify(R, null, 1));
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

// ---- the harness core (bot brain + look-ahead)
const { instance } = await WebAssembly.instantiate(readFileSync(WASM), {});
const ex: any = instance.exports;
const put = (b: Uint8Array) => { const p = ex.rc_alloc(b.length); new Uint8Array(ex.memory.buffer, p, b.length).set(b); return p; };
const map = readFileSync(join(ROOT, "maps/CrumbleShelf.mapblob"));
const cfg = new Uint8Array(24); new DataView(cfg.buffer).setUint32(0, 30, true);
const H = ex.rc_init(put(map), map.length, put(cfg));
const cap = ex.rc_world_size() + 4096, wbuf = ex.rc_alloc(cap), obuf = ex.rc_alloc(8), ibuf = ex.rc_alloc(72);
const setWorld = (w: Uint8Array) => { new Uint8Array(ex.memory.buffer, wbuf, w.length).set(w); return ex.rc_set_world(H, wbuf, w.length) === 0; };
const botCmd = (slot: number) => { ex.rc_bot_input(H, slot, obuf); return new Uint8Array(ex.memory.buffer, obuf, 5).slice(); };
const step = (cmds: Map<number, Uint8Array>) => {
  const inp = new Uint8Array(ex.memory.buffer, ibuf, 72); inp.fill(0);
  for (const [s, c] of cmds) { inp[s * 6] = 1; inp.set(c, s * 6 + 1); }
  ex.rc_step(H, ibuf);
};
const worldTick = () => { const n = ex.rc_world_bytes(H, wbuf, cap); return n >= 4 ? new DataView(ex.memory.buffer, wbuf).getUint32(0, true) : -1; };

// ---- the humans: one chain bundle each (own burner + session key), persistent keys across runs
const KEYF = join(ROOT, "tools/keeper/.keys/devnet-p6-crown-humans.json");
const saved: string[] = existsSync(KEYF) ? JSON.parse(readFileSync(KEYF, "utf8")) : [];
const humans = await Promise.all(slots.map(async (slot, i) => {
  const kv = memoryKV();
  const chain: any = createChain({ kv });
  const r = JSON.parse(await chain.init(JSON.stringify(CFG)));
  if (!r.ok) throw new Error("init " + r.error);
  if (saved[i]) JSON.parse(chain.importBurner(saved[i]));
  const w = JSON.parse(chain.wallet());
  saved[i] = chain.exportBurner();
  return { slot, chain, burner: new PublicKey(w.burner), sent: 0, errors: 0, lastFirst: -1 };
}));
writeFileSync(KEYF, JSON.stringify(saved), { mode: 0o600 });
for (const h of humans) {
  const b = await base.getBalance(h.burner).catch(() => 0);
  if (b < 15_000_000) await sendAndConfirmTransaction(base, new Transaction().add(SystemProgram.transfer({ fromPubkey: admin.publicKey, toPubkey: h.burner, lamports: 30_000_000 - b })), [admin]);
  const s = JSON.parse(await h.chain.session("create", "{}"));
  R.humans.push({ slot: h.slot, burner: h.burner.toBase58(), session: s.ok ? s.token ?? "ok" : s.error });
}
save();
console.log("humans", R.humans);
// join the hybrid match's lobby in the finalist slots (returns once the Final started)
const joins = await Promise.all(humans.map(async (h) => {
  const t0 = Date.now();
  const j = JSON.parse(await h.chain.join(JSON.stringify({ match: MATCH, slot: h.slot })));
  h.chain.subscribe("$world");
  return { slot: h.slot, ok: j.ok, error: j.error, joinedSlot: j.slot, ms: Date.now() - t0 };
}));
R.joins = joins; save();
console.log("joined", joins);
// play: every 3 ticks, plan the next 3 commands of each human from the bot brain on the look-ahead World
const b64 = (s: string) => Uint8Array.from(Buffer.from(s, "base64"));
let lastTick = -1, done = false, frames = 0, lastAdv = Date.now();
const t0 = Date.now();
while (!done && Date.now() - t0 < 6 * 60_000) {
  const v = humans[0].chain.latestWorldBase64();
  if (!v) { await sleep(20); continue; }
  const view = b64(v);
  const dv = new DataView(view.buffer, view.byteOffset);
  const tick = dv.getUint32(0, true), len = dv.getUint16(14, true);
  if (tick < lastTick + 3) { if (lastTick > 0 && Date.now() - lastAdv > 12_000) done = true; await sleep(10); continue; }
  lastAdv = Date.now();
  lastTick = tick;
  if (!setWorld(view.subarray(64, 64 + len))) { await sleep(10); continue; }
  frames++;
  // look ahead LEAD ticks (the batch has to arrive before its ticks run), then plan 3 ticks
  for (let k = 0; k < LEAD; k++) step(new Map(humans.map((h) => [h.slot, botCmd(h.slot)])));
  const first = worldTick() + 1;
  const plans = new Map<number, Uint8Array[]>(humans.map((h) => [h.slot, []]));
  for (let k = 0; k < 3; k++) {
    const cmds = new Map(humans.map((h) => [h.slot, botCmd(h.slot)]));
    for (const [s, c] of cmds) plans.get(s)!.push(c);
    step(cmds);
  }
  for (const h of humans) {
    if (first <= h.lastFirst) continue;
    const batch = new Uint8Array(6 + 15);
    batch[0] = h.slot; new DataView(batch.buffer).setUint32(1, first, true); batch[5] = 3;
    plans.get(h.slot)!.forEach((c, i) => batch.set(c, 6 + 5 * i));
    try { h.chain.sendInputs(batch); h.sent++; h.lastFirst = first; } catch { h.errors++; }
  }
  // director phase ≥ DONE (11): the WorldAcc header's phase is not in the view; stop when the arena leaves RUNNING
  if (frames % 30 === 0) {
    const st = JSON.parse(humans[0].chain.stats());
    R.stats = st; save();
    const ev = JSON.parse(humans[0].chain.drainEvents());
    if (ev.some((e: any) => /ended|done|final_over/i.test(JSON.stringify(e)))) done = true;
  }
}
R.frames = frames; R.sent = humans.map((h) => ({ slot: h.slot, sent: h.sent, errors: h.errors }));
save();
console.log("played", R.sent, "frames", frames);
for (const h of humans) h.chain.stop();
process.exit(0);
