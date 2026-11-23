/**
 * Cloudflare Turnstile (bot check on the join form). Enabled when VITE_TURNSTILE_SITE_KEY is set; the worker verifies
 * the token when its TURNSTILE_SECRET is set. "interaction-only": most visitors never see a challenge.
 */
import { useEffect, useRef, useState } from 'react';

export const TURNSTILE_SITE_KEY = String(import.meta.env.VITE_TURNSTILE_SITE_KEY || '');

type TurnstileApi = {
  render: (el: HTMLElement, opts: Record<string, unknown>) => string;
  reset: (id: string) => void;
  remove: (id: string) => void;
};
declare global {
  interface Window {
    turnstile?: TurnstileApi;
  }
}

let scriptPromise: Promise<void> | null = null;
function loadScript() {
  if (window.turnstile) return Promise.resolve();
  scriptPromise ??= new Promise<void>((resolve, reject) => {
    const s = document.createElement('script');
    s.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
    s.async = true;
    s.onload = () => resolve();
    s.onerror = () => reject(new Error('turnstile failed to load'));
    document.head.appendChild(s);
  });
  return scriptPromise;
}

/** Renders the widget into the returned ref; `token` is null until the check passes. */
export function useTurnstile() {
  const ref = useRef<HTMLDivElement>(null);
  const id = useRef<string | null>(null);
  const [token, setToken] = useState<string | null>(null);

  useEffect(() => {
    if (!TURNSTILE_SITE_KEY) return;
    let dead = false;
    loadScript()
      .then(() => {
        if (dead || !ref.current || !window.turnstile) return;
        id.current = window.turnstile.render(ref.current, {
          sitekey: TURNSTILE_SITE_KEY,
          appearance: 'interaction-only',
          theme: 'light',
          callback: (t: string) => setToken(t),
          'expired-callback': () => setToken(null),
          'error-callback': () => setToken(null),
        });
      })
      .catch(() => undefined);
    return () => {
      dead = true;
      if (id.current && window.turnstile) window.turnstile.remove(id.current);
      id.current = null;
    };
  }, []);

  const reset = () => {
    setToken(null);
    if (id.current && window.turnstile) window.turnstile.reset(id.current);
  };
  return { ref, token, enabled: !!TURNSTILE_SITE_KEY, reset };
}
