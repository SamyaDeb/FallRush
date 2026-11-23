/**
 * The page opens like a capsule toy: the screen is a giant capsule (clear top, magenta bottom, ink seam). A coin drops,
 * the crank turns while the 3D loads, then the capsule wobbles and pops apart to reveal the hero. Click to skip.
 * Return visits in the same session get a quick pop; reduced motion gets a fade.
 */
import { useEffect, useRef, useState } from 'react';
import { gsap } from '../lib/scroll';
import { prefersReducedMotion } from '../three/common';
import { play } from '../lib/sfx';

const SEEN = 'fr_seen_intro';

export function CapsuleLoader({ ready }: { ready: boolean }) {
  const root = useRef<HTMLDivElement>(null);
  const [gone, setGone] = useState(false);
  const [pct, setPct] = useState(0);
  const started = useRef(performance.now());
  const opened = useRef(false);
  const quick = useRef(false);

  useEffect(() => {
    try {
      quick.current = sessionStorage.getItem(SEEN) === '1';
      sessionStorage.setItem(SEEN, '1');
    } catch {
      /* ignore */
    }
    document.documentElement.classList.add('intro');
    return () => document.documentElement.classList.remove('intro');
  }, []);

  // fake-but-honest progress: creeps while loading, completes when the hero scene has drawn
  useEffect(() => {
    if (gone) return;
    let raf = 0;
    const tick = () => {
      setPct((p) => (ready ? Math.min(100, p + 9) : Math.min(88, p + (90 - p) * 0.03)));
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [ready, gone]);

  const open = () => {
    if (opened.current || !root.current) return;
    opened.current = true;
    const el = root.current;
    const done = () => {
      document.documentElement.classList.remove('intro');
      setGone(true);
    };
    if (prefersReducedMotion()) {
      gsap.to(el, { opacity: 0, duration: 0.4, onComplete: done });
      return;
    }
    play('capsulepop');
    const top = el.querySelector('.cl-top'), bot = el.querySelector('.cl-bot'), seam = el.querySelector('.cl-seam'), mid = el.querySelector('.cl-mid');
    const tl = gsap.timeline({ onComplete: done });
    if (!quick.current) tl.to(el, { keyframes: { rotate: [0, -2.5, 2.5, -1.5, 1, 0] }, duration: 0.5, ease: 'none' });
    tl.to(mid, { scale: 0.6, opacity: 0, duration: 0.2 }, quick.current ? 0 : 0.35)
      .to(seam, { scaleX: 1.4, opacity: 0, duration: 0.25 }, '<')
      .to(top, { yPercent: -110, rotate: -8, duration: 0.7, ease: 'back.in(1.2)' }, '<0.05')
      .to(bot, { yPercent: 110, rotate: 6, duration: 0.7, ease: 'back.in(1.2)' }, '<');
  };

  // open once ready (min 1.6 s for the first visit so the gag lands), or after 6 s regardless
  useEffect(() => {
    if (gone) return;
    const minT = quick.current ? 200 : 1600;
    const wait = Math.max(0, minT - (performance.now() - started.current));
    if (ready) {
      const id = setTimeout(open, wait);
      return () => clearTimeout(id);
    }
    const id = setTimeout(open, 6000);
    return () => clearTimeout(id);
  }, [ready, gone]);

  // coin + crank
  useEffect(() => {
    const el = root.current;
    if (!el || prefersReducedMotion()) return;
    const ctx = gsap.context(() => {
      gsap.fromTo('.cl-coin', { y: -120, rotateY: 0, opacity: 0 }, { y: 0, rotateY: 720, opacity: 1, duration: 0.6, ease: 'bounce.out' });
      gsap.to('.cl-crank', { rotate: 360, duration: 1.1, repeat: -1, ease: 'power1.inOut', delay: 0.5 });
      gsap.to('.cl-mid .cl-title', { y: -6, duration: 0.6, yoyo: true, repeat: -1, ease: 'sine.inOut' });
    }, el);
    return () => ctx.revert();
  }, []);

  if (gone) return null;
  return (
    <div ref={root} className="capsule-loader" onClick={open} role="presentation">
      <div className="cl-top" />
      <div className="cl-bot" />
      <div className="cl-seam" />
      <div className="cl-mid">
        <div className="cl-machine" aria-hidden="true">
          <div className="cl-slot">
            <span className="cl-coin" />
          </div>
          <span className="cl-crank" />
        </div>
        <div className="cl-title">
          <span className="word">FALL</span> <span className="word yellow">RUSH</span>
        </div>
        <div className="cl-bar" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(pct)} aria-label="Loading">
          <i style={{ width: pct + '%' }} />
        </div>
        <span className="cl-hint">winding up the Rushlings… (click to skip)</span>
      </div>
    </div>
  );
}
