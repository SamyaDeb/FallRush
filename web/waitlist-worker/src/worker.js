/**
 * Fall Rush waitlist worker (Cloudflare Workers + D1 + R2).
 *
 *   POST /api/join            {email, rushling, ref?} -> {code, rank, total, token?, shareUrl, posterUrl, hasPoster}
 *   PUT  /api/poster/:code    body: PNG (16:9), header Authorization: Bearer <token>  -> {ok, posterUrl}
 *   GET  /api/me/:code        -> {code, rank, total, rushling, referrals, hasPoster}
 *   GET  /p/:code.png         -> the player's poster
 *   GET  /r/:code             -> share page: X / Open Graph card with the poster, then sends people to the waitlist
 *
 * Place in line = join order, minus QUEUE_BOOST spots for every friend who joined with your link.
 */

const RUSHLINGS = ['Bolt', 'Splash', 'Sunny', 'Minty', 'Grape'];
const CODE_RE = /^[A-Z]{3,10}-[A-HJ-NP-Z2-9]{4}$/;
const EMAIL_RE = /^[^\s@]{1,64}@[^\s@]{1,190}\.[^\s@]{2,63}$/;
const MAX_POSTER_BYTES = 3 * 1024 * 1024;

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    const path = url.pathname;
    try {
      if (req.method === 'OPTIONS' && path.startsWith('/api/')) return cors(env, req, new Response(null, { status: 204 }));
      if (req.method === 'POST' && path === '/api/join') return cors(env, req, await join(req, env));
      let m;
      if (req.method === 'PUT' && (m = path.match(/^\/api\/poster\/([A-Z0-9-]+)$/))) return cors(env, req, await uploadPoster(req, env, m[1]));
      if (req.method === 'GET' && (m = path.match(/^\/api\/me\/([A-Z0-9-]+)$/))) return cors(env, req, await me(env, m[1]));
      if (req.method === 'GET' && (m = path.match(/^\/p\/([A-Z0-9-]+)\.png$/))) return await poster(env, m[1]);
      if (req.method === 'GET' && (m = path.match(/^\/r\/([A-Za-z0-9-]+)\/?$/))) return await sharePage(env, m[1].toUpperCase());
      return json({ error: 'not_found' }, 404);
    } catch (err) {
      console.error(err);
      return cors(env, req, json({ error: 'server_error' }, 500));
    }
  },

  // Daily backup (cron in wrangler.toml): the whole waitlist as JSON in the private R2 bucket under backups/
  // (the public /p/ route only serves posters/). Keeps the last BACKUP_DAYS days.
  async scheduled(event, env, ctx) {
    ctx.waitUntil(backup(env));
  },
};

async function backup(env) {
  const { results } = await env.DB.prepare('SELECT * FROM waitlist ORDER BY seq').all();
  const day = new Date().toISOString().slice(0, 10);
  await env.POSTERS.put(`backups/waitlist-${day}.json`, JSON.stringify({ exportedAt: new Date().toISOString(), count: results.length, rows: results }), {
    httpMetadata: { contentType: 'application/json' },
  });
  const keep = Number(env.BACKUP_DAYS || 30);
  const cutoff = Date.now() - keep * 86400000;
  const listed = await env.POSTERS.list({ prefix: 'backups/' });
  for (const o of listed.objects) if (o.uploaded.getTime() < cutoff) await env.POSTERS.delete(o.key);
}

// ---------- handlers ----------

async function join(req, env) {
  if (!sameOrigin(req, env)) return json({ error: 'forbidden' }, 403);
  let body;
  try { body = await req.json(); } catch { return json({ error: 'bad_json' }, 400); }
  const email = String(body.email || '').trim().toLowerCase();
  const rushling = RUSHLINGS.find((r) => r.toLowerCase() === String(body.rushling || '').toLowerCase());
  const ref = String(body.ref || '').toUpperCase();
  if (!EMAIL_RE.test(email) || email.length > 254) return json({ error: 'invalid_email' }, 400);
  if (!rushling) return json({ error: 'invalid_rushling' }, 400);

  // Abuse protection (production): per-IP rate limit, then Cloudflare Turnstile.
  const ip = req.headers.get('CF-Connecting-IP') || '';
  if (env.JOIN_LIMITER && ip) {
    const { success } = await env.JOIN_LIMITER.limit({ key: ip });
    if (!success) return json({ error: 'rate_limited' }, 429);
  }
  if (env.TURNSTILE_SECRET && !(await turnstileOk(env.TURNSTILE_SECRET, body.turnstileToken, ip))) {
    return json({ error: 'bot_check_failed' }, 403);
  }

  // Already on the list: return their spot (no new upload token).
  const existing = await env.DB.prepare('SELECT * FROM waitlist WHERE email = ?').bind(email).first();
  if (existing) return json(await publicView(env, existing, null));

  const token = randomHex(32);
  let code, row;
  for (let i = 0; i < 6 && !row; i++) {
    code = rushling.toUpperCase() + '-' + randomCode(4);
    try {
      row = await env.DB.prepare(
        'INSERT INTO waitlist (code, email, rushling, referred_by, token_hash) VALUES (?, ?, ?, ?, ?) RETURNING *'
      ).bind(code, email, rushling, CODE_RE.test(ref) ? ref : null, await sha256(token)).first();
    } catch (e) {
      if (!String(e).includes('UNIQUE')) throw e;
      const again = await env.DB.prepare('SELECT * FROM waitlist WHERE email = ?').bind(email).first();
      if (again) return json(await publicView(env, again, null)); // raced with a duplicate submit
    }
  }
  if (!row) return json({ error: 'try_again' }, 503);

  // Credit the friend who invited them (never yourself).
  if (row.referred_by && row.referred_by !== row.code) {
    await env.DB.prepare('UPDATE waitlist SET referrals = referrals + 1 WHERE code = ?').bind(row.referred_by).run();
  }
  return json(await publicView(env, row, token), 201);
}

async function uploadPoster(req, env, code) {
  if (!sameOrigin(req, env)) return json({ error: 'forbidden' }, 403);
  const auth = req.headers.get('Authorization') || '';
  const token = auth.startsWith('Bearer ') ? auth.slice(7) : '';
  const row = await env.DB.prepare('SELECT code, token_hash FROM waitlist WHERE code = ?').bind(code).first();
  if (!row || !token || (await sha256(token)) !== row.token_hash) return json({ error: 'unauthorized' }, 401);

  const len = Number(req.headers.get('Content-Length') || 0);
  if (len > MAX_POSTER_BYTES) return json({ error: 'too_large' }, 413);
  const buf = await req.arrayBuffer();
  if (buf.byteLength > MAX_POSTER_BYTES) return json({ error: 'too_large' }, 413);
  const b = new Uint8Array(buf);
  const isPng = b.length > 24 && b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47;
  if (!isPng) return json({ error: 'png_only' }, 415);
  const view = new DataView(buf);
  const w = view.getUint32(16), h = view.getUint32(20);
  if (w < 600 || w > 4096 || h < 300 || h > 4096) return json({ error: 'bad_size' }, 422);

  await env.POSTERS.put(`posters/${code}.png`, buf, { httpMetadata: { contentType: 'image/png', cacheControl: 'public, max-age=86400' } });
  await env.DB.prepare('UPDATE waitlist SET has_poster = 1 WHERE code = ?').bind(code).run();
  return json({ ok: true, posterUrl: `${site(env)}/p/${code}.png` });
}

async function me(env, code) {
  const row = await env.DB.prepare('SELECT * FROM waitlist WHERE code = ?').bind(code).first();
  if (!row) return json({ error: 'not_found' }, 404);
  return json(await publicView(env, row, null));
}

async function poster(env, code) {
  const obj = await env.POSTERS.get(`posters/${code}.png`);
  if (!obj) return Response.redirect(`${site(env)}/og-default.png`, 302);
  return new Response(obj.body, { headers: { 'Content-Type': 'image/png', 'Cache-Control': 'public, max-age=86400' } });
}

async function sharePage(env, code) {
  const row = CODE_RE.test(code) ? await env.DB.prepare('SELECT * FROM waitlist WHERE code = ?').bind(code).first() : null;
  if (!row) return Response.redirect(`${site(env)}/`, 302);
  const v = await publicView(env, row, null);
  const landing = `${site(env)}/?ref=${encodeURIComponent(code)}`;
  const title = `${row.rushling} joined the Fall Rush race: #${v.rank.toLocaleString('en-US')} in line`;
  const desc = 'Pick your Rushling and join the waitlist for Fall Rush, the provably fair knockout party game.';
  const img = row.has_poster ? v.posterUrl : `${site(env)}/og-default.png`;
  const e = escapeHtml;
  // Crawlers (X, Discord, Telegram, iMessage…) read the tags; people are sent straight on to the waitlist.
  const html = `<!doctype html>
<html lang="en"><head>
<meta charset="utf-8">
<title>${e(title)}</title>
<meta name="description" content="${e(desc)}">
<meta property="og:type" content="website">
<meta property="og:site_name" content="Fall Rush">
<meta property="og:title" content="${e(title)}">
<meta property="og:description" content="${e(desc)}">
<meta property="og:url" content="${e(`${site(env)}/r/${code}`)}">
<meta property="og:image" content="${e(img)}">
<meta property="og:image:width" content="1600">
<meta property="og:image:height" content="900">
<meta property="og:image:alt" content="${e(`${row.rushling}, number ${v.rank} on the Fall Rush waitlist`)}">
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:title" content="${e(title)}">
<meta name="twitter:description" content="${e(desc)}">
<meta name="twitter:image" content="${e(img)}">
<meta name="twitter:image:alt" content="${e(`${row.rushling}, number ${v.rank} on the Fall Rush waitlist`)}">
<meta http-equiv="refresh" content="0; url=${e(landing)}">
<link rel="canonical" href="${e(landing)}">
</head><body>
<p><a href="${e(landing)}">Join the Fall Rush waitlist</a></p>
<script>location.replace(${JSON.stringify(landing)});</script>
</body></html>`;
  return new Response(html, { headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'public, max-age=300' } });
}

// ---------- helpers ----------

async function turnstileOk(secret, token, ip) {
  if (!token || typeof token !== 'string' || token.length > 2048) return false;
  const form = new FormData();
  form.append('secret', secret);
  form.append('response', token);
  if (ip) form.append('remoteip', ip);
  try {
    const r = await fetch('https://challenges.cloudflare.com/turnstile/v0/siteverify', { method: 'POST', body: form });
    const d = await r.json();
    return !!d.success;
  } catch {
    return false;
  }
}

async function publicView(env, row, token) {
  const boost = Number(env.QUEUE_BOOST || 25);
  const score = row.seq - boost * row.referrals;
  const ahead = await env.DB.prepare(
    'SELECT COUNT(*) AS n FROM waitlist WHERE (seq - ? * referrals) < ? OR ((seq - ? * referrals) = ? AND seq < ?)'
  ).bind(boost, score, boost, score, row.seq).first();
  const total = await env.DB.prepare('SELECT COUNT(*) AS n FROM waitlist').first();
  const out = {
    code: row.code,
    rushling: row.rushling,
    rank: (ahead?.n || 0) + 1,
    total: total?.n || 0,
    referrals: row.referrals,
    hasPoster: !!row.has_poster,
    shareUrl: `${site(env)}/r/${row.code}`,
    posterUrl: `${site(env)}/p/${row.code}.png`,
  };
  if (token) out.token = token;
  return out;
}

function site(env) { return String(env.SITE_URL || '').replace(/\/$/, ''); }

/** Origins allowed to call /api: the site itself, ALLOWED_ORIGIN, and a comma-separated ALLOWED_ORIGINS (e.g. the game
 *  host, which serves the same landing as its front door and calls this worker cross-origin). */
function origins(env) {
  return [site(env), env.ALLOWED_ORIGIN, ...String(env.ALLOWED_ORIGINS || '').split(',')]
    .map((o) => String(o || '').trim().replace(/\/$/, ''))
    .filter(Boolean);
}

function sameOrigin(req, env) {
  const origin = req.headers.get('Origin');
  return !origin || origins(env).includes(origin);
}

function cors(env, req, res) {
  const h = new Headers(res.headers);
  const origin = req.headers.get('Origin');
  const list = origins(env);
  h.set('Access-Control-Allow-Origin', origin && list.includes(origin) ? origin : list[0] || '');
  h.set('Access-Control-Allow-Methods', 'GET, POST, PUT, OPTIONS');
  h.set('Access-Control-Allow-Headers', 'Content-Type, Authorization');
  h.set('Vary', 'Origin');
  return new Response(res.body, { status: res.status, headers: h });
}

function json(data, status = 200) {
  return new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' } });
}

function randomHex(bytes) {
  const a = crypto.getRandomValues(new Uint8Array(bytes));
  return [...a].map((x) => x.toString(16).padStart(2, '0')).join('');
}

function randomCode(n) {
  const abc = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  const a = crypto.getRandomValues(new Uint8Array(n));
  return [...a].map((x) => abc[x % abc.length]).join('');
}

async function sha256(s) {
  const d = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(s));
  return [...new Uint8Array(d)].map((x) => x.toString(16).padStart(2, '0')).join('');
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
