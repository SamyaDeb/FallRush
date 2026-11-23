// The game's own chain bundle (fallrush-chain.js, packages/chain): burner + session keys + every economy write.
// Same origin + same localStorage keys as the Unity build, so /play, /live and /me share one guest wallet.
import { api, ARENAS } from './net';

type Api = Record<string, (...a: any[]) => any>;
let loading: Promise<Api> | null = null;
let inited: string | null = null;
let chain: Api | null = null;
export const J = async (p: Promise<string> | string) => JSON.parse(await p);

function load(): Promise<Api> {
  if (!loading) loading = new Promise((ok, fail) => {
    const s = document.createElement('script');
    s.src = '/chain/fallrush-chain.js';
    s.onload = () => ok((globalThis as any).FallRushChain);
    s.onerror = () => fail(new Error('chain bundle failed to load'));
    document.head.appendChild(s);
  });
  return loading;
}

/** Initialises the bundle with the arena's config (devnet.json = the default arena). */
export async function chainFor(arenaId?: number): Promise<Api> {
  const c = await load();
  const file = arenaId != null && ARENAS[arenaId] ? `devnet-${ARENAS[arenaId].arena}.json` : 'devnet.json';
  if (inited !== file) {
    const cfg = await (await fetch('/chain/' + file)).json();
    const r = await J(c.init(JSON.stringify(cfg)));
    if (!r.ok) throw new Error(r.error);
    inited = file;
  }
  chain = c;
  return c;
}
export const wallet = () => (chain ? JSON.parse(chain.wallet()) : null);

export type Step = (s: string) => void;
/** Guest setup, no popup: gas (a small devnet SOL top-up for a new burner), session key, chips + the day's faucet. */
export async function ensureGuest(step: Step = () => {}, arenaId?: number): Promise<{ burner: string; chips?: string; error?: string }> {
  const c = await chainFor(arenaId);
  const w = JSON.parse(c.wallet());
  const bal = await J(c.devBalances()).catch(() => ({ ok: false }));
  if (!bal.ok || bal.burner < 5_000_000) {
    step('topping up your guest wallet (devnet SOL)…');
    const g = await api('/gas', { burner: w.burner }).catch((e) => ({ ok: false, error: String(e) }));
    if (!g.ok && !(bal.ok && bal.burner > 3_000_000)) return { burner: w.burner, error: g.error ?? 'gas failed' };
  }
  const now = Math.floor(Date.now() / 1000);
  if (!w.session?.token || w.session.validUntil < now + 600) {
    step('creating a session key…');
    const s = await J(c.session('create', '{}'));
    if (!s.ok) return { burner: w.burner, error: 'session: ' + s.error };
  }
  step('chips…');
  const st = await J(c.econ('state', '{}')).catch(() => null);
  const needFaucet = !st?.chips || BigInt(st.chips.faucetTotal ?? 0) === 0n || now - Number(st.chips.faucetAt ?? 0) >= 86_400;
  if (needFaucet) {
    const gate = await api('/faucet-gate', { burner: w.burner }).catch(() => ({ ok: true }));
    const r = await J(c.econ('chips', JSON.stringify({ faucet: gate.ok !== false })));
    if (!r.ok) return { burner: w.burner, error: 'chips: ' + r.error };
    return { burner: w.burner, chips: r.chips?.balance };
  }
  return { burner: w.burner, chips: st?.chips?.balance };
}
