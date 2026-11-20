# 11 — Security, Anti-Cheat & Trust Model

**Status:** v2 (2026-11-18).

## 1. Trust statement (MVP; it goes in the README and the demo)

The authoritative game server decides:
- physics
- collisions
- checkpoints
- who qualified

The `fall_rush` program on a MagicBlock Ephemeral Rollup accepts results only when they come:
- from the configured game authority,
- in strict sequence,
- for roster slots,
- with counts consistent with the committed ruleset.

The match deal comes from MagicBlock VRF and is verified on-chain. Results are committed to Solana every round.

**This is not trustless verification of physics.** Never claim "fully on-chain", "trustless gameplay" or "cheat-proof".

## 2. Threats → controls

| Threat | Control |
|---|---|
| Speed / teleport / fly hacks | Inputs only. The server motor is authoritative. Client positions are ignored. (Fallback F1, if invoked, is server-validated bounds with a snap-back.) |
| Input flooding / malformed packets | Size and rate caps; finite / range checks; drop and count; kick after 50 violations per minute. **As built (M2b):** ≤ 32 messages decoded per tick; rate violations from a token bucket (45 msg/s, burst 450) so a TCP stall's backlog is not a violation; commands > 300 ticks ahead are violations (31–300 dropped silently). Measured: a 200-msg/frame flood is kicked within 3 ticks; a 5 s stall is not (doc 04 §4.2, M2 proof B10) |
| Checkpoint skipping | Ordered checkpoints, server-side trigger plus a reachability check (distance from the previous one) |
| Replay / forged join | Single-use HMAC join tickets bound to user + server + expiry |
| Operator rerolls the deal | The deal is derived **in the VRF callback**; the pool hash is committed before the request; no reroll path in the program |
| Forged VRF callback | `#[vrf_callback]` scoped-identity signer (not the deprecated global identity); a test calls it directly and expects failure |
| Duplicate / out-of-order chain events | On-chain `event_seq` equality check; DB `(match_id, seq)` primary key |
| Fake champion | The program requires exactly one qualified slot in the last played round |
| Game authority key leak | Key scoped to `fall_rush` match instructions only; ≤ 5 devnet SOL; `admin` can rotate it via `update_config` or `set_paused` |
| Session key abuse (picks) | A session token is scoped to the `fall_rush` program with short validity (1 h). Picks have no value. The program checks the token's authority equals `picker`. |
| Trophy double-mint | Unique `trophies(match_id)`; check for an existing asset with the `matchPda` attribute before any retry |
| Secrets in builds | No keys in Unity or web builds. RPC URLs are public endpoints only. Secrets live only in `/opt/fallrush/.env` on the VPS (chmod 600). `.env` is git-ignored and `.env.example` holds placeholders. |
| Bots inflating results | Bots are flagged in the roster hash and HUD. Ranked status requires ≥ 4 humans. |

## 3. Keys

| Key | Holder | Powers |
|---|---|---|
| Program upgrade authority | Developer's local wallet (devnet) → multisig before mainnet | Upgrade program |
| `admin` (GameConfig) | Same as above for the hackathon | Config, pause, rotate |
| `game_authority` | Rush Relay (VPS `.env`, chmod 600) | Match lifecycle only |
| `mint_authority` | Rush Relay (separate secret) | Crown Capsule collection |

There is no treasury and no token custody.

## 4. Mainnet gates (unchanged from v1 §7; not in hackathon scope)

- Audit
- Multisig
- Legal review (India and other jurisdictions; any prize or economy feature)
- Privacy policy and terms
- RPC redundancy
- Incident runbook
