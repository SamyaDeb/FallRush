# Fall Rush waitlist worker

Lets the join page give each player a **one-click X post**: the caption and their link are filled in, and X shows **their own 16:9 poster** (Rushling + place in line) as a large image card under the post.

```
Join page ──POST /api/join──▶ worker ──▶ D1 (place in line, invite codes, referrals)
          ──PUT  /api/poster/CODE (PNG)──▶ R2 (posters/CODE.png)
X post: "I just joined … your-site.com/r/CODE"
X crawler ──GET /r/CODE──▶ page with twitter:card = summary_large_image + og:image = /p/CODE.png
People who click ──/r/CODE──▶ your-site.com/?ref=CODE  (that friend moves up 25 spots when they join)
```

## Set up (about 10 minutes)

```bash
cd web/waitlist-worker
npm i -g wrangler            # or: npx wrangler …
wrangler login
wrangler d1 create fallrush-waitlist          # copy the database_id into wrangler.toml
wrangler d1 execute fallrush-waitlist --remote --file=schema.sql
wrangler r2 bucket create fallrush-posters
# edit wrangler.toml: SITE_URL, ALLOWED_ORIGIN, routes for your domain
wrangler deploy
```

Put the landing page and the worker on **the same domain** (the worker's routes take `/api/*`, `/r/*` and `/p/*`), so share links look like `your-site.com/r/BOLT-7K2Q`.

Also add `og-default.png` (1600 × 900) at the site root: it's the card for anyone whose poster hasn't uploaded yet.

## Hook up the join page

On the Join artboard, set the two settings in **Tweaks → Waitlist**:

- `siteUrl` = `your-site.com`
- `apiBase` = `https://your-site.com` (leave empty to stay in demo mode, which only remembers the player in their browser)

The page then: joins via the API (real place in line), draws the 16:9 poster with that number, uploads it, and uses `your-site.com/r/CODE` in the X post. A visitor who arrives with `?ref=CODE` passes it on when they join.

## Check the card

After deploying, paste a `/r/CODE` link into a draft post on X: the poster should appear under it. X caches cards, so a changed poster can take a while to refresh. Make sure `robots.txt` doesn't block `Twitterbot`.

X shows large cards a little wider than 16:9 and puts your domain in the bottom-left corner, so the poster keeps its important parts away from the top and bottom edges and that corner.

## Before launch

- **Bots:** add a Cloudflare rate-limiting rule on `POST /api/join` (for example 5 per minute per IP) and consider Cloudflare Turnstile on the form.
- **Email:** add a confirmation email (Resend, Postmark…) if you want verified addresses; addresses are never returned by the API.
- **Privacy:** add a short privacy note under the form (what you store: email, chosen Rushling, invite code).
