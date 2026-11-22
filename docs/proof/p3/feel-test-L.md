# P3-L feel test (user, 2026-11-23)

3 on-chain Lid Flip Finals on MagicBlock devnet-as (arena 2), the user playing the Mac player through the relay
(you + 2 bots, P2's keeper `MODE=final`): runs `p3-k-20261123-003426` (slot 0, champion 6), `p3-k-20261123-003632`
(slot 2, champion 8), `p3-k-20261123-003900` (slot 0, champion 2). All 3 complete; champion on screen = arena result.

Answers:
1. "Does on-chain feel as good as Classic?" → **As good as Classic.**
2. "Anything laggy (jump, lid falls, sweeper hits)?" → **Nothing laggy.**

No tuning was needed after the test. Client settings in effect: inputs stamped T+3 (+0..+3 adaptive from
applied_tick), 3 ticks per transaction, 120 ms smoothing, 2 m snap, remote interpolation 100–150 ms.
