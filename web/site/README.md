# Fall Rush landing site

The public site: the final 3D hero (from the "Fall Rush — 3D Hero" design) with the waitlist live, now also the front
door of the game URL (`/` on the VPS; the game app keeps `/play`, `/live`, `/replay`, `/receipt`, `/me`, `/arenas`).

**The landing (2026-11-23):** capsule loader (the screen cracks open like a capsule toy) → hero race (click a Rushling
to bonk it; **▶ Play the beta** and **Join waitlist** are separate doors) → a 1000vh **scroll story** on one sticky canvas
(`three/StoryCanvas.tsx`, chapters in `three/story/chapters.ts`, shared timeline in `three/story/timeline.ts`): the sky
drop → Capsule Run 12→8 → Goo Tower 8→5 → Star Fountain 5→3 → the on-chain Final, Lid Flip 3→1 (ER / VRF / session keys
/ Metaplex cards, live Final tick) → the crown; GSAP ScrollTrigger scrubs the copy (`pages/Story.tsx`) → the **Rushling
pit** (Rapier physics toy box, `three/PitScene.tsx`, loaded only near it) → finale with live arenas + recent matches
(`/api/status`, `/api/matches` via `lib/live.ts`). Everything story-telling is a pure function of scroll progress, so
scrolling up rewinds it. Props are the game's own kit as .glb (`tools/blender/props.py --glb web/site/public/models`);
sound is the game's synthesised SFX + race loop (`public/sfx`, off until the visitor turns it on). Reduced motion: no
loader, fades instead of pops, a calmer camera; no WebGL: the chapters stack as cards.

**Builds:** the VPS build (`scripts/p5-deploy.sh` web step) goes to `web/app/dist/site` with `assetsDir: site-assets`,
waitlist API on `https://waitlist.fallrush.fun` (worker `ALLOWED_ORIGINS` includes the game URL). The Cloudflare copy:
`bash scripts/landing-deploy-cf.sh` (Play / live data point at the game URL).

**Flow:** hero → **Join waitlist** → `/join`: **1 Pick** (3D turntable + colour carousel, ← → / Enter) →
**2 Join** (email → `POST /api/join`, real place in line; everyone on the turntable jumps) → **3 Poster** (their
Rushling + `#rank`, 1600×900, drawn in the browser and uploaded with `PUT /api/poster/CODE`) → **4 Share**
(**Post on X** with `your-site.com/r/CODE`, so X shows their poster as the card; on phones, **Share poster to X** sends the
PNG through the share sheet; invite link + Copy). **Back to the course** drops their Rushling into the hero race in a
capsule, and the hero then shows their card (rank, progress, poster, share). Returning visitors start at the poster step
on `/join`; the poster is re-made if they climbed. Friends who arrive via `/r/CODE` land on `/?ref=CODE`; the code is
kept in `sessionStorage` until they join, and the inviter moves up 25 spots.

Stack: Vite + React + TypeScript, three.js via `@react-three/fiber`. The 3D bundle and the poster code are lazy-loaded
as soon as the page script runs (in parallel with React), and fades in after its first rendered frame; until then (or
without WebGL) the page shows the sky gradient.

## Run locally

```bash
cd web/site
npm install
cp .env.example .env          # optional in dev
npm run worker:db             # once: create the local D1 table
npm run worker                # terminal 1: waitlist worker on :8787 (local D1 + R2)
npm run dev                   # terminal 2: site on :5173, proxies /api, /r, /p to the worker
```

## Env

| Var | What |
|---|---|
| `VITE_SITE_URL` | Public URL, no trailing slash (e.g. `https://fallrush.gg`). Used for og: tags and the poster footer. |
| `VITE_API_BASE` | Worker base URL. Empty = same origin (recommended: the worker's routes sit on the site's domain). |
| `WORKER_DEV_URL` | Dev only: where Vite proxies `/api`, `/r`, `/p` (default `http://localhost:8787`). |

## Deploy (Cloudflare Pages + the worker on one domain)

1. Deploy the worker first: see `../waitlist-worker/README.md` (D1, R2, `SITE_URL` / `ALLOWED_ORIGIN` = your domain,
   uncomment the `routes` for `/api/*`, `/r/*`, `/p/*`).
2. Pages project: root `web/site`, build `npm run build`, output `dist`, env `VITE_SITE_URL=https://your-domain`.
3. Add the custom domain to Pages. The worker routes win for `/api/*`, `/r/*`, `/p/*`; Pages serves everything else
   (`/`, `/join`, assets). `/join` opens the email form directly.
4. Check: paste `https://your-domain/r/CODE` into an X draft → the poster card appears (X caches cards).

**Before launch:** add a Cloudflare rate-limiting rule on `POST /api/join` (e.g. 5 requests / minute / IP) and
consider Turnstile on the form.

## Files

- `src/pages/Home.tsx` — hero overlay, capsule stage, spot card with poster + share.
- `src/pages/Join.tsx` + `src/three/JoinScene.tsx` — the 4-step join flow over the 3D turntable.
- `src/three/HeroScene.tsx` — the course, capsule reveal, and play mode (the **Play free** button: WASD / arrows / tap move Bolt, Space jumps, Esc or **Exit** returns to the race).
- `src/three/Rushling.tsx` — Rushling mesh + procedural animation, ported from `RushlingParts.cs` / `RushlingVisuals.cs`.
- `src/three/poster.ts` — offscreen render + 2D composite of the 1600×900 poster.
- `src/lib/waitlist.ts` — API client, `?ref=` capture, saved spot (the email is never stored in the browser).
- `public/og-default.png` — generic card (made with the poster generator, `rank: null`).

## Live setup (2026-11-22)

- **URL:** https://waitlist.fallrush.fun — one Cloudflare Worker `fallrush-waitlist` (config: `../waitlist-worker/wrangler.toml`)
  serves the site (static assets from `dist/`) and runs `/api/*`, `/r/*`, `/p/*`.
- **Data:** D1 `fallrush-waitlist` (emails, Rushling, codes, referrals) · R2 `fallrush-posters` (`posters/CODE.png`, public via
  `/p/`; `backups/` private).
- **Protection:** Turnstile (`VITE_TURNSTILE_SITE_KEY` in `.env.production`, `TURNSTILE_SECRET` worker secret) + 5 joins/min/IP.
- **Deploy:** `npm run deploy` (builds the site, deploys the worker + assets).
- **Backups:** daily at 02:00 UTC the worker writes `backups/waitlist-YYYY-MM-DD.json` to R2 (last 30 days kept); D1 also keeps
  30 days of point-in-time restore (`wrangler d1 time-travel`).
- **Export to CSV:** `npm run export:emails` → `FallRush/backups/waitlist-YYYY-MM-DD.csv` (git-ignored, owner-only permissions).
- **Privacy page:** `/privacy`. Set `VITE_CONTACT_EMAIL` in `.env.production` to show a removal-request address.
