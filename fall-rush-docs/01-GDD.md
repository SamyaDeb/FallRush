# 01 — Game Design Document (GDD)

## 1. High concept

A colorful, chaotic multiplayer knockout party game. Players control original toy-like competitors through obstacle courses, survival arenas, team challenges, and a final showdown. Each match is a sequence of distinct maps; the field shrinks after every round until one champion remains.

**Design promise:** easy to understand in 10 seconds, satisfying to master over 100 matches.

## 2. Design pillars

- **Readable chaos:** hazards are visually obvious and telegraphed before impact.
- **Skill over spending:** cosmetics never improve speed, jump height, collision mass, hitbox, or qualification odds.
- **Short rounds:** target 60–150 seconds per round; target 6–10 minutes per full match.
- **Fair competition:** same map, seed, rules, and obstacle schedule for every player in a match.
- **Spectacle:** expressive character animation, clear elimination feedback, strong sound and camera work.
- **Wallet optional:** no wallet popup during core gameplay.

## 3. Target audience

Casual and competitive players who enjoy party games, short-session multiplayer, streamer-friendly chaos, and collectible cosmetics. The game should not assume crypto knowledge.

## 4. Match format

### Default 20-player target (production)

| Stage | Mode | Start | Qualify | Eliminated |
|---|---|---:|---:|---:|
| Round 1 | Race | 20 | 12 | 8 |
| Round 2 | Survival | 12 | 8 | 4 |
| Round 3 | Skill / team variant | 8 | 4 | 4 |
| Final | Last player standing | 4 | 1 | 3 |

### Hackathon configuration

Use 10–12 slots and scale qualification thresholds proportionally. Example for 12: race 12→8; survival 8→5; skill 5→3; final 3→1. Configuration is server-controlled and versioned. Never hardcode round thresholds in the Unity client.

## 5. Round modes

### Race
Players navigate a course and cross checkpoints in order. First N valid finishers qualify. If fewer than N finish before the timer, the best valid checkpoint progress / elapsed time ranking is used. Tie-breaks must be deterministic and documented.

### Survival
Players remain on an arena while hazards move or platforms disappear. At timer expiry, top N by survival state or the survivors qualify. Server determines fall/elimination, not client claims.

### Skill challenge
A short individual challenge: timed gates, moving platforms, precision jumps, or checkpoint collection. Ranking is based on validated score/time.

### Team challenge (stretch)
Teams cooperate to reach a threshold. Team assignment must be balanced and randomization must be committed before play. Avoid team mode in the first MVP if it threatens delivery.

### Final
A compact arena with shrinking safe space, timed hazards, or disappearing platforms. Last valid player alive wins. If timer expires, deterministic survival/score tiebreaker decides champion.

## 6. Player controls

Keyboard baseline:
- WASD: move
- Mouse: camera
- Space: jump
- Shift: dive / burst (cooldown and physics tuned)
- E: interact / grab only where the map requires it
- Esc: settings / leave (leave confirmation during match)

Controller mapping is a post-hackathon requirement. Input buffering and coyote time should make jumps forgiving without enabling exploits.

## 7. Character controller

Use a capsule-based authoritative collider, with cosmetic mesh/rig separate from the collision capsule. Server owns position, velocity, grounded state, stun state, checkpoint state, and elimination state. Client predicts local movement and reconciles to snapshots.

Target feel: responsive acceleration, readable momentum, consistent jump arc, limited air control, predictable collision response. Avoid uncontrolled player-to-player physics impulses in MVP; they create desync and griefing. Use server-defined push/stumble rules if needed.

## 8. Map pool — MVP

1. **Sky Dash (race):** floating platforms, broad ramps, rotating beam, moving gates, checkpoint sequence, finish line.
2. **Hammer Havoc (race/skill):** timed swinging paddles, rotating sweepers, safe lanes, checkpoint sequence.
3. **Slime Escape (survival/climb):** rising hazard plane, moving platforms, falling blocks, vertical route.
4. **Last Island (final):** modular safe platforms that disappear on contact, bounded arena, last-player-standing.

All names are working names. Create original layouts, art, props, sounds, obstacle silhouettes, and map compositions. Never trace or reproduce a specific Fall Guys level.

## 9. Map selection and fairness

Before the match starts, server requests a VRF result for a match-specific request. A deterministic mapping function converts the random bytes into an allowed sequence from a versioned map pool. The sequence is stored in match state and disclosed to all players. Do not use modulo reduction naively where bias matters; use a documented rejection-sampling / range-reduction method. The map pool and weights are fixed before requesting randomness. Do not allow the operator to reroll until a preferred sequence appears.

For MVP, randomness selects map order and a small number of safe, prevalidated obstacle variants. It must not select impossible layouts or secretly change one player's conditions.

## 10. Progression and collectibles

- Account-free guest profile supported for play.
- Optional wallet linking after a match or in profile.
- XP is earned for participation, qualification, and champion status; XP is not transferable or redeemable.
- Cosmetic items: skins, hats, trails, emotes, victory poses. Cosmetics do not affect stats.
- Achievements: first qualification, first final, champion, season participation.
- On-chain minting should be opt-in and user-initiated; do not mint an NFT for every action.
- Marketplace/trading is post-MVP and must support moderation, metadata persistence, and legal review.

## 11. Economy principles

No pay-to-win. No paid random loot boxes in MVP. No token emissions or advertised yield. If randomized cosmetic packs are later introduced, publish odds, use verified randomness, comply with consumer/gambling rules, and provide age/jurisdiction controls. The initial hackathon demo can use free earned cosmetic drops.

## 12. UX flows

1. Launch → choose guest / connect wallet (wallet optional).
2. Main menu → Play → matchmaking queue.
3. Lobby → ready state → match sequence disclosed.
4. Round intro → gameplay → qualification / elimination result.
5. Intermission → next map.
6. Final → champion ceremony → match summary.
7. Rewards → XP / achievement; optional wallet claim or collectible mint.
8. Return to lobby.

## 13. Accessibility and usability

High-contrast hazard indicators, color-independent team markings, subtitles for important audio cues, adjustable camera sensitivity, remappable controls post-MVP, motion-reduction option, and clear latency/reconnect states.

## 14. Success metrics

Hackathon: successful full match completion, match completion rate, median RTT, disconnect rate, server correction rate, ER update success, VRF verification, commit success, demo clarity. Production: D1/D7 retention, matches per player, queue time, crash-free sessions, qualified-player rate, cosmetics earned/owned, support incidents.
