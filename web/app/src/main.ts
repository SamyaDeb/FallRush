import './style.css';
import { home } from './pages/home';
import { play } from './pages/play';
import { live } from './pages/live';
import { replay } from './pages/replay';
import { receipt } from './pages/receipt';
import { me } from './pages/me';
import { season } from './pages/season';

export type Page = (root: HTMLElement, params: string[]) => (() => void) | void;
const routes: [RegExp, Page][] = [
  [/^\/$/, home], [/^\/play\/?$/, play], [/^\/live\/?(\d+)?\/?$/, live],
  [/^\/replay\/([1-9A-HJ-NP-Za-km-z]{32,44})\/?$/, replay], [/^\/receipt\/([1-9A-HJ-NP-Za-km-z]{32,44})\/?$/, receipt], [/^\/me\/?$/, me], [/^\/season\/?(\d+)?\/?$/, season],
];
let cleanup: (() => void) | void;
export function go(path: string) { history.pushState({}, '', path); render(); }
function render() {
  if (cleanup) { try { cleanup(); } catch { /* ignore */ } cleanup = undefined; }
  const root = document.getElementById('app')!;
  root.innerHTML = '';
  const p = location.pathname;
  for (const [re, page] of routes) {
    const m = p.match(re);
    if (m) { cleanup = page(root, m.slice(1)); return; }
  }
  root.innerHTML = '<div class="card"><h2>Not found</h2><a href="/" data-link>Home</a></div>';
}
document.addEventListener('click', (e) => {
  const a = (e.target as HTMLElement).closest('a[data-link]') as HTMLAnchorElement | null;
  if (a && a.origin === location.origin && !e.metaKey && !e.ctrlKey) { e.preventDefault(); go(a.pathname + a.search); }
});
window.addEventListener('popstate', render);
render();
