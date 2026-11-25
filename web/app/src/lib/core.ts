// rush_core.wasm (the same core the program runs as sBPF and Unity runs for prediction): decode a World into
// players / lids / sweepers for the 2D views, and step it for the replay check.
type Ex = Record<string, any> & { memory: WebAssembly.Memory };
let ex: Ex | null = null;
const maps: Record<string, Uint8Array> = {};
export async function core(): Promise<Ex> {
  if (ex) return ex;
  const { instance } = await WebAssembly.instantiate(await (await fetch('/chain/rush_core.wasm')).arrayBuffer(), {});
  ex = instance.exports as Ex;
  return ex;
}
/** The program's map slots (sim.rs: 0..3 = deal ids 0 Capsule Run, 1 Goo Tower, 2 Star Fountain, 3 Lid Flip; 4 = lobby). */
export const CHAIN_MAPS = ['BlockPartyDash', 'GooTower', 'StarScramble', 'CrumbleShelf', 'Lobby'];
/** Round names by deal id (the World's map_key). */
export const ROUND_MAP = ['Capsule Run', 'Goo Tower', 'Star Fountain', 'Lid Flip'];
async function map(name: string) { return (maps[name] ??= new Uint8Array(await (await fetch(`/maps/${name}.mapblob`)).arrayBuffer())); }
const put = (e: Ex, b: Uint8Array) => { const p = e.rc_alloc(b.length); new Uint8Array(e.memory.buffer, p, b.length).set(b); return p; };

export class CoreWorld {
  h = -1; e!: Ex; private buf = 0; private cap = 0;
  static async create(mapName = 'CrumbleShelf') {
    const w = new CoreWorld();
    w.e = await core();
    const m = await map(mapName);
    const cfg = new Uint8Array(24); new DataView(cfg.buffer).setUint32(0, 30, true);
    const mp = put(w.e, m), cp = put(w.e, cfg);
    w.h = w.e.rc_init(mp, m.length, cp);
    w.e.rc_dealloc(mp, m.length); w.e.rc_dealloc(cp, 24);
    w.cap = w.e.rc_world_size() + 4096; w.buf = w.e.rc_alloc(w.cap);
    return w;
  }
  /** fullchain: a handle whose map follows the World through every round, as the program's tick() picks it (needs
   *  the fullchain rush_core.wasm; `blobs` = the 5 slot blobs, default /maps/<name>.mapblob). Null on an old wasm. */
  static async createChain(blobs?: Uint8Array[]): Promise<CoreWorld | null> {
    const e = await core();
    if (!e.rc_step_chain) return null;
    const bs = blobs ?? (await Promise.all(CHAIN_MAPS.map((n) => map(n))));
    const w = await CoreWorld.create('Lobby');
    for (let s = 0; s < 5; s++) { const p = put(e, bs[s]); const r = e.rc_chain_map(w.h, s, p, bs[s].length); e.rc_dealloc(p, bs[s].length); if (r !== 0) return null; }
    return w;
  }
  /** fullchain: the map load the program runs before a tick (deal id loaded, −1 none needed, −2 missing map). */
  chainLoad(): number { return this.e.rc_chain_load(this.h); }
  /** fullchain: one whole tick exactly as the chain steps it (step_slice parts until complete); events or < 0. */
  stepChain(inputs: Uint8Array, budget = 6000): number { const p = put(this.e, inputs); const n = this.e.rc_step_chain(this.h, p, budget); this.e.rc_dealloc(p, inputs.length); return n; }
  /** fullchain: one program execution from part `from` (budget 0 = one player); the next part or < 0. */
  stepPart(inputs: Uint8Array, from: number, budget = 0): number { const p = put(this.e, inputs); const n = this.e.rc_step_part(this.h, p, from, budget); this.e.rc_dealloc(p, inputs.length); return n; }
  /** The director's position of the current World: round 0..4, director phase, the loaded round, its map (deal id). */
  round() { const r = this.e.rc_round ? this.e.rc_round(this.h) >>> 0 : 0; return { round: r & 0xff, phase: (r >> 8) & 0xff, roundIndex: (r >> 16) & 0xff, mapKey: (r >>> 24) & 0xff }; }
  set(world: Uint8Array): boolean { new Uint8Array(this.e.memory.buffer, this.buf, world.length).set(world); return this.e.rc_set_world(this.h, this.buf, world.length) === 0; }
  bytes(): Uint8Array { const n = this.e.rc_world_bytes(this.h, this.buf, this.cap); return new Uint8Array(this.e.memory.buffer, this.buf, n).slice(); }
  /** 12 × 6 B inputs {present, mx, my, buttons, yaw lo, yaw hi}. */
  step(inputs: Uint8Array) { const p = put(this.e, inputs); const n = this.e.rc_step(this.h, p); this.e.rc_dealloc(p, inputs.length); return n; }
  player(slot: number) {
    const n = this.e.rc_player(this.h, slot, this.buf);
    if (n < 33) return null;
    const d = new DataView(this.e.memory.buffer, this.buf, 36);
    return { x: d.getInt32(0, true), y: d.getInt32(4, true), z: d.getInt32(8, true), yaw: d.getUint8(24), stun: d.getUint8(26), flags: d.getUint8(33) };
  }
  queuedDrops(): number { return this.e.rc_drops_queued(this.h); }
  ingestDrop(kind: number): number { return this.e.rc_ingest_drop(this.h, kind); }
  lids() {
    const n = this.e.rc_lids(this.h, this.buf, 9 * 700);
    const d = new DataView(this.e.memory.buffer, this.buf, 9 * n);
    return Array.from({ length: n }, (_, i) => ({ x: d.getInt32(9 * i, true), z: d.getInt32(9 * i + 4, true), s: d.getUint8(9 * i + 8) }));
  }
  sweepers() {
    const n = this.e.rc_sweepers(this.h, this.buf, 20 * 8);
    const d = new DataView(this.e.memory.buffer, this.buf, 20 * n);
    return Array.from({ length: n }, (_, i) => ({ px: d.getInt32(20 * i, true), pz: d.getInt32(20 * i + 4, true), arm: d.getInt32(20 * i + 8, true), c: d.getInt32(20 * i + 12, true) / 32768, s: d.getInt32(20 * i + 16, true) / 32768 }));
  }
}
