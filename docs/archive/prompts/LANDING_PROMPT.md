# Prompt: build the Fall Rush landing page + waitlist flow

Copy everything below the line into your terminal agent, run from the repo root (`FallRush/`).

---

You are working in the Fall Rush repo (Unity party game on MagicBlock / Solana). Build the **public landing site** in a new folder `web/site/`, and wire it to the existing waitlist backend in `web/waitlist-worker/` (Cloudflare Worker + D1 + R2; read its README.md and src/worker.js first; don't rewrite it unless something is broken).

## Read first
- `docs/17-ART-DIRECTION.md`: Rushlings (capsule wind-up toys), palette, originality rules (no Fall Guys look-alikes).
- `client-unity/Assets/_Game/Scripts/Client/RushlingParts.cs`, `ProcMesh.cs`, `RushlingVisuals.cs`, `Scripts/Shared/CosmeticPresets.cs` (or wherever `CosmeticPresets` lives): the Rushling mesh is built in code; port it to three.js.
- `docs/01-GDD.md` §1–2 for copy (12 players, 4 rounds, 1 crown; "the provably fair knockout party game"; "No wallet needed").
- `web/waitlist-worker/README.md` for the API.
- Repo root `Fall Rush  Logo.png` = brand reference.

## Stack
- Vite + React + TypeScript, `three` + `@react-three/fiber` + `@react-three/drei`. No UI kit. Fonts: Google Fonts **Lilita One** (display) and **Varela Round** (body), matching the game.
- Deploy target: Cloudflare Pages (static), same domain as the worker. The worker owns `/api/*`, `/r/*`, `/p/*`; the site owns everything else.
- Config via env: `VITE_SITE_URL` (e.g. `https://your-site.com`), `VITE_API_BASE` (empty = same origin). Add `.env.example`.
- Two routes (react-router): `/` (hero) and `/join` (join flow). Read `?ref=CODE` on any page and keep it in `sessionStorage` until join.

## Palette / tokens
Ink `#2A1250`, Magenta `#FF3EA5`, Sun yellow `#FFD21F`, Grape `#7B2FF7`, deep grape `#5F25BD`, mint `#2FE0A8`, sky gradient `#4FA6EC → #66B5F2 → #9CD2F5 → #DDEFFB`. Pastel track tops: `#F9D1E2 #F9EDC2 #D6EDF9 #DED1F9`. Hazard = hot pink `#FF2D8A` + lemon `#FFD43B` stripes.
Chunky toy UI: white cards, 3px ink border, radius 22–26px, hard ink drop-shadow `0 6px 0 #2A1250`, yellow primary buttons, press-down on :active. Logo wordmark "FALL" white / "RUSH" yellow, Lilita One, thick `#7B2FF7` stroke (`paint-order: stroke fill`) with a stacked purple extrusion shadow, rotated −4°, pink underline bar with a little checker flag.

## The Rushling component (`src/three/Rushling.tsx`)
Port from RushlingParts.cs: body height 1.0, radius R 0.31, seam at 0.54. Lower half = lathe in shell colour, upper dome glossy white, ink seam torus, dark rounded LED **screen face** on the front of the dome (canvas texture; expressions: happy `^ ^`, run `• •`, jump `O O`, star `★ ★`, blink, dizzy `x x`; glyph colour = face theme), emblem decal on the lower front (bolt, star, heart, diamond, moon, flame), **wind-up key** on the back that spins, topper (goggles / propeller that spins / chef hat / headphones), two capsule feet, two **noodle arms** (tube along a Catmull-Rom curve, rebuilt per frame, mitten hands). Procedural animation per RushlingVisuals: run gait (bob, waddle roll ±7°, opposite arm swing, feet stepping), idle breathing + blinks, jump (squash → stretch → land squash, arms up), hit/flop (pitch back ~80°, dizzy face, pop back up). MeshStandardMaterial, roughness ~0.3 vinyl look, soft shadows.

Presets (first five are the playable picks):
| Name | Shell | Emblem / colour | Topper | Face | Key |
|---|---|---|---|---|---|
| Bolt | #FF3EA5 | bolt #FFD21F | goggles | #5BF0B5 | #7B2FF7 |
| Splash | #3FA9F5 | star #FFFFFF | propeller | #58E1FF | #FFD21F |
| Sunny | #FFD21F | heart #FF3EA5 | chef hat | #FF7AC6 | #FF3EA5 |
| Minty | #5BE3B5 | diamond #FFFFFF | headphones | #5BF0B5 | #7B2FF7 |
| Grape | #7B2FF7 | moon #FFD21F | goggles | #FFB547 | #FFD21F |

## Page 1: `/` hero (full viewport, 3D)
- Full-screen R3F canvas: a long floating pastel toy-block track (chevrons, padded pink/yellow rims) receding to a mint/white checkered finish arch with a spinning crown; floating pastel islands on pillars with striped spinners and flags; drifting clouds; a swinging striped foam mallet; falling confetti. Fog to `#D4EAF9`. Characters face the camera and run toward it while the **ground scrolls away under their feet** (texture offset + decor moving −z, recycled). Bolt leads (scale 1.45), 5 others behind. Mouse parallax on the camera.
- **Free-roam play, no separate game mode:** WASD/arrow keys move Bolt on the track (clamped), Space jumps, click/tap on the track = run to that point, click a Rushling = it jumps with star eyes + confetti. While the visitor drives, the track scroll eases to a stop, the others wander, the camera follows Bolt softly. Running into a Rushling at speed knocks it over (dizzy, slides away, pops up). After 12 s without input, everyone returns to the race. Ignore keys while focus is in an input.
- Bottom-centre bouncing hint: keycaps `W A S D` "Move Bolt" `Space` "Jump"; on touch devices "Tap the course to move Bolt". Hide once they start playing.
- Overlay (left column, vertically centred, max-width ~540px): chip "12 players · 4 rounds · 1 crown", the big wordmark logo, then a big yellow **Join waitlist →** button linking to `/join`, with a small note "Pick your Rushling, get your spot and a personal race poster." Top bar: small wordmark, nav pills (Rounds, Rushlings, Provably fair, Crown Capsule), pink "Play free" pill.
- If the visitor already joined (localStorage `fallrush-waitlist`), replace the button with a card: their Rushling swatch, name, `#rank`, "N Rushlings ahead of you", progress bar, invite link + Copy + Share, "Get your poster" link. Their Rushling also appears in the 3D race with a bobbing yellow marker.
- Mobile: overlay stacks above the scene, nav links hidden, scene camera pulls back.

## Page 2: `/join` flow (game-style, 4 steps)
Top bar: Back (to `/`), wordmark, step tracker pill `1 Pick · 2 Join · 3 Poster · 4 Share` (done = mint check, current = ink pill).
Background: 3D turntable (lavender disc, checker rim, pillar below, clouds, confetti) with the 5 Rushlings on pads around it facing outward; the selected one rotates to the front, grows to 1.4×, faces the camera, hops with star eyes; a pulsing yellow ring marks the front spot.

1. **Pick:** title "Pick your Rushling" + note "Looks only: every Rushling has the same hitbox, speed and jump." Bottom card: left arrow button · **colour carousel** (5 capsule swatches looping: selected one big with yellow ring and glow, neighbours smaller and `blur(1.6px)` at 70 % opacity, outer ones smaller `blur(3px)` 45 %) · right arrow button. Then name, one-line personality, kit line, big "Choose {Name}" button, hint "← → keys · Enter to choose". Personalities: Bolt "Goggles down, first off the line." Splash "Spins up the propeller and dives into the chaos." Sunny "Chef hat on, sunny screen, zero fear of mallets." Minty "Headphones on. Hears the hazard before it hits." Grape "Moon emblem, night-shift goggles, sneaky fast."
2. **Join:** card "Lock in {Name}", email field (label, validation), "Join the waitlist" button (loading state, error message), "Change Rushling" link. On submit `POST /api/join {email, rushling, ref}` → `{code, rank, total, token, shareUrl, posterUrl, hasPoster}`. Confetti, all Rushlings jump. Save to localStorage (never store the email).
3. **Poster:** generate a **1600×900 PNG** client-side: render the chosen Rushling offscreen (separate WebGLRenderer, `preserveDrawingBuffer`, celebrating pose: arms up, star eyes) and composite on a 2D canvas: sky gradient, sun rays + glow behind the character on the left half, clouds along the bottom, lavender pedestal with yellow ring, confetti; right column: wordmark logo with pink underline, big yellow badge "IN LINE FOR THE CROWN" + `#1,284` (auto-shrink to fit), name plate in the shell colour "BOLT JOINED THE RACE", small text "Pick your Rushling at your-site.com". Keep 70 px clear at top/bottom and the bottom-left corner (X crops large cards a bit and overlays the domain there). Wait for `document.fonts.load` before drawing. Show it in a 16:9 frame with "Download poster" and "Next: post it on X". Upload it: `PUT /api/poster/{code}` with `Authorization: Bearer {token}`, `Content-Type: image/png`.
4. **Share:** preview of the post (caption + poster). Buttons: **Post on X** → `https://x.com/intent/post?text=…&url={shareUrl}` (opens the X app on phones); on devices where `navigator.canShare({files})` is true, also **Share poster to X** via `navigator.share({files:[poster.png], text, url})`. Caption: "I just joined the Fall Rush waitlist as {Name}, #{rank} in line.\nPick your Rushling and race me for the crown #FallRush". Invite link field + Copy. "Every friend who joins from your post moves {Name} up 25 spots." Back to the course link.
If they arrive already joined, start at step 3 with their saved poster (`/p/{code}.png`), or regenerate.

## Backend wiring
- Use `web/waitlist-worker` as-is: deploy instructions are in its README (D1 schema, R2 bucket, routes on the same domain). Add `public/og-default.png` (1600×900 generic poster) to the site; also set site-wide `og:` / `twitter:card=summary_large_image` tags on `/` and `/join` using it.
- `/r/{code}` is served by the worker (X card with the player's poster → redirects to `/?ref=CODE`). Verify by pasting a `/r/` link into an X draft.
- Add a Cloudflare rate-limit rule note for `POST /api/join` in the README, plus a one-line privacy note under the email field.

## Quality bar
- 60 fps on a mid laptop: cap pixel ratio at 2, shadow map 1024, pause rendering when the canvas is off-screen or the tab is hidden, dispose everything on unmount.
- `prefers-reduced-motion`: slow the scene, no confetti bursts, no bobbing hint.
- Accessibility: real buttons/links, labels on inputs, `aria-live` for status messages, visible focus rings, 44 px touch targets, text contrast ≥ 4.5:1.
- Show the key art (`docs` or repo cover image) as a static fallback while three.js loads or if WebGL fails.
- Lighthouse: lazy-load the 3D bundle after first paint; keep the initial JS small.

## Deliverables
1. `web/site/` app with `npm run dev`, `npm run build`, README (env vars, deploy to Cloudflare Pages, how routes split with the worker).
2. Any fixes to `web/waitlist-worker/` you needed, listed in your summary.
3. Run it locally (`wrangler dev` for the worker + Vite proxy `/api`, `/r`, `/p` to it) and walk the whole flow: hero → Join → pick → email → poster uploaded → `/r/CODE` returns the card tags → `/?ref=CODE` credits the referrer. Report what you tested.
