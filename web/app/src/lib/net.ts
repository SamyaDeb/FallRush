// Endpoints + tiny JSON-RPC / websocket readers (reads only; every write goes through the game's chain bundle).
export const ER_HTTP = 'https://devnet-as.magicblock.app';
export const ER_WS = 'wss://devnet-as.magicblock.app';
export const BASE_HTTP = 'https://api.devnet.solana.com';
export const PROGRAM = '4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4';
export const RUSH_MINT = '3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw';
export const CORE = 'CoREENxT6tW1HoK8ypY1SxRMZTcVPm7R94rH4PZNhX7d';
export const ASSETS_AUTH = '7puJtevqtCLvmYEfcBo5zqwiAqX72LqmMT5ATycn2MND';
export const VALIDATOR = 'MAS1Dt9qreoRMQ14YQuhg8UTZMMzDdKhmkZMECCzk57';
export const ARENAS: Record<number, { arena: string; world: string }> = {
  2: { arena: 'HbinPthhN3g5BRQRL4aKQ75HyQXMofhsUbg2c28bJXHt', world: 'HYJRSXpSGZ3HdjmZr4oMy3x6VgTWLcarkgHav7L4hSTQ' },
  3: { arena: 'E9XE7vtQgrWnXLcGMn5FYXAFLb6GmBjrKcZq5wgPcEbG', world: '9Q5KBadvfx3Yoen1uqLUgbU1x6tXn4GmkEkjGqfPuRWn' },
};
export const explorer = (kind: 'tx' | 'address', id: string, rollup = false) =>
  `https://explorer.solana.com/${kind}/${id}?cluster=${rollup ? 'custom&customUrl=' + encodeURIComponent(ER_HTTP) : 'devnet'}`;

let rid = 1;
export async function rpc<T = any>(url: string, method: string, params: unknown[]): Promise<T> {
  for (let i = 0; ; i++) {
    const r = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ jsonrpc: '2.0', id: rid++, method, params }) });
    if (r.status === 429 && i < 4) { await new Promise((ok) => setTimeout(ok, 800 * (i + 1))); continue; }
    const j = await r.json();
    if (j.error) throw new Error(j.error.message ?? JSON.stringify(j.error));
    return j.result as T;
  }
}
export const b64 = (s: string) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
export async function account(url: string, key: string): Promise<{ data: Uint8Array; owner: string; lamports: number } | null> {
  const r = await rpc<any>(url, 'getAccountInfo', [key, { encoding: 'base64', commitment: 'confirmed' }]);
  return r?.value ? { data: b64(r.value.data[0]), owner: r.value.owner, lamports: r.value.lamports } : null;
}
export async function accounts(url: string, keys: string[]): Promise<(Uint8Array | null)[]> {
  const r = await rpc<any>(url, 'getMultipleAccounts', [keys, { encoding: 'base64', commitment: 'processed' }]);
  return r.value.map((v: any) => (v ? b64(v.data[0]) : null));
}

/** accountSubscribe on the rollup, auto-reconnecting; returns a stop function. */
export function subscribe(key: string, onData: (d: Uint8Array) => void, onState?: (s: string) => void): () => void {
  let ws: WebSocket | null = null, stopped = false, retry = 0;
  const open = () => {
    if (stopped) return;
    ws = new WebSocket(ER_WS);
    ws.onopen = () => { retry = 0; onState?.('live'); ws!.send(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'accountSubscribe', params: [key, { encoding: 'base64', commitment: 'processed' }] })); };
    ws.onmessage = (m) => { try { const j = JSON.parse(m.data); const v = j.params?.result?.value; if (v?.data) onData(b64(v.data[0])); } catch { /* ignore */ } };
    ws.onclose = () => { onState?.('reconnecting'); if (!stopped) setTimeout(open, Math.min(5000, 500 * 2 ** retry++)); };
    ws.onerror = () => ws?.close();
  };
  open();
  return () => { stopped = true; ws?.close(); };
}

export async function api<T = any>(path: string, body?: unknown): Promise<T> {
  const r = await fetch('/api' + path, body ? { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) } : {});
  return r.json();
}

// ---- base58 (small, for PDAs and display) ----
const A = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
export function b58(bytes: Uint8Array): string {
  let x = 0n; for (const v of bytes) x = (x << 8n) | BigInt(v);
  let s = ''; while (x > 0n) { s = A[Number(x % 58n)] + s; x /= 58n; }
  for (const v of bytes) { if (v !== 0) break; s = '1' + s; }
  return s;
}
export const short = (s?: string | null) => (s && s.length > 10 ? s.slice(0, 4) + '…' + s.slice(-4) : s ?? '');
export const rush = (base: bigint | string | number) => (Number(BigInt(base)) / 1e6).toLocaleString(undefined, { maximumFractionDigits: 2 });
