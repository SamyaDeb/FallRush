// P7: tiny motion helpers (no library). countUp eases a number into an element (≤ 0.6 s, ease-out); reduced motion
// jumps straight to the value. bump / shake restart a CSS animation class.
const reduced = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
const last = new WeakMap<Element, number>();
export function countUp(el: Element | null, to: number, fmt: (n: number) => string = (n) => Math.round(n).toLocaleString()) {
  if (!el) return;
  const from = last.get(el) ?? 0;
  last.set(el, to);
  if (reduced() || from === to) { el.textContent = fmt(to); return; }
  const dur = Math.min(600, 180 + 90 * Math.log10(1 + Math.abs(to - from))), t0 = performance.now();
  const step = (t: number) => {
    const u = Math.min(1, (t - t0) / dur), e = 1 - (1 - u) ** 3;
    el.textContent = fmt(from + (to - from) * e);
    if (u < 1 && last.get(el) === to) requestAnimationFrame(step);
  };
  requestAnimationFrame(step);
}
export function restart(el: Element | null, cls: 'bump' | 'shake' | 'flash') {
  if (!el || reduced()) return;
  el.classList.remove(cls);
  void (el as HTMLElement).offsetWidth;
  el.classList.add(cls);
}
/** Stagger index for `.enter` children. */
export const stagger = (i: number) => `style="--i:${i}"`;
