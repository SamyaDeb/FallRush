// harden: indexer decisions.  node --experimental-strip-types --no-warnings --test tools/indexer/policy.test.ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { idle, serverIdle, keeperIdle, lowFlags, seasonPause } from "./policy.ts";

const arena = (keeper: any, o: any = {}) => ({ id: "gs2", up: true, url: "wss://x", room: false, inMatch: false, phase: "LobbyOpen", humans: 0, arena: 3, keeper, ...o });
test("idle() is false while the arena's keeper is still in onchain_match (server already in the lobby)", () => {
  const sv = arena({ alive: true, stage: "onchain_match" });
  assert.equal(serverIdle(sv), true);
  assert.equal(keeperIdle(sv), false);
  assert.equal(idle(sv), false);
});
test("idle() needs a live keeper in stage idle; Classic-only servers have no keeper", () => {
  assert.equal(idle(arena({ alive: true, stage: "idle" })), true);
  assert.equal(idle(arena({ alive: false, stage: "idle" })), false);
  assert.equal(idle(arena({ alive: true, stage: "season" })), false);
  assert.equal(idle(arena(undefined)), false);
  assert.equal(idle({ id: "gs3", up: true, url: "wss://y", room: false, inMatch: false, phase: "Countdown", humans: 0, arena: null, classic: true }), true);
  assert.equal(idle(arena({ alive: true, stage: "idle" }, { humans: 1 })), false);
});
test("lowFlags thresholds 0.3 / 0.1 / 0.2 SOL", () => {
  assert.deepEqual(lowFlags({ authorityLamports: 1e9, treasuryLamports: 36_806_480, funderLamports: 753_710_000 }), { authority: false, treasury: true, funder: false, any: true });
  assert.equal(lowFlags({ authorityLamports: -1, treasuryLamports: null, funderLamports: null }).any, false);
});
test("seasonPause: any keeper paused (commit cap) or SEASONS=0", () => {
  assert.deepEqual(seasonPause([{ seasons: true, seasonsPaused: false }, undefined]), { paused: false, reason: null });
  assert.deepEqual(seasonPause([{ seasons: true, seasonsPaused: true, seasonsPausedReason: "commit cap" }]), { paused: true, reason: "commit cap" });
  assert.deepEqual(seasonPause([{ seasons: false, seasonsPaused: true, seasonsPausedReason: "SEASONS=0" }]), { paused: true, reason: "SEASONS=0" });
  assert.deepEqual(seasonPause([{ stage: "idle" }]), { paused: false, reason: null }); // an older keeper: no fields
});
