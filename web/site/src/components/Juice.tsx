/**
 * Page-wide juice: a foam-mallet cursor that swings on click (fine pointers only), magnetic squashy buttons (`.mag`),
 * and the comic "BONK!" burst the hero fires when a Rushling gets bonked.
 */
import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react';
import { gsap } from '../lib/scroll';
import { prefersReducedMotion } from '../three/common';

export interface JuiceHandle {
  bonk: (x: number, y: number) => void;
}

const WORDS = ['BONK!', 'BOING!', 'WHEE!', 'POW!', 'BOP!', 'YEET!'];

export const Juice = forwardRef<JuiceHandle>(function Juice(_, ref) {
  const cursor = useRef<HTMLDivElement>(null);
  const layer = useRef<HTMLDivElement>(null);
  const reduced = useRef(prefersReducedMotion());

  useImperativeHandle(ref, () => ({
    bonk(x, y) {
      const host = layer.current;
      if (!host) return;
      const el = document.createElement('span');
      el.className = 'bonk-word';
      el.textContent = WORDS[Math.floor(Math.random() * WORDS.length)];
      el.style.left = x + 'px';
      el.style.top = y + 'px';
      host.appendChild(el);
      if (reduced.current) {
        gsap.fromTo(el, { opacity: 1 }, { opacity: 0, duration: 0.9, delay: 0.3, onComplete: () => el.remove() });
        return;
      }
      const tl = gsap.timeline({ onComplete: () => el.remove() });
      tl.fromTo(el, { scale: 0.2, rotate: gsap.utils.random(-30, 30), xPercent: -50, yPercent: -50 }, { scale: 1.15, duration: 0.18, ease: 'back.out(3)' })
        .to(el, { y: -60, scale: 1, duration: 0.6, ease: 'power2.out' }, 0.18)
        .to(el, { opacity: 0, duration: 0.25 }, 0.6);
      // a quick page shake
      gsap.fromTo(document.body, { x: 0 }, { x: 0, duration: 0.3, keyframes: { x: [0, -6, 5, -3, 2, 0] }, ease: 'none', clearProps: 'transform' });
    },
  }));

  // mallet cursor
  useEffect(() => {
    const fine = window.matchMedia('(hover: hover) and (pointer: fine)').matches;
    const el = cursor.current;
    if (!fine || !el) return;
    document.documentElement.classList.add('mallet-cursor');
    const xTo = gsap.quickTo(el, 'x', { duration: 0.12, ease: 'power3' });
    const yTo = gsap.quickTo(el, 'y', { duration: 0.12, ease: 'power3' });
    let shown = false;
    const move = (e: PointerEvent) => {
      if (e.pointerType !== 'mouse') return;
      xTo(e.clientX);
      yTo(e.clientY);
      const t = e.target as HTMLElement | null;
      const typing = !!t?.closest('input, textarea, select, [contenteditable="true"]');
      const hot = !!t?.closest('a, button, canvas, [role="button"], label');
      el.classList.toggle('hide', typing);
      el.classList.toggle('hot', hot);
      if (!shown) {
        shown = true;
        gsap.set(el, { x: e.clientX, y: e.clientY });
        el.classList.add('on');
      }
    };
    const down = () => {
      if (reduced.current) return;
      gsap.fromTo(el.firstElementChild, { rotate: 0 }, { rotate: -55, duration: 0.07, yoyo: true, repeat: 1, ease: 'power2.in' });
    };
    const leave = () => el.classList.remove('on');
    const enter = () => shown && el.classList.add('on');
    window.addEventListener('pointermove', move, { passive: true });
    window.addEventListener('pointerdown', down);
    document.addEventListener('mouseleave', leave);
    document.addEventListener('mouseenter', enter);
    return () => {
      document.documentElement.classList.remove('mallet-cursor');
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerdown', down);
      document.removeEventListener('mouseleave', leave);
      document.removeEventListener('mouseenter', enter);
    };
  }, []);

  // magnetic, squashy buttons
  useEffect(() => {
    if (reduced.current || !window.matchMedia('(hover: hover)').matches) return;
    let cur: HTMLElement | null = null;
    const move = (e: PointerEvent) => {
      const t = (e.target as HTMLElement | null)?.closest<HTMLElement>('.mag');
      if (cur && cur !== t) {
        gsap.to(cur, { x: 0, y: 0, scaleX: 1, scaleY: 1, duration: 0.5, ease: 'elastic.out(1, 0.35)' });
        cur = null;
      }
      if (!t) return;
      cur = t;
      const r = t.getBoundingClientRect();
      const dx = (e.clientX - (r.left + r.width / 2)) / (r.width / 2);
      const dy = (e.clientY - (r.top + r.height / 2)) / (r.height / 2);
      gsap.to(t, { x: dx * 7, y: dy * 5, scaleX: 1.04, scaleY: 0.97, duration: 0.25, ease: 'power3' });
    };
    window.addEventListener('pointermove', move, { passive: true });
    return () => window.removeEventListener('pointermove', move);
  }, []);

  return (
    <>
      <div ref={layer} className="bonk-layer" aria-hidden="true" />
      <div ref={cursor} className="mallet" aria-hidden="true">
        <svg width="54" height="54" viewBox="0 0 54 54">
          <g>
            <rect x="24" y="20" width="6" height="30" rx="3" fill="#FFD21F" stroke="#2A1250" strokeWidth="2.5" />
            <rect x="8" y="4" width="38" height="20" rx="9" fill="#FF3EA5" stroke="#2A1250" strokeWidth="3" />
            <rect x="16" y="4" width="6" height="20" fill="#FFFFFF" opacity="0.9" />
            <rect x="32" y="4" width="6" height="20" fill="#FFFFFF" opacity="0.9" />
            <rect x="8" y="4" width="38" height="20" rx="9" fill="none" stroke="#2A1250" strokeWidth="3" />
          </g>
        </svg>
      </div>
    </>
  );
});
