# Fall Rush — private-beta production audit (2026-11-24)

Scope: the whole live game at https://fallrush.fun (landing, /play WebGL game, /live, /season, /me, /receipt, /replay),
the public API (indexer), arena keepers, the three game servers (gs1 arena 2, gs2 arena 3, gs3 Classic rooms), host
nginx, and the browser chain bundle. Goal: safe for a **private beta of ~20 people at once** (the 12-player match cap
stays: "20 people" = 20 concurrent, spread over the servers, plus spectators).

Method: 3 parallel code audits (server APIs, game server, web app), every page loaded in a clean headless Chrome at
390 px and 1440 px against production, local race / abuse tests against the indexer, EditMode tests, then a 20-player
load test on production (headless game clients going through the real matchmaker + headless /live bettors).


## 1. Verdict

**GO for a private beta of ~20 concurrent people**, with the fixes below deployed (2026-11-24 17:52–18:05 UTC), the
known limits in §5 accepted, and the checklist in §6 done before invites go out. Load-test numbers: §4.

## 2. Critical / high findings — FIXED and deployed

| # | Sev | Finding | Fix (where) | Verified |
|---|---|---|---|---|
| C1 | critical | **Guest-gas faucet drain.** `/api/gas` checked its limits, then awaited the RPC, and only then wrote the ledger: N parallel requests all passed and each sent 0.012 SOL. One burst could empty the funder → no new guest can play. | All checks **and the reservation** happen synchronously before the first await; undo on a failed send; IPv6 limits keyed by /64; hourly cap 40 (`tools/indexer/server.ts`, compose) | local: 20 parallel calls for one wallet → **1** send; 10 wallets with cap 3 → **3** sends |
| C2 | critical | **Funder runway.** Funder held 0.41 SOL ≈ 37 new guests. | +0.50 devnet SOL from admin (admin 2.34 ≥ 1.0 floor) → 0.91 SOL ≈ 80 guests (`docs/proof/p7/sol.md`) | `/api/health` funderLamports 909,775,000 |
| H1 | high | **Guests could get gas only once, ever.** Each session key costs ~0.004 SOL; on the 2nd–3rd visit the burner ran dry and /live said "betting unavailable" forever. | One top-up per wallet per **24 h** (indexer); session renewed with `force` when < 10 min left (`web/app/src/lib/chain.ts`) | code + typecheck |
| H2 | high | **No rate limit on the API**, which calls the public devnet RPC from the IP the keepers share (an id sweep on `/api/season` → 429s → keepers stall). | host nginx `limit_req` 20 r/s, burst 60 per real client on `/api/` (play host only, `nginx -t`, reload); `/health` funder balance cached 30 s; season ids validated + caches bounded | burst of 150 → 31× 429, normal traffic 200 |
| H3 | high | **On-chain Final had no server timeout.** If the rollup task / keeper died after the hand-off, the arena sat in the Final forever (until every human closed the tab). | `ChainFinalRules.TimedOut` 120 s after the Final's own timer → `Abort(ServerStop)` (keeper refunds); slot bytes from chain range-checked | EditMode 462/0 |
| H4 | high | **Seats for 20 people.** Connections were capped at 12 *including watchers*, the matchmaker did not know about watchers, and a full lobby kicked the 13th person ("Server full"). | Server admits 16 connections (12 seats + 4 watchers); a person who finds the lobby full now **watches and is seated in the next lobby**; status publishes `conns`; matchmaker skips full servers and **overflows to gs3 (Classic)** before "watch" | live status shows `conns`/`maxConns 16`; load test §4 |
| H5 | high | **Rooms took a public arena first** (halving public capacity) and two concurrent "Create room" calls could both grab the same server (dead invite, server reopened to the public). | Rooms try gs3 first; one room decision at a time (async mutex); one open room per network; an unjoined room is released after 3 min | local: 3 concurrent creates → 1 room on gs3 |
| H6 | high | **Public "Start now (host)"**: the first arrival could start a public match alone while 19 others were still being routed. | Public lobbies always count down (server ignores Start; button hidden); a room that opens mid-countdown waits for its host | EditMode test `BetaAudit_PublicLobby_NoHostStart_RoomStill` |
| H7 | high | **Reconnect lost the seat.** After a network change the server still held the old connection, so the reconnect became a watcher and the player's Rushling was eliminated as DISCONNECT. | A hello whose token matches a still-connected slot drops the stale connection and resumes the seat (lobby and match) | EditMode test `BetaAudit_FindConnectedSlot_…` |

## 3. Medium / low findings — FIXED and deployed

| # | Finding | Fix |
|---|---|---|
| M1 | Landing crashed at desktop width: `TypeError: Cannot read properties of null (reading 'color')` in the 3D story canvas (fog read before / after its effect). | null guard (`web/site/src/three/StoryCanvas.tsx`); og:url now fallrush.fun. Production sweep: **0 console errors** |
| M2 | Player names went unsanitised into rich-text nameplates and JSON lines (`<size=200>` blew up screens; a trailing `\` broke the prejoin JSON). | `MatchDirector.CleanName`: no control chars, `< > \ "`, bidi/zero-width; empty → "Player N" (+ test) |
| M3 | Room codes were 4 chars (~1 M) with no limit on lookups → guessable. | 6 chars (~1 B, the game accepts 4–8); 30 wrong codes / 10 min per network → 429 |
| M4 | A hung keeper reported healthy (the heartbeat is a timer). | `/health` is not-ok when a match stage has not moved for 25 min → the watchdog restarts it (resume / abort + refund); TEE fetches have 15 s timeouts |
| M5 | Corrupt gas ledger (killed mid-write) crash-looped the whole API. | atomic write (tmp + rename), tolerant load, pruned maps |
| M6 | A file pruned while streamed crashed the indexer (and a restart wipes live rooms). | stream error handler; oversized bodies settle |
| M7 | Sync reads of up to 200 `summary.json` on every `/matches` / `/health` (polled by every landing + /live tab). | 5 s directory cache + per-file cache by mtime |
| M8 | /live: World socket leaked when leaving during load; chain chip rebuilt every frame (links unclickable); 14 rollup reads every 1.5 s even in background tabs; setup errors left a spinner forever; a confirmation timeout was shown as "refused" (users bet twice). | socket only if still on the page; chip ≤ 2/s; poll 2.5 s visible / 10 s hidden; **Retry** button; "sent, not confirmed yet" + 6 s hold; gs3 no longer shown as "arena 0" |
| M9 | /play: no WebGL 2 check (21 MB download that cannot start), no loader `onerror`. | WebGL 2 check with a /live fallback link; loader error + Reload; phone note |
| M10 | /replay of an aborted match: 404 + "no World recording"; unhandled rejection left "loading…". | clear "aborted (refunded)" / "no on-chain Final" messages; catch-all |
| M11 | /season stopped polling after one failed fetch. | keeps retrying every 10 s |
| L1 | Server / RPC / error text went into `innerHTML` (live, me, receipt, replay). | `esc()` everywhere such text is shown |
| L2 | Watchdog did not cover gs3. | gs3 added (heartbeat check) |
| L3 | `RxResend` could be spammed. | 1 per client per second |
| L4 | Faucet-gate refusal showed a silent "0 RUSH chips". | the reason is shown |

## 4. Load test — 20 people at once on production

**Run 1** (`load/run1/`, 2026-11-24 18:05–18:22 UTC, `scripts/beta-load.sh run1 20 900 10 3`): 20 headless game
clients (the real client code path with the match bot steering; each one asks the live matchmaker `/api/mm` like the
Quick Match button and connects where it is sent; 3 s apart) + 10 headless spectator bettors (the game's chain bundle:
open bets, sealed Champion bets in the TEE, Chaos Drops) for 15 min. VPS sampled every 15 s.

| Metric | Result |
|---|---|
| Players connected | **19 / 20** (p9: a client-side connect timeout in the test harness while this laptop launched 20 Unity processes; the server never saw it) |
| Routing | 3 matches at once: gs1 4 players + 2 watching, gs2 2 + 4 watching, gs3 (overflow, Classic) 5 + 2 watching — nobody refused, nobody "Server full" |
| Game-server tick | steady **30 Hz**; tick work p95 median **0.7 ms**, worst 10-s window 4.5 ms (budget 33 ms → ≥ 7× headroom) |
| Game-server resources | 5–9 % CPU, 112 → 131 MB each (cap 1 GB); host load avg peak 1.55 on 2 vCPU |
| Snapshots to clients | 15.0 Hz on every client, **0 bad snapshots** |
| RTT (via SSH tunnel Mac → Mumbai) | p50 ≈ 102 ms; per-10 s p95 median 300 ms. The tail (8.9 % of windows > 1 s) is the harness: 20 Unity clients on one 8 GB laptop dropped to 10 fps (1 % low); the server side shows no stalls |
| Matches | gs1 + gs2 first matches **completed with the Final on-chain (real-time 1.0)**; gs3 Classic match completed; the second matches aborted cleanly (keeper abort + refunds) when the clients died at 18:16 (below) |
| Spectator bettors | setup 10/10; 1,156 state polls 0 fail; **sealed bets 20/20** (p50 1.7 s, p95 6.8 s); open bets 23/24, drops 5/7 (the failures are the program's own window / cooldown rules); **1× RPC 429** in 15 min |
| Neighbours | finity 200 throughout (0.3–1.2 s), Ovelo healthy |

Found by run 1 and fixed (commit c413a91, server build pending, §7):
- **Watchers were never seated** when the next lobby already had humans (LobbyOpen → Countdown on the same tick):
  late arrivals would watch match after match. → watchers are seated during the countdown too.
- **Fixed 20 s countdown from the first arrival** split a trickling crowd (gs2 locked with 2 players while 3 more
  were on their way). → each arrival keeps ≥ 8 s on the clock, capped at 45 s.
- **Tier-first matchmaking** sent a "Champion" to an empty server while a lobby with 4 people was open. → populated
  lobbies first (deployed 18:20 UTC).

Harness note: at 18:16 all 20 client processes quit together (graceful shutdown, in launch order) while another
session started a Unity batch job on this laptop; the server logged ordinary disconnects + the abort path. Not a
server fault, but it cut run 1's second matches short.

**Run 2 — after the fixes** (`load/run2/`, 18:40–18:53 UTC, on web #15 + gameserver built from c413a91 + 575ea17
(OC-56 slope landing, the other session's), `scripts/beta-load.sh run2 20 660 5 3`):

| Metric | Run 1 (before) | **Run 2 (after)** |
|---|---|---|
| Players connected / bad snapshots | 19 / 20 · 0 | **20 / 20 · 0** |
| Where the 20 went | gs1 4 + 2 watching · gs2 2 + 4 watching · gs3 5 + 2 watching | **gs1 12 (full) + 2 watching · gs2 6** — players 1–14 all sent to the populated lobby, the countdown held until it filled |
| Connections on one server | ≤ 7 | **14** (12 players + 2 watchers; the old cap of 12 would have refused 2) |
| Tick work p95 (gs1, 14 clients) | 0.7 ms median | 0.74–0.86 ms median, worst window 11 ms (budget 33 ms) |
| RTT p95 per 10 s window | median 300 ms, p90 913 ms | **median 184 ms, p90 233 ms** |
| Matches | 2 arena Finals on-chain + gs3 Classic | **2 arena Finals on-chain (real-time 1.0)**; the second matches aborted + refunded when the clients reached their scheduled end |
| Spectator bettors (5) | 1 × 429 | sealed bets 10/10 (p95 3.3 s), bets 14/14, drops 2/2, state 468/468, **0 × 429** |
| Host | load ≤ 1.55 | load ≤ 0.92; gs1 7 % CPU / 144 MB; finity 200, Ovelo healthy |

Edge found in run 2 (accepted, §5): when all 12 players of a server stay for the next match, that server's watchers
keep watching (the seats never free up) while another server has room. Real players leave between matches; a watcher
can press Quick Match again. Proper fix (send watchers to an open server) needs a client change → after the beta.


## 5. Known limits — accepted for the beta (not fixed)

| Area | Limit | Why not now / mitigation |
|---|---|---|
| Match size | **12 per match** (protocol, 4 maps × 12 spawns, on-chain program). 20 people = 2–3 matches at once. | By decision (user, 2026-11-24). Capacity: 2 arenas × 12 + gs3 × 12 = 36 seats + 4 watchers per server. |
| RPC | Browsers call the public devnet RPC and `devnet-as.magicblock.app` directly; one /live tab ≈ 14 rollup reads per poll. | Poll slowed (2.5 s / 10 s hidden); run 1 saw one 429 with 10 bettors. **Recommended before a bigger beta:** a keyed devnet RPC (or a `/rpc` proxy) and `getMultipleAccounts` for the economy state. |
| Devnet services | MagicBlock devnet-as, the TEE and Solana devnet are outside our control. | Keeper retries/backoff; Final timeout → abort + refunds (H3); sealed bets fall back to commit-reveal. |
| Unity re-init | The WebGL client calls `init()` on the shared chain bundle again; pending confirmations of the page can hang (rare). | Page-side init is now shared (no double init from the page); the bundle fix needs a bundle rebuild → after the beta. |
| AFK players | A connected but idle (hidden tab) player who misses the 10 s map load is "Late" and still advances for a round or two. | Gameplay rule; left as is. |
| Private room host | A host whose tab is hidden stays "connected" up to 90 s; nobody else can press Start. | Rare; recreate the room. |
| Indexer restart | Wipes live private rooms (they become public). | Restart is rare (watchdog after 3 failed checks); rooms are recreated in seconds. |
| Mobile | /play on phones is experimental (21 MB WebGL, 2 GB heap; iOS Safari unverified). | /play now says so; /live works on phones. |
| Session keys | Each new session key carries a 0.002 SOL top-up that is lost on rotation. | Daily re-gas covers it (H1); the funder has ≈ 75 new-guest top-ups. |
| Watchers on a full server | If all 12 players stay for the next match, that server's watchers keep watching (run 2). | Leave + Quick Match again; fix after the beta (client asks the matchmaker for an open server). |
| Tests | The load clients run the real client code but are bots; real human hands, phones and Wi-Fi were not part of this audit. | The user's playtest (gate 1) covers this. |

## 6. Before sending the beta invites — checklist

1. **Link:** https://fallrush.fun/play (desktop Chrome / Edge / Firefox / Safari 15+). Friends who want one match
   together: **Create room** → share the 6-character code / invite link (rooms use gs3 first, so public arenas stay open).
2. **Balances** (devnet): funder ≥ 0.3 SOL, game authority ≥ 0.5 SOL — check `https://fallrush.fun/api/health`
   (`funderLamports`) and the authority in an explorer; top up from admin (≥ 1.0 floor). Today: funder 0.90,
   authority 2.03, admin 1.51.
3. **Health during the session:** `bash scripts/p5-health.sh`; the watchdog restarts a failed fallrush-* service
   after 3 checks (gs1/gs2/gs3, keepers, indexer, web).
4. **Kill switches** (indexer env): `ROOMS_ON=0`, `MM_ON=0`.
5. **Rollback:** game servers → image `fallrush-gameserver:p7ux-prev`; web → `/opt/fallrush/web.p6bak`; landing →
   `/opt/fallrush/web.site-prebeta`; nginx → `/opt/fallrush/fallrush-p5.conf.beta-bak`; whole VPS → Hostinger
   snapshot (re-taken 2026-11-24 18:35 UTC, expires 25 Nov 18:35).
6. Optional: rotate the front-Worker shared key (`FRONT_KEY`) — it was printed into an audit session's tool output
   (never committed).

## 7. What changed, where, and how it shipped

- Commit **c413a91** (branch p7): all fixes above (indexer, keeper, watchdog, nginx template, game server, web app,
  landing) + 4 EditMode tests; `scripts/beta-load.sh` (the 20-player harness).
- Deployed by this audit 17:50–18:20 UTC: VPS snapshot → game servers gs1/gs2/gs3 → nginx `/api/` limit → node image
  (indexer, keepers) → web app + landing → watchdog; neighbours (finity, Ovelo) checked before and after every step.
- The last server fixes (watchers seated during the countdown, countdown extension) shipped in the other session's
  18:37 UTC deploy (web #15 + gameserver from c413a91 + its OC-56 slope fix; EditMode 469 / 468 passed / 0 failed).
- Devnet SOL: funder +0.50, authority +0.80 from admin; load bettors swept back (0.166 + 0.083). Ledger in
  `docs/proof/p7/sol.md`.

## 8. Evidence

- `pages/` (before: landing crash, replay 404) and `pages-after/` (20 page loads, 0 console errors) — clean headless
  Chrome against https://fallrush.fun at 390 px and 1440 px.
- `load/run1/`, `load/run2/`: `mm.txt` (each player's matchmaker answer), `vps.txt` (server status + docker stats every
  15 s), `bettors.out` + `bettors/events.jsonl`, `matches.json`, `players-logs.tgz` (the 20 client logs).
- `docs/proof/p7/vps/deploy.log` (every deploy step), `docs/proof/p7/builds.txt` (tests + builds).




