# Fall Rush demo video v3 draft (4:00), for roasting

Length is 4:00. There is no length limit: the build.magicblock.app form has a single "Pitch & Demo" field. The format is 1920×1080 at 60 fps.
- The founder records the voice-over (VO) and supplies 30–40 s of beta-user gameplay. Those are best moments, recorded today in public Quick Match lobbies on the on-chain arenas.
- Claude captures everything else and edits the video in HyperFrames.
- Judging criteria: creativity, technical depth, and "how compellingly they showcase what's possible on Solana". Track: Entertainment Finance.

## Thesis
"The game isn't next to the chain. The game *is* the program."

## Arc
1. A hook from the beta.
2. The impossible problem.
3. Live proof.
4. People use it (bet + sabotage).
5. Real players love it (the beta clip).
6. A human champion and the Crown.
7. How it works.
8. Don't trust us: replay.
9. CTA.

## Visual system ("clean, not slop")
- **Font.** Poppins only (OFL, Google Fonts):
  - 600 for headlines, 500 for labels and pills, 400 for captions.
  - Tabular figures for counters, if Poppins has them; otherwise fixed-width number boxes.
  - Tx signatures are Poppins 500, truncated `4iZa…j6YQ`.
- **Colour:**
  - Canvas: off-white `#F7F6F3` for full-frame cards, ink `#14161A` for text.
  - One accent: Fall Rush blue `#0182FD`, sampled from the logo.
  - Green `#1DB954` only for ✓ / byte-exact moments.
  - Over gameplay, text sits on white pills (radius 999, 1 px hairline `#E6E4DF`, one soft shadow `0 6px 20px rgba(0,0,0,.08)`).
- **Layout:**
  - 8-pt grid, 96 px safe margins, left-aligned, max 7 words per card.
  - Captions: lower third, white pill, 40 px, ≤ 2 lines.
- **Motion:**
  - Text: one mask-reveal up (0.35 s, power3.out), then hold. Numbers count up once.
  - One push-in per proof shot (1.0 → 1.4, 0.5 s).
  - No typewriter, glitch or neon glow, no 3D blockchain cubes, no emoji, no gradient text, no particle backgrounds.
- **Sound:**
  - Our own music bed, ducked 8–10 dB under the VO.
  - Real game SFX; beta laughter unducked.
  - A soft tick on each counter.
  - Master to −14 LUFS.

## Script (VO ≈ 2.1 words/s; the beta clip is mostly unnarrated)

| # | Time | Picture | On screen (Poppins) | VO |
|---|---|---|---|---|
| 1 | 0:00–0:08 | Beta: the best laugh at a knockout. Freeze on the hit; the rollup tx pill slides in. | `this knockout = a Solana transaction` | "This knockout… is a Solana transaction." |
| 2 | 0:08–0:13 | Logo sting over game audio | FALL RUSH · Bet on it. Sabotage it. Win the Crown. | "This is Fall Rush. Twelve players. Four rounds. One crown." |
| 3 | 0:13–0:28 | A 2 s shot of each of the 4 maps, then a black card | `A physics game needs a new world 30× a second.` → `Solana's base layer can't.` | "A physics game needs a new world thirty times a second. Solana's base layer can't do that… a MagicBlock Ephemeral Rollup can." |
| 4 | 0:28–0:40 | Incognito → fallrush.fun/play?judge=1 → lobby, with a real timer | `no wallet · no popups` · `session key signs every move` | "Click a link. You're in. No wallet. No popups. A session key signs every move, and the program checks it every time." |
| 5 | 0:40–1:25 | **Hero proof, split screen.** Left: gameplay with ◆ Rollup ✓. Right: the `tick-watch` terminal reading the World account from devnet-as.magicblock.app: tick counter, ticks/s, your height jumping on Space. Push-in on the explorer tick() tx. | `tick() · 30 per second` → `1 tick = up to 1.58M CU` → `1 transaction ≤ 1.4M CU` → `resumable physics · sliced · anyone can crank` → `rush-core · 15k lines · integer Rust → sBPF + WASM` | "Every thirty-three milliseconds, our program steps the physics. Not a server. The program. … One full twelve-player tick costs more compute than a transaction allows. So we made the physics resumable: it runs in slices, and anyone can send the next slice. The result never changes. … No floats anywhere, so the chain, your browser and our tests get identical bytes. All four rounds. Real time." |
| 6 | 1:25–1:33 | Bots racing, one labelled | `empty seats → bots · their brains run inside the program` | "Empty seats fill with bots, and their brains run inside the program too." |
| 7 | 1:33–2:00 | Phone frame (/live) + game split. Bet → odds move → Chaos Drop (Bumper Rain) → warning → it lands. Sealed Champion pick with the attestation ✓ line. | `Rush Markets · settled by the program` → `Chaos Drop · lands in the on-chain world` → `Private ER · sealed until close` | "Your friends watch from their phones. They bet, and the program pays the winners. … Or they spend chips to drop chaos into the Final. … Champion picks stay sealed inside a private rollup until betting closes." |
| 8 | 2:00–2:40 | **BETA CLIP (founder's 30–40 s).** Opens with a title card, then unnarrated best moments with real audio. Lower-third name pills (with permission). Ends on a stat card. | `This week: real players.` · end: `[N] players · [M] on-chain matches · [B] bets · 0 wallet popups` | Over the title card only: "This week, we put it in front of real players." … *(silence; the clip plays)* … over the stat card: "Nobody asked what a wallet was. They asked for a rematch." *(only if true)* |
| 9 | 2:40–3:02 | Lid Flip Final, 3 left → 1; a human wins → Crown ceremony → explorer undelegate tx with the Magic Action → the Crown asset in the wallet | `Magic Action · Crown minted on undelegate` | "Three left. One crown. … When the match commits back to Solana, a Magic Action mints the Crown, straight to the winner." |
| 10 | 3:02–3:25 | **How it works**, an animated diagram on the off-white canvas, built step by step: Player → session key → ER (tick 30/s, markets, drops) → commit → Solana (Match, receipt, Crown). Plus VRF and the Private ER as side nodes. | node labels only | "Here's the whole loop. Your session key signs inputs into the rollup. Our program steps the world, runs the markets and the drops. MagicBlock VRF deals the match. Every round commits to Solana. Our server only seats players: it can't pick a winner." |
| 11 | 3:25–3:48 | Receipt page: Rollup ✓ → Solana ✓ → replay check (sped up) → holds on `N / N byte-exact`. Quick cut: `laptop-crank` terminal sends crank() → signature lands. | `replay = on-chain ✓` · `N / N snapshots byte-exact` | "Don't trust us. Every match leaves a receipt on Solana. Replay it in your browser… every snapshot matches. Byte for byte." |
| 12 | 3:48–4:00 | End card: fallrush.fun/play?judge=1 + QR, MagicBlock + Solana logos; small print `devnet · test chips · ER · VRF · Session Keys · Private ER · Magic Actions` | | "The game is the program. Play one now, at fallrush dot fun." |

## Constraints (from the earlier fact-check)
- Chaos Drops, sealed bets and a human-won Crown have never been exercised in a full on-chain match. A rehearsal match goes first, and each beat has a fallback line.
- The beta clip must come from matches that show ◆ Rollup ✓. Count only on-chain matches.
- Don't say "1.4M-CU slices" (that's the limit; measured peak ~940k).
- The replay count comes from the filmed match, not the old 21,540.
- Bots are disclosed (beat 6).
