// P2.10 chain worker: Classic server rounds 1–3 → the on-chain Match. Tails the Classic server's
// match-logs/match-<id>.jsonl (M4 outbox: {type:"match"} header, {type:"event", seq, name, round, slot, arg}) and sends
// start_round / record_qualified / record_eliminated / complete_round (+ commit), signed by the game authority.
// Idempotent through event_seq: an event is sent only when its seq equals Match.event_seq (DEAL = seq 0 on both sides:
// the on-chain DEAL is the VRF callback). Stops after COMPLETE_ROUND 3 and returns the Final's 3 slots (round-3
// qualifiers) for start_final.
import { PublicKey } from "@solana/web3.js";
import { readFileSync, existsSync } from "node:fs";
import { pe, wallet, sendEr, sleep, log } from "../keeper/lib.ts";

export interface ClassicHeader { matchId: Buffer; rosterHash: Buffer; players: number; targets: number[]; seed: string }

export function readLines(path: string): any[] {
  if (!existsSync(path)) return [];
  return readFileSync(path, "utf8").split("\n").filter((l) => l.trim()).map((l) => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);
}

/** Waits for the jsonl's match header. */
export async function header(path: string, timeoutMs = 600_000): Promise<ClassicHeader> {
  const t0 = Date.now();
  for (;;) {
    const h = readLines(path).find((l) => l.type === "match");
    if (h) return { matchId: Buffer.from(h.matchId, "hex"), rosterHash: Buffer.from(h.rosterHash, "hex"), players: h.players, targets: h.targets, seed: h.seed };
    if (Date.now() - t0 > timeoutMs) throw new Error(`no match header in ${path}`);
    await sleep(500);
  }
}

const argByte = (a: any) => (typeof a === "string" && a.length >= 2 ? parseInt(a.slice(0, 2), 16) : 0);

/** Rounds 1–3 from the jsonl into `matchPda` (Dealt or later). Returns the 3 finalists. */
export async function bridgeRounds13(path: string, matchPda: PublicKey, logFile: string, timeoutMs = 1_800_000): Promise<{ finalists: number[]; sent: number; skipped: number }> {
  const gp = { authority: wallet.publicKey, matchAccount: matchPda };
  let sent = 0, skipped = 0;
  const t0 = Date.now();
  for (;;) {
    const m: any = await pe.account.match.fetch(matchPda);
    const seq: number = m.eventSeq;
    const evs = readLines(path).filter((l) => l.type === "event");
    const done3 = evs.find((e) => e.name === "COMPLETE_ROUND" && e.round === 3);
    if (done3 && seq > done3.seq) {
      const fin = evs.filter((e) => e.name === "QUALIFIED" && e.round === 3).map((e) => e.slot).sort((a: number, b: number) => a - b);
      log(logFile, { act: "chain_worker_done", finalists: fin, sent, skipped, seq });
      return { finalists: fin, sent, skipped };
    }
    const e = evs.find((x) => x.seq === seq);
    if (!e) {
      if (Date.now() - t0 > timeoutMs) throw new Error(`chain worker: event seq ${seq} never appeared`);
      await sleep(300);
      continue;
    }
    // events before seq are on-chain already (idempotent restart)
    skipped = evs.filter((x) => x.seq < seq).length;
    let ix: any;
    switch (e.name) {
      case "START_ROUND": ix = pe.methods.startRound(e.round, seq); break;
      case "QUALIFIED": ix = pe.methods.recordQualified(e.round, e.slot, argByte(e.arg), seq); break;
      case "ELIMINATED": ix = pe.methods.recordEliminated(e.round, e.slot, argByte(e.arg), seq); break;
      case "COMPLETE_ROUND": ix = pe.methods.completeRound(e.round, seq); break;
      default: throw new Error(`chain worker: unexpected event ${e.name} at seq ${seq}`);
    }
    const r = await sendEr([await ix.accountsPartial(gp).instruction()], [wallet], `${e.name} ${e.round}/${e.slot}`, 10);
    log(logFile, { act: "chain_worker", seq, name: e.name, round: e.round, slot: e.slot, ms: r.ms, err: r.err, sig: r.sig });
    if (r.err) throw new Error(`chain worker ${e.name} seq ${seq}: ${r.err}`);
    sent++;
  }
}
