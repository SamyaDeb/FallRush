/**
 * The 3D chunks, shared by React.lazy and the preloader so each downloads once.
 * main.tsx starts the current page's scene immediately (in parallel with React), then warms the rest when idle.
 */
export const loadHeroScene = () => import('../three/HeroScene');
export const loadJoinScene = () => import('../three/JoinScene');
export const loadPosterModule = () => import('../three/poster');

export function preloadFor(path: string) {
  const first = path.startsWith('/join') ? loadJoinScene : loadHeroScene;
  first().catch(() => undefined);
  const rest = () => {
    (path.startsWith('/join') ? loadHeroScene : loadJoinScene)().catch(() => undefined);
    loadPosterModule().catch(() => undefined);
  };
  const ric = (window as Window & { requestIdleCallback?: (cb: () => void, o?: { timeout: number }) => number }).requestIdleCallback;
  if (ric) ric(rest, { timeout: 4000 });
  else setTimeout(rest, 2500);
}
