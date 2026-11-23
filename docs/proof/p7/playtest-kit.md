# Fall Rush · playtest kit (P7, playtest 1)

Share sections 1 and 2 with the testers as they are. Sections 3 and 4 are for you (the host).

---

## 1. For testers

**Link: https://fallrush.fun → Play** (a laptop with Chrome or Edge works best; Safari and Firefox also run it).

**How it works (≤ 120 words).** You're a Rushling, a wind-up capsule toy. 12 players race through 4 rounds; bots fill the empty seats. Rounds 1–3 knock players out; the last round, **Lid Flip**, runs on a **MagicBlock rollup** on Solana. Stand on the lids, dodge the sweeper, be the last one standing to win the **Crown**. Out early? You keep playing as a spectator: press **B** to bet chips on who survives, or drop **Chaos Drops** on the Final. The **Champion bet is sealed**: nobody, not even us, can see your pick until betting closes. No wallet, no popups: you get a guest wallet and free test chips automatically (devnet, no real money).

**Controls.** WASD / arrows move · Space jump · Shift or left click dive · mouse look · 1–4 emotes · Tab: watch the next player · B: Rush Markets (bets) · Esc: settings.

**Phones.** Use a phone for **https://fallrush.fun/live**: watch the Final live as a 2D map, bet, and buy Chaos Drops while someone else plays.

**Phantom.** If you have the Phantom extension (devnet), open **https://fallrush.fun/me** after a match → **Connect Phantom** → **Claim to Phantom** on your capsule (or the Crown if you won).

After each match, the game shows a **receipt**: it ticks *Rollup ✓ → Solana ✓*, then shows what was minted to you. **Open receipt** shows the full proof page, which can replay the Final in your browser.

---

## 2. Feedback form (copy into Google Forms or the group chat)

1. Device and browser (e.g. "MacBook Air, Chrome" / "iPhone 15, Safari for /live"):
2. Time from clicking **Play** to being in the lobby (about how many seconds?):
3. Bugs: what happened, when (round / screen), and a screenshot if you can:
4. **Feel** of the movement and the maps (1 = bad, 5 = great):
5. **The cards and menus** (deal, results, leaderboard, receipt) (1–5):
6. **Betting / markets fun** (1–5):
7. **Chaos Drops fun** (1–5):
8. Did anything feel unfair, confusing, or too slow?
9. **The one thing to fix** before launch:

---

## 3. Run plan (host)

- **3–4 matches**, back to back, all on https://fallrush.fun (Quick Play seats everyone in the fullest open arena; with more than 12 people the second arena fills).
- **Players**: as many laptops as you have (aim for 4–8 humans; bots fill up to 12).
- **Spectators**: 2–3 people on **phones** on `/live`. In each match they should place **one open bet** (Qualify / Final winner), **one sealed Champion bet**, and buy **one Chaos Drop** during the Final.
- **Phantom**: anyone who has the extension tries the claim on `/me` after match 1 or 2.
- **Reveal-path test (once, in match 3)**: one spectator who placed a **sealed** bet **closes their tab as soon as the Final starts**. Their pick must still be revealed and settled (the private book reveals it; if the TEE is slow, the program's commit-reveal fallback handles it). Check their payout on the receipt.
- Between matches: collect the form answers in the chat; note the match receipt links (the game's **Open receipt** button).
- When done, send the agent: **"playtest 1 notes: …"** (paste the answers, or a file path).

## 4. What the agent watches live

- `bash scripts/p7-watch.sh` (every 10 s): each arena's phase / humans / keeper stage / Final tick, the guest-gas funder balance, and new warnings or errors from the keepers, game servers and the indexer.
- `/api/status` and the recent matches on https://fallrush.fun/arenas.
- If something breaks mid-match: the match aborts cleanly and **every stake is refunded**; the next lobby opens by itself.
