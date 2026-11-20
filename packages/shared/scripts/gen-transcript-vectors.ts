// Generates test-vectors/transcript_vectors.json for the C# parity tests (M4):
//   - round targets for P = 4…20 (targets.ts),
//   - roster hashes (M4 proposal, defined HERE until M5/M6 move it into src/: see ROSTER_HASH below),
//   - rolling transcripts (transcript.ts) for scripted matches: a full 12-player 4-round match, a Survival
//     minimum-advance match that ends in round 2 with one survivor, and an aborted match.
// Nothing in src/ is changed; this script only calls the reference functions.
//
// ROSTER_HASH (doc 06 §1 "(slot, H(user_id), wallet?)", M4 proposal):
//   roster_hash = sha256("fallrush-roster-v1" ‖ u8 P ‖ for slot = 0 … P−1:
//                        u8 slot ‖ u8 kind (0 guest, 1 bot) ‖ sha256(utf8 user_id) ‖ u8 hasWallet ‖ [wallet 32 B])
//   Placeholder user ids until M5 issues guest identities (TODO(M5)): bots "bot:<slot>", guests "guest:practice:<slot>".
import { createHash } from "node:crypto";
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { roundTargets, transcriptInit, transcriptNext, TAG, NO_SLOT, deriveDeal } from "../src";

const sha = (...parts: (Buffer | Uint8Array | string)[]) => {
  const h = createHash("sha256");
  for (const p of parts) h.update(p);
  return h.digest();
};

interface RosterEntry { slot: number; bot: boolean; userId: string; wallet?: string }

export function rosterHash(roster: RosterEntry[]): Buffer {
  const parts: Buffer[] = [Buffer.from("fallrush-roster-v1"), Buffer.from([roster.length])];
  roster.forEach((r, i) => {
    if (r.slot !== i) throw new Error("roster slots must be 0…P−1 in order");
    parts.push(Buffer.from([r.slot, r.bot ? 1 : 0]), sha(Buffer.from(r.userId, "utf8")));
    if (r.wallet) {
      const w = Buffer.from(r.wallet, "hex");
      if (w.length !== 32) throw new Error("wallet must be 32 bytes");
      parts.push(Buffer.from([1]), w);
    } else parts.push(Buffer.from([0]));
  });
  return sha(...parts);
}

const placeholder = (slot: number, bot: boolean) => (bot ? `bot:${slot}` : `guest:practice:${slot}`);
const practiceRoster = (p: number, humans: number[]): RosterEntry[] =>
  Array.from({ length: p }, (_, s) => ({ slot: s, bot: !humans.includes(s), userId: placeholder(s, !humans.includes(s)) }));

interface Ev { tag: number; seq: number; round: number; slot: number; arg: string }

function chain(matchId: Buffer, roster: Buffer, events: Omit<Ev, "seq">[]) {
  let h = transcriptInit(matchId, roster);
  const init = h.toString("hex");
  const out: (Ev & { after: string })[] = [];
  events.forEach((e, seq) => {
    h = transcriptNext(h, e.tag, seq, e.round, e.slot, Buffer.from(e.arg, "hex"));
    out.push({ ...e, seq, after: h.toString("hex") });
  });
  return { init, events: out, final: h.toString("hex") };
}

const u8 = (n: number) => Buffer.from([n]).toString("hex");

// ---- match A: 12 players (slot 0 human), 4 rounds, scripted orders (the C# director test replays these).
function matchA() {
  const seed = sha("fallrush-m4-vector-match-A");
  const matchId = sha("fallrush-match-id", seed).subarray(0, 16);
  const roster = practiceRoster(12, [0]);
  const rh = rosterHash(roster);
  const targets = roundTargets(12);
  // Round orders (place 1 first) among the alive slots; the first `target` qualify.
  const orders = [
    [3, 0, 7, 1, 9, 4, 11, 2, 5, 6, 8, 10],
    [0, 9, 2, 3, 7, 1, 4, 11],
    [9, 2, 0, 7, 3],
    [2, 9, 0],
  ];
  // Elimination reasons by place beyond the target (scripted; the real rules choose them).
  const reasons = [
    [0, 0, 4, 0],
    [1, 2, 3],
    [3, 3],
    [2, 4],
  ];
  const ev: Omit<Ev, "seq">[] = [{ tag: TAG.DEAL, round: 0, slot: NO_SLOT, arg: seed.toString("hex") }];
  orders.forEach((order, i) => {
    const round = i + 1;
    ev.push({ tag: TAG.START_ROUND, round, slot: NO_SLOT, arg: "" });
    const t = targets[i];
    order.slice(0, t).forEach((slot, k) => ev.push({ tag: TAG.QUALIFIED, round, slot, arg: u8(k + 1) }));
    order.slice(t).forEach((slot, k) => ev.push({ tag: TAG.ELIMINATED, round, slot, arg: u8(reasons[i][k]) }));
    ev.push({ tag: TAG.COMPLETE_ROUND, round, slot: NO_SLOT, arg: u8(t) });
  });
  ev.push({ tag: TAG.COMPLETE_MATCH, round: 4, slot: orders[3][0], arg: "" });
  return {
    name: "full-12p-4r", seed: seed.toString("hex"), matchId: matchId.toString("hex"), players: 12, humans: [0],
    rosterHash: rh.toString("hex"), targets, deal: deriveDeal(seed), orders, reasons, ...chain(matchId, rh, ev),
  };
}

// ---- match B: Survival minimum-advance: 1 survivor in round 2 → champion, rounds 3–4 skipped.
function matchB() {
  const seed = sha("fallrush-m4-vector-match-B");
  const matchId = sha("fallrush-match-id", seed).subarray(0, 16);
  const roster = practiceRoster(12, [0, 5]);
  const rh = rosterHash(roster);
  const ev: Omit<Ev, "seq">[] = [{ tag: TAG.DEAL, round: 0, slot: NO_SLOT, arg: seed.toString("hex") }];
  const r1 = [5, 1, 2, 3, 4, 0, 6, 7, 8, 9, 10, 11];
  ev.push({ tag: TAG.START_ROUND, round: 1, slot: NO_SLOT, arg: "" });
  r1.slice(0, 8).forEach((s, k) => ev.push({ tag: TAG.QUALIFIED, round: 1, slot: s, arg: u8(k + 1) }));
  r1.slice(8).forEach((s) => ev.push({ tag: TAG.ELIMINATED, round: 1, slot: s, arg: u8(0) }));
  ev.push({ tag: TAG.COMPLETE_ROUND, round: 1, slot: NO_SLOT, arg: u8(8) });
  // Round 2: only slot 3 survives the goo.
  ev.push({ tag: TAG.START_ROUND, round: 2, slot: NO_SLOT, arg: "" });
  ev.push({ tag: TAG.QUALIFIED, round: 2, slot: 3, arg: u8(1) });
  [5, 1, 2, 4, 0, 6, 7].forEach((s) => ev.push({ tag: TAG.ELIMINATED, round: 2, slot: s, arg: u8(1) }));
  ev.push({ tag: TAG.COMPLETE_ROUND, round: 2, slot: NO_SLOT, arg: u8(1) });
  ev.push({ tag: TAG.COMPLETE_MATCH, round: 2, slot: 3, arg: "" });
  return { name: "survival-one-survivor", seed: seed.toString("hex"), matchId: matchId.toString("hex"), players: 12, humans: [0, 5], rosterHash: rh.toString("hex"), ...chain(matchId, rh, ev) };
}

// ---- match C: aborted in round 1 (reason 1 = no humans left).
function matchC() {
  const seed = sha("fallrush-m4-vector-match-C");
  const matchId = sha("fallrush-match-id", seed).subarray(0, 16);
  const rh = rosterHash(practiceRoster(12, [0]));
  const ev: Omit<Ev, "seq">[] = [
    { tag: TAG.DEAL, round: 0, slot: NO_SLOT, arg: seed.toString("hex") },
    { tag: TAG.START_ROUND, round: 1, slot: NO_SLOT, arg: "" },
    { tag: TAG.ABORT, round: 1, slot: NO_SLOT, arg: u8(1) },
  ];
  return { name: "abort-r1", seed: seed.toString("hex"), matchId: matchId.toString("hex"), players: 12, humans: [0], rosterHash: rh.toString("hex"), ...chain(matchId, rh, ev) };
}

const rosters = [
  { name: "12-practice-h0", entries: practiceRoster(12, [0]) },
  { name: "4-all-guests", entries: practiceRoster(4, [0, 1, 2, 3]) },
  { name: "20-bots", entries: practiceRoster(20, []) },
  {
    name: "5-with-wallet",
    entries: practiceRoster(5, [0, 1]).map((r) => (r.slot === 1 ? { ...r, userId: "user:7f3a", wallet: sha("wallet-1").toString("hex") } : r)),
  },
].map((r) => ({ ...r, hash: rosterHash(r.entries).toString("hex") }));

const targets = Array.from({ length: 17 }, (_, i) => ({ p: i + 4, targets: roundTargets(i + 4) }));

const out = { version: 1, targets, rosters, matches: [matchA(), matchB(), matchC()] };
const path = join(__dirname, "..", "test-vectors", "transcript_vectors.json");
writeFileSync(path, JSON.stringify(out, null, 1) + "\n");
console.log(`wrote ${targets.length} target rows, ${rosters.length} rosters, ${out.matches.length} transcripts to ${path}`);
