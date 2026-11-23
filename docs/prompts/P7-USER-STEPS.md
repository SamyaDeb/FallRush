# P7: your steps (the parts you do by hand)

The P7 agent does the polish, hardening, PROOF.md, README, submission text and the secret scan by itself (`docs/prompts/P7-PROMPT.md`). It **stops twice** and waits for you. It never pushes, never makes the repo public, never fills the form and never makes the video. Those are yours.

| When (UTC / IST) | You do |
|---|---|
| Now | §1 start the session |
| Wed 25, 13:00–16:00 UTC (18:30–21:30 IST) | §2 playtest 1 |
| Thu 26, ~10:00 UTC (15:30 IST) | §3 playtest 2, recorded |
| Thu 26 – Fri 27 morning | §4 the video |
| **Fri 27, by 12:00 UTC (17:30 IST)** | §5 push + submit (hard deadline 14:00 UTC / 19:30 IST) |
| Fri 27 → results | §6 judging week |

---

## 1. Start
1. Open a **new** Claude Code session in `/Users/samya/Downloads/FallRush`.
2. Paste: `Read docs/prompts/P7-PROMPT.md and run it.`
3. Leave it running. The first stop is **USER GATE 1**: you'll get a short report plus `docs/proof/p7/playtest-kit.md`.

Meanwhile, line up testers for Wednesday:
- **≥ 8 players + 3 spectators**;
- **≥ 2 on real phones** (spectators on `/live`);
- **≥ 1 with real Phantom** (to claim assets on `/me`);
- ideally on **different networks** (not all on one Wi-Fi, which triggers devnet RPC rate limits).

## 2. USER GATE 1: playtest 1 (~1 h for 3–4 matches)
**Status (2026-11-23 22:25 UTC): ready.** The new build is live at https://fallrush.fun (Play / Live / Season / Me). Kit: `docs/proof/p7/playtest-kit.md` (in the worktree `../FallRush-p7` until the merge). Capacity: the guest-gas funder holds ≈ 0.67 SOL ≈ 56 new guests; 40 guests per network per day. If testers share one Wi-Fi, more than ~40 fresh browsers that day hit the limit (an incognito window = a fresh guest).
1. Send testers the "For testers" section and the feedback form from `docs/proof/p7/playtest-kit.md`.
2. Start everyone together (a call or a group chat). Follow the kit's run plan. In one match, someone closes their tab during the Final on purpose.
3. Note the match times. Collect screenshots of anything odd.
4. Back in the session, paste:
   `playtest 1 notes: <the notes, or a path to a file with the form answers>`
   The agent then fixes P0 / P1 issues (Part B) and stops at gate 2.

## 3. USER GATE 2: playtest 2, recorded (footage for your video)
1. Two **clean, full** matches with as many humans as you can get.
2. Record with OBS at 1080p60:
   - (a) the game window;
   - (b) a phone screen on `/live` (iOS / Android screen recording);
   - (c) a browser tab on the explorer.
3. Capture these shots for the doc 19 §7 storyboard:
   - the Deal + the VRF transaction;
   - Round 1 with the F3 tick-health overlay;
   - a phone bet + a Chaos Drop landing in game;
   - the Final + the market settling;
   - the Crown + capsules popping;
   - the receipt page Rollup ✓ → Solana ✓;
   - `/replay` matching.
4. Paste into the session: `playtest 2 done: <any notes>`. The agent then finishes Part C (PROOF.md, README, submission pack, secret scan, release-candidate freeze, merge).

If you can't run a playtest in time, just tell the session. It goes ahead with Part C either way.

## 4. The demo video (yours, ≤ 3:00)
- Storyboard: `docs/19-ONCHAIN-PLAN.md` §7.
- Footage: the playtest 2 recordings.
- Upload to **YouTube (unlisted or public)**. Optionally post it natively on X.
- Put the link in the README by replacing `<VIDEO_URL>` (or tell the session to do it).

## 5. Publish + submit (Fri 27, by 12:00 UTC)
1. **Read `docs/proof/p7/secret-scan.md` first.**
   - **Clean** → publish `main`.
   - **Dirty** → publish the clean `public` branch the agent prepared (instructions in that file), and rotate whatever it lists.
2. Check that `git status` is clean. Check that `PROOF.md`, `README.md` and `LICENSE` are there and that the README has your video link.
3. Create the GitHub repo (empty, no README), then push:
   ```bash
   git remote add origin git@github.com:<you>/fall-rush.git
   git push -u origin main          # or: git push -u origin public:main  (if the scan said so)
   ```
4. Set the repo to **Public** and add a description, topics (`solana`, `magicblock`, `ephemeral-rollups`, `unity`, `game`) and the fallrush.fun website.
5. Fill in the form at **build.magicblock.app** (Blitz V10) using `docs/proof/p7/submission.md`:
   - pitch;
   - demo / video link;
   - the public repo;
   - the explorer link;
   - the program address `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4`;
   - the project description;
   - **your prize wallet**;
   - your X handle.
6. Post the X thread from `submission.md` (tag MagicBlock).
7. Open fallrush.fun in a private window once after submitting to check it still loads.

## 6. Judging week (until the results, ~3–5 days)
- Once a day:
  - open fallrush.fun → Play, or `https://play.<…>/api/status`;
  - skim `/opt/fallrush/logs/watchdog.log` (or ask a session to run `scripts/p5-health.sh`).
- **Hostinger snapshots expire after 24 h.** Take a fresh one before letting anyone change the VPS.
- If the guest-gas funder falls below **0.3 SOL**, top it up (`solana transfer` from the admin wallet). New visitors need it.
- If the on-chain Final breaks, the Classic fallback keeps the game playable. Say so in any judge chat.

## Run these yourself (filled in by the agent if a command was denied)

### W1. Waitlist Worker CORS (denied for the agent on 2026-11-23, as in the landing run)
Lets the fallrush.fun landing call the waitlist API (`ALLOWED_ORIGINS` gets `https://fallrush.fun` and `https://www.fallrush.fun`). From the main checkout (it has the landing's `node_modules`):
```bash
cd /Users/samya/Downloads/FallRush && bash scripts/landing-deploy-cf.sh
```
Then in the Cloudflare dashboard → Turnstile → the waitlist widget → Hostnames: make sure `fallrush.fun` and `www.fallrush.fun` are listed. Test: sign up once on https://fallrush.fun with an address you own.
