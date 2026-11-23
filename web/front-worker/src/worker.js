/**
 * fallrush.fun front: a pass-through to the game host on the VPS (the landing at `/`, the game app, /api, /unity).
 * Cloudflare creates the DNS for the custom domains; the VPS keeps its own TLS. www → apex (301).
 *
 * The VPS rate-limits guest gas and the chips faucet per client IP, so the visitor's IP is forwarded in
 * X-Fallrush-Client-IP together with a shared secret (X-Fallrush-Front); the VPS host nginx only trusts the IP header
 * when the secret matches (infra/prod/fallrush-p5.conf).
 *
 * Config (set at deploy, never committed: the origin holds the VPS address):
 *   var ORIGIN     https://play.<ip-dashed>.sslip.io
 *   secret FRONT_KEY
 * Deploy: bash scripts/front-deploy.sh
 */
export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    if (url.hostname.startsWith('www.')) {
      url.hostname = url.hostname.slice(4);
      return Response.redirect(url.toString(), 301);
    }
    const origin = new URL(env.ORIGIN);
    const target = new URL(url.pathname + url.search, origin);
    const headers = new Headers(req.headers);
    headers.delete('host');
    headers.set('X-Fallrush-Front', env.FRONT_KEY || '');
    headers.set('X-Fallrush-Client-IP', req.headers.get('CF-Connecting-IP') || '');
    headers.set('X-Forwarded-Host', url.hostname);
    headers.set('X-Forwarded-Proto', 'https');
    const res = await fetch(target, {
      method: req.method,
      headers,
      body: req.method === 'GET' || req.method === 'HEAD' ? undefined : req.body,
      redirect: 'manual',
    });
    // keep redirects on this domain
    const loc = res.headers.get('location');
    if (loc && loc.startsWith(origin.origin)) {
      const h = new Headers(res.headers);
      h.set('location', 'https://' + url.hostname + loc.slice(origin.origin.length));
      return new Response(res.body, { status: res.status, headers: h });
    }
    return res;
  },
};
