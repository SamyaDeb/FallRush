// /play: the Unity WebGL build (Brotli) full screen. Meanwhile, silently: a guest burner, gas, session key, chips
// (faucet on the first visit), and Quick Play = the fullest open game server (/api/status). No popups.
import { api } from '../lib/net';
import { ensureGuest, wallet, chainFor } from '../lib/chain';
declare const createUnityInstance: any;
export function play(root: HTMLElement) {
  const t0 = performance.now();
  document.getElementById('top')!.style.display = 'none';
  root.innerHTML = `<div id="unity-wrap"><canvas id="unity-canvas" tabindex="-1"></canvas>
    <div id="unity-load"><div style="font:44px 'Lilita One';-webkit-text-stroke:6px #7B2FF7;paint-order:stroke fill">FALL <span style="color:#FFD21F">RUSH</span></div>
    <div class="bar"><i id="bar"></i></div><div id="msg" class="muted" style="color:#ddd">loading the game…</div><div id="guest" class="muted" style="color:#bbb"></div></div></div>`;
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
    const s = document.createElement('script');
    s.src = '/unity/Build/WebGL.loader.js';
    s.onload = () => createUnityInstance(root.querySelector('#unity-canvas'), {
      dataUrl: '/unity/Build/WebGL.data.br', frameworkUrl: '/unity/Build/WebGL.framework.js.br', codeUrl: '/unity/Build/WebGL.wasm.br',
      streamingAssetsUrl: '/unity/StreamingAssets', companyName: 'Fall Rush', productName: 'Fall Rush', productVersion: '0.5',
    }, (p: number) => { (root.querySelector('#bar') as HTMLElement).style.width = Math.round(p * 100) + '%'; })
      .then((inst: any) => { mark('unity'); (window as any).__fr.unity = inst; (root.querySelector('#unity-load') as HTMLElement).style.display = 'none'; })
      .catch((e: any) => { (root.querySelector('#msg') as HTMLElement).textContent = 'could not start the game: ' + e; });
    document.body.appendChild(s);
  })();
  return () => { document.getElementById('top')!.style.display = ''; location.reload(); };
}
