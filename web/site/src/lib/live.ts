/**
 * Live game data from the indexer (`/api/status`, `/api/matches`). Same origin on the VPS; the Cloudflare build sets
 * VITE_LIVE_API to the game URL (nginx allows CORS on those two GETs). Any failure → `ok: false` and the widgets hide.
 */
import { useEffect, useState } from 'react';

const BASE = String(import.meta.env.VITE_LIVE_API ?? '').replace(/\/$/, '');

export interface ArenaServer {
  id: string;
  up: boolean;
  phase: string;
  inMatch: boolean;
  round: number;
  humans: number;
  arena?: number;
  keeper?: { stage?: string; worldTick?: number; worldPhase?: number; alive?: boolean };
}
export interface MatchRow {
  match: string;
  doneAt: number;
  champion: number | null;
  humanMask: number;
  pools: string[];
  replay?: boolean;
}
export interface Live {
  ok: boolean;
  servers: ArenaServer[];
  humans: number;
  arenasUp: number;
  finalTick: number | null;
  matches: MatchRow[];
}

const EMPTY: Live = { ok: false, servers: [], humans: 0, arenasUp: 0, finalTick: null, matches: [] };

async function get<T>(path: string): Promise<T> {
  const r = await fetch(BASE + '/api' + path, { signal: AbortSignal.timeout(4000) });
  if (!r.ok) throw new Error(String(r.status));
  const ct = r.headers.get('content-type') || '';
  if (!ct.includes('json')) throw new Error('not json'); // dev without an indexer: Vite answers with index.html
  return r.json() as Promise<T>;
}

/** Polls status every `everyMs` (and matches every 4th poll) while `on` is true. */
export function useLive(on = true, everyMs = 5000): Live {
  const [live, setLive] = useState<Live>(EMPTY);
  useEffect(() => {
    if (!on) return;
    let stop = false;
    let n = 0;
    let timer: ReturnType<typeof setTimeout>;
    let matches: MatchRow[] = [];
    const tick = async () => {
      try {
        const s = await get<{ servers?: ArenaServer[] }>('/status');
        if (n % 4 === 0 || matches.length === 0) {
          try {
            matches = (await get<{ matches?: MatchRow[] }>('/matches?n=10')).matches ?? [];
          } catch {
            /* keep the last list */
          }
        }
        const servers = s.servers ?? [];
        const fin = servers.find((x) => x.up && x.keeper?.stage === 'final' && x.keeper.worldTick != null);
        if (!stop)
          setLive({
            ok: true,
            servers,
            humans: servers.reduce((a, x) => a + (x.up ? x.humans || 0 : 0), 0),
            arenasUp: servers.filter((x) => x.up).length,
            finalTick: fin?.keeper?.worldTick ?? null,
            matches,
          });
      } catch {
        if (!stop) setLive(EMPTY);
      }
      n++;
      if (!stop) timer = setTimeout(tick, document.hidden ? everyMs * 3 : everyMs);
    };
    tick();
    return () => {
      stop = true;
      clearTimeout(timer);
    };
  }, [on, everyMs]);
  return live;
}

export const shortKey = (s: string) => (s.length > 10 ? `${s.slice(0, 4)}…${s.slice(-4)}` : s);
