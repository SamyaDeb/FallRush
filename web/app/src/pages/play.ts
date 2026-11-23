// /play: the Unity WebGL build (Brotli) full screen. Meanwhile, silently: a guest burner, gas, session key, chips
// (faucet on the first visit), and Quick Play = the fullest open game server (/api/status). No popups.
import { api } from '../lib/net';
import { ensureGuest, wallet, chainFor } from '../lib/chain';
declare const createUnityInstance: any;
export function play(root: HTMLElement) {
  const t0 = performance.now();
  document.getElementById('top')!.style.display = 'none';
  document.getElementById('foot')!.style.display = 'none';
  // P7: the capsule loader (the bottom half fills with the real download progress; the size is the build's)
  root.innerHTML = `<div id="unity-wrap"><canvas id="unity-canvas" tabindex="-1"></canvas>
    <div id="unity-load"><div class="word">FALL <span class="yellow">RUSH</span></div>
    <div class="capsule" id="cap"><div class="top"><span class="shine"></span></div><div class="bot"><i></i></div></div>
    <div class="loadcard"><div class="pct" id="pct">0%</div><div id="msg">loading the game…</div><div id="guest" class="muted"></div></div></div></div>`;
  const msg = (s: string) => { (root.querySelector('#guest') as HTMLElement).textContent = s; };
  (window as any).__fr = { t0, marks: {} as Record<string, number> };
  const mark = (k: string) => { (window as any).__fr.marks[k] = Math.round(performance.now() - t0); };
  // guest wallet in parallel with the download (Unity's own bundle instance reuses it: same localStorage)
  const guest = chainFor().then(() => ensureGuest(msg)).then((g) => { mark('guest'); msg(g.error ? `guest wallet: ${g.error} (you can still play)` : `guest wallet ready · ${g.chips ? Number(BigInt(g.chips) / 1000000n) + ' RUSH chips' : ''}`); return g; }).catch((e) => { msg('guest wallet: ' + e); });
  (async () => {
    // Quick Play: the fullest open server; else any running one (you watch, then join the next lobby)
    let server = new URLSearchParams(location.search).get('server');
    if (!server) {
      try {
        const s = await api('/status');
        const up = (s.servers ?? []).filter((x: any) => x.up && x.url);
        const open = up.filter((x: any) => !x.inMatch && x.humans < x.size).sort((a: any, b: any) => b.humans - a.humans);
        server = (open[0] ?? up.sort((a: any, b: any) => a.humans - b.humans)[0])?.url ?? null;
      } catch { /* fall through */ }
    }
    mark('quickplay');
    const q = new URLSearchParams(location.search);
    if (server) q.set('server', server);
    q.set('quickplay', '1');
    try { const eq = JSON.parse(localStorage.getItem('fallrush.equip') ?? 'null'); const w = wallet(); if (eq?.asset && eq.wallet && (!w || w.burner === eq.wallet)) { q.set('shell', eq.asset); q.set('wallet', eq.wallet); } } catch { /* none */ }
    history.replaceState({}, '', '/play?' + q.toString());
    await chainFor().catch(() => {}); // the burner exists before Unity's own bundle instance starts (one burner); the rest of the guest setup runs in parallel
    // the build files (scripts/p5-deploy.sh writes build.json); the two big ones come in 4 parallel Range requests
    // (one TCP stream measured 0.1–0.5 MB/s from the VPS, 4 streams ~2.2 MB/s) as plain bytes; Unity's loader
    // decodes the Brotli itself (Decompression Fallback, doc 15 OC-14)
    const bj = await fetch('/unity/Build/build.json').then((r) => r.json()).catch(() => ({ loader: 'WebGL.loader.js', data: 'WebGL.data.br', framework: 'WebGL.framework.js.br', code: 'WebGL.wasm.br', parallel: false }));
    const got: Record<string, number> = {}, tot: Record<string, number> = {};
    const cap = root.querySelector('#cap') as HTMLElement, pct = root.querySelector('#pct') as HTMLElement, msgEl = root.querySelector('#msg') as HTMLElement;
    const MB = (n: number) => (n / 1048576).toFixed(1);
    const show = (p: number) => { const v = Math.max(0, Math.min(1, p)); cap.style.setProperty('--p', v.toFixed(3)); pct.textContent = Math.round(v * 100) + '%'; };
    const prog = () => { const a = Object.values(got).reduce((x, y) => x + y, 0), b = Object.values(tot).reduce((x, y) => x + y, 0); if (b) { show((0.9 * a) / b); msgEl.textContent = `downloading ${MB(a)} / ${MB(b)} MB`; } };
    const pfetch = async (name: string, parts = 5): Promise<string> => {
      const url = '/unity/Build/' + name;
      const n = Number((await fetch(url, { method: 'HEAD' })).headers.get('content-length') ?? 0);
      if (!n || !bj.parallel) return url;
      tot[name] = n; got[name] = 0;
      const size = Math.ceil(n / parts), bufs: Uint8Array[] = [];
      // one range; a stall (no bytes for 6 s) aborts and resumes from the bytes already received (up to 8 tries)
      const range = async (i: number) => {
        const a = i * size, b = Math.min(n, a + size) - 1, out = new Uint8Array(b - a + 1);
        let o = 0;
        for (let t = 0; o < out.length; t++) {
          if (t >= 8) throw new Error('range stalled');
          const ac = new AbortController();
          let last = Date.now();
          const wd = setInterval(() => { if (Date.now() - last > 6000) ac.abort(); }, 1000);
          try {
            const r = await fetch(url, { headers: { Range: `bytes=${a + o}-${b}` }, signal: ac.signal });
            if (r.status !== 206 || !r.body) throw new Error('range ' + r.status);
            const rd = r.body.getReader();
            for (;;) { const { done, value } = await rd.read(); if (done) break; out.set(value, o); o += value.length; got[name] += value.length; last = Date.now(); prog(); }
          } catch { (window as any).__fr.retries = ((window as any).__fr.retries ?? 0) + 1; }
          finally { clearInterval(wd); }
        }
        bufs[i] = out;
      };
      await Promise.all(Array.from({ length: parts }, (_, i) => range(i)));
      return URL.createObjectURL(new Blob(bufs as BlobPart[], { type: 'application/octet-stream' }));
    };
    const [dataUrl, codeUrl] = await Promise.all([pfetch(bj.data).catch(() => '/unity/Build/' + bj.data), pfetch(bj.code).catch(() => '/unity/Build/' + bj.code)]);
    mark('download');
    msgEl.textContent = 'unpacking…';
    const s = document.createElement('script');
    s.src = '/unity/Build/' + bj.loader;
    s.onload = () => createUnityInstance(root.querySelector('#unity-canvas'), {
      dataUrl, frameworkUrl: '/unity/Build/' + bj.framework, codeUrl,
      streamingAssetsUrl: '/unity/StreamingAssets', companyName: 'Fall Rush', productName: 'Fall Rush', productVersion: '0.5',
    }, (p: number) => { show(0.9 + p * 0.1); if (p > 0.05) msgEl.textContent = 'starting the game…'; })
      .then((inst: any) => { mark('unity'); (window as any).__fr.unity = inst; cap.classList.add('open'); show(1); setTimeout(() => { (root.querySelector('#unity-load') as HTMLElement).style.display = 'none'; }, 450); })
      .catch((e: any) => { msgEl.textContent = 'could not start the game: ' + e; });
    document.body.appendChild(s);
  })();
  return () => { document.getElementById('top')!.style.display = ''; document.getElementById('foot')!.style.display = ''; location.reload(); };
}
