/** Waitlist API client (web/waitlist-worker) + local state. The email is never stored client-side. */

const env = import.meta.env;
export const API_BASE = String(env.VITE_API_BASE ?? '').replace(/\/$/, '');
export const SITE_URL = String(env.VITE_SITE_URL || (typeof location !== 'undefined' ? location.origin : 'https://your-site.com')).replace(/\/$/, '');
/** "your-site.com" (for the poster / UI). */
export const SITE_HOST = SITE_URL.replace(/^https?:\/\//, '');

const CODE_RE = /^[A-Z]{3,10}-[A-HJ-NP-Z2-9]{4}$/;
const REF_KEY = 'fallrush-ref';
const SAVE_KEY = 'fallrush-waitlist';

export interface Spot {
  code: string;
  rushling: string;
  rank: number;
  total: number;
  referrals?: number;
  shareUrl: string;
  posterUrl: string;
  hasPoster: boolean;
  /** Poster upload token (only returned on the first join). */
  token?: string;
  /** Rank printed on the uploaded poster (re-made when the player climbs). */
  posterRank?: number;
}

// ---------- ?ref=CODE (kept in sessionStorage until join) ----------

export function captureRef(search: string) {
  const raw = new URLSearchParams(search).get('ref');
  if (!raw) return;
  const code = raw.trim().toUpperCase();
  if (!CODE_RE.test(code)) return;
  try {
    sessionStorage.setItem(REF_KEY, code);
  } catch {
    /* private mode */
  }
}

export function getRef(): string | null {
  try {
    return sessionStorage.getItem(REF_KEY);
  } catch {
    return null;
  }
}

function clearRef() {
  try {
    sessionStorage.removeItem(REF_KEY);
  } catch {
    /* ignore */
  }
}

// ---------- saved spot (localStorage) ----------

export function loadSpot(): Spot | null {
  try {
    const s = JSON.parse(localStorage.getItem(SAVE_KEY) || 'null');
    return s && typeof s.code === 'string' && CODE_RE.test(s.code) ? (s as Spot) : null;
  } catch {
    return null;
  }
}

export function saveSpot(s: Spot) {
  try {
    localStorage.setItem(SAVE_KEY, JSON.stringify(s));
  } catch {
    /* ignore */
  }
}

export function forgetSpot() {
  try {
    localStorage.removeItem(SAVE_KEY);
  } catch {
    /* ignore */
  }
}

// ---------- API ----------

export class ApiError extends Error {
  constructor(public code: string, message: string) {
    super(message);
  }
}

const MESSAGES: Record<string, string> = {
  invalid_email: 'That email doesn’t look right. Check it and try again.',
  invalid_rushling: 'Pick a Rushling first.',
  forbidden: 'This page isn’t allowed to join from here (origin check).',
  try_again: 'The line is busy. Try again in a moment.',
  server_error: 'Something went wrong on our side. Try again in a moment.',
  network: 'Couldn’t reach the waitlist. Check your connection and try again.',
  rate_limited: 'Too many tries. Wait a minute and try again.',
  bot_check_failed: 'We couldn’t confirm you’re human. Wait a moment and try again.',
};

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(API_BASE + path, init);
  } catch {
    throw new ApiError('network', MESSAGES.network);
  }
  let body: Record<string, unknown> = {};
  try {
    body = await res.json();
  } catch {
    /* non-JSON (e.g. a rate-limit page) */
  }
  if (!res.ok) {
    const code = res.status === 429 ? 'rate_limited' : String(body.error || 'server_error');
    throw new ApiError(code, MESSAGES[code] || MESSAGES.server_error);
  }
  return body as T;
}

export async function joinWaitlist(email: string, rushling: string, turnstileToken?: string | null): Promise<Spot> {
  const ref = getRef();
  const spot = await call<Spot>('/api/join', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, rushling, ref, turnstileToken: turnstileToken || undefined }),
  });
  clearRef();
  // An existing signup comes back without a token: keep the one we may already have.
  const prev = loadSpot();
  const merged: Spot = { ...spot, token: spot.token ?? (prev?.code === spot.code ? prev.token : undefined) };
  saveSpot(merged);
  return merged;
}

export async function uploadPoster(spot: Spot, png: Blob): Promise<void> {
  if (!spot.token) throw new ApiError('unauthorized', 'This browser can’t update that poster.');
  await call<{ ok: boolean }>(`/api/poster/${encodeURIComponent(spot.code)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'image/png', Authorization: `Bearer ${spot.token}` },
    body: png,
  });
  saveSpot({ ...spot, hasPoster: true });
}

/** Refresh rank / total / referrals for the saved spot. */
export async function refreshSpot(spot: Spot): Promise<Spot> {
  const me = await call<Spot>(`/api/me/${encodeURIComponent(spot.code)}`);
  const next = { ...spot, ...me, token: spot.token };
  saveSpot(next);
  return next;
}

export const fmt = (n: number) => n.toLocaleString('en-US');

/** Post text for the share step (the /r/CODE link is passed separately, so X shows the poster card). */
/** The official X account, tagged in every shared post. */
export const X_HANDLE = 'fallrushdotfun';

export const shareCaption = (name: string, rank: number) =>
  `Just joined the @${X_HANDLE} waitlist as ${name} 🏁\n#${fmt(rank)} in line for the crown 👑\n\nPick your Rushling and race me 👇 #FallRush`;

export const xIntentUrl = (text: string, url: string) =>
  `https://x.com/intent/post?text=${encodeURIComponent(text)}&url=${encodeURIComponent(url)}`;
