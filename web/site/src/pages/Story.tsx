/**
 * The scroll story (below the hero): a tall section with a sticky stage. The 3D scene (StoryCanvas) and this DOM copy
 * read the same progress; GSAP ScrollTrigger scrubs the copy in and out per chapter. Without WebGL the chapters
 * simply stack as readable cards.
 */
import { Suspense, lazy, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { gsap, SplitText, useStoryProgress } from '../lib/scroll';
import { CHAPTERS, ROUND_CARD, ZONE, aliveAt, localP, type ChapterId } from '../three/story/timeline';
import { play } from '../lib/sfx';
import { PLAY_URL, PROGRAM, RUSH_MINT, VALIDATOR, explorer } from '../lib/links';
import { prefersReducedMotion } from '../three/common';
import type { Live } from '../lib/live';
import { Link } from 'react-router-dom';

const StoryCanvas = lazy(() => import('../three/StoryCanvas'));

interface Copy {
  id: ChapterId;
  eyebrow: string;
  title: string;
  body: string;
  stat?: string;
  side: 'left' | 'right';
}

const COPY: Copy[] = [
  {
    id: 'fall',
    eyebrow: 'DROP IN',
    title: '12 Rushlings. 4 rounds. 1 crown.',
    body: 'Wind-up capsule toys, dropped into a knockout show. Every round sends some of them home. One walks away wearing the crown.',
    side: 'left',
  },
  {
    id: 'run',
    eyebrow: 'ROUND 1 · RACE',
    title: 'Capsule Run',
    body: 'Gumballs roll down the slope, mallets swing over the hop field, a sweeper spins on the last pad. The first 8 over the line go through.',
    stat: '12 → 8',
    side: 'right',
  },
  {
    id: 'goo',
    eyebrow: 'ROUND 2 · SURVIVAL',
    title: 'Goo Tower',
    body: 'Two laps of spiral ledge round a capsule-machine core, and the glitter goo rising underneath. Climb, or get swallowed.',
    stat: '8 → 5',
    side: 'left',
  },
  {
    id: 'star',
    eyebrow: 'ROUND 3 · SKILL',
    title: 'Star Fountain',
    body: 'The fountain bursts a wave of stars every 8 seconds. Grab them on the turntable, hop the sweepers. Top 3 go to the Final.',
    stat: '5 → 3',
    side: 'right',
  },
  {
    id: 'lid',
    eyebrow: 'THE FINAL · ON-CHAIN',
    title: 'Lid Flip',
    body: 'Lids light up, then swing open under you. And this round does not run on our server: a Solana program steps it, tick by tick, inside a MagicBlock Ephemeral Rollup.',
    stat: '3 → 1',
    side: 'left',
  },
  {
    id: 'crown',
    eyebrow: 'CHAMPION',
    title: 'One crown.',
    body: 'Minted to the winner as a Crown Capsule. Everyone else? Back in the capsule machine for the next show.',
    side: 'right',
  },
];

const CARDS = [
  { at: [0.04, 0.3], k: 'Ephemeral Rollup', v: 'The arena under glass. The fall_rush program simulates the Final at 30 Hz on devnet-as (validator ' + VALIDATOR.slice(0, 4) + '…); a timed task calls tick().', link: ['program', explorer('address', PROGRAM)] },
  { at: [0.24, 0.5], k: 'VRF', v: 'MagicBlock VRF rolls the deal: the maps, the hazard seeds and the capsule rarity. Nobody picks them.' },
  { at: [0.44, 0.7], k: 'Session keys', v: 'A guest wallet signs once. A session key winds up every move, bet and Chaos Drop after that. No popups.' },
  { at: [0.66, 0.96], k: 'Metaplex Core', v: 'The match receipt is committed to Solana and the program mints the Crown Capsule and capsule drops.', link: ['RUSH mint', explorer('address', RUSH_MINT)] },
] as const;


/** chapter-local window → story progress */
const toP = (id: ChapterId, lp: number) => {
  const c = CHAPTERS.find((q) => q.id === id)!;
  const a = c.a === 0 ? 0 : c.a + ZONE;
  const b = c.b === 1 ? 1 : c.b - ZONE;
  return a + lp * (b - a);
};

export default function Story({ webgl, live }: { webgl: boolean; live: Live }) {
  const sec = useRef<HTMLElement>(null);
  const prog = useStoryProgress(sec);
  const [near, setNear] = useState(false);
  const [dig, setDig] = useState(0);
  const reduced = useMemo(prefersReducedMotion, []);
  const aliveRef = useRef<HTMLSpanElement>(null);
  const tickRef = useRef<HTMLSpanElement>(null);
  const wipeRefs = useRef<(HTMLDivElement | null)[]>([]);
  const liveTick = useRef<number | null>(null);
  liveTick.current = live.finalTick;

  // mount the 3D scene when the story is close (the hero keeps the first screen light)
  useEffect(() => {
    const el = sec.current;
    if (!el || !webgl) return;
    const io = new IntersectionObserver((e) => e[0].isIntersecting && setNear(true), { rootMargin: '120% 0px' });
    io.observe(el);
    return () => io.disconnect();
  }, [webgl]);

  // the scrubbed DOM timeline
  useLayoutEffect(() => {
    const el = sec.current;
    if (!el || !webgl) return;
    const ctx = gsap.context(() => {
      const tl = gsap.timeline({ scrollTrigger: { trigger: el, start: 'top top', end: 'bottom bottom', scrub: reduced ? true : 0.6 } });
      tl.set({}, {}, 1); // timeline length = 1 (= story progress)
      COPY.forEach((c, i) => {
        const ch = CHAPTERS[i];
        const card = el.querySelector<HTMLElement>(`[data-ch="${c.id}"]`)!;
        const title = card.querySelector<HTMLElement>('.st-title')!;
        // copy lands after the round card has wiped off, and leaves before the next one arrives
        const inAt = ch.a === 0 ? 0 : ch.a + ZONE * 0.75;
        const outAt = ch.b - ZONE - 0.022;
        if (i === 0) gsap.set(card, { autoAlpha: 1 });
        else tl.fromTo(card, { autoAlpha: 0, y: 70, rotate: c.side === 'left' ? -3 : 3 }, { autoAlpha: 1, y: 0, rotate: 0, duration: 0.025, ease: 'back.out(2)' }, inAt);
        if (!reduced) {
          const split = new SplitText(title, { type: 'chars,words', charsClass: 'st-char' });
          const pop = { yPercent: 130, rotate: () => gsap.utils.random(-25, 25), opacity: 0, ease: 'back.out(2.5)' };
          // the first title pops once as the story arrives; the others scrub with the scroll
          if (i === 0) gsap.from(split.chars, { ...pop, stagger: 0.025, duration: 0.6, scrollTrigger: { trigger: el, start: 'top 55%', toggleActions: 'play none none none' } });
          else tl.from(split.chars, { ...pop, stagger: 0.0012, duration: 0.02 }, inAt + 0.008);
        }
        if (i < COPY.length - 1) tl.to(card, { autoAlpha: 0, y: -60, duration: 0.022, ease: 'power2.in' }, outAt);
      });
      CARDS.forEach((cd, k) => {
        const node = el.querySelector<HTMLElement>(`[data-card="${k}"]`)!;
        tl.fromTo(node, { autoAlpha: 0, x: 60, scale: 0.9 }, { autoAlpha: 1, x: 0, scale: 1, duration: 0.012, ease: 'back.out(2)' }, toP('lid', cd.at[0]));
        tl.to(node, { autoAlpha: 0, x: -40, duration: 0.01 }, toP('lid', cd.at[1]));
      });
      const chips = el.querySelector<HTMLElement>('.st-chips');
      if (chips) {
        tl.fromTo(chips, { autoAlpha: 0 }, { autoAlpha: 1, duration: 0.01 }, toP('lid', 0.05));
        tl.to(chips, { autoAlpha: 0, duration: 0.01 }, toP('lid', 0.95));
      }
      const hud = el.querySelector<HTMLElement>('.st-hud');
      if (hud) {
        tl.fromTo(hud, { autoAlpha: 0, y: -20 }, { autoAlpha: 1, y: 0, duration: 0.02 }, 0.02);
      }
    }, el);
    return () => ctx.revert();
  }, [webgl, reduced]);

  // counters (Rushlings left, Final tick) and the round-card wipes, all from the same eased progress as the 3D
  useEffect(() => {
    if (!webgl) return;
    let raf = 0;
    let lastAlive = -1;
    let lastP = 0;
    const loop = () => {
      const pr = prog.current;
      const p = near ? pr.p : pr.raw;
      const alive = aliveAt(p);
      if (alive !== lastAlive && aliveRef.current) {
        aliveRef.current.textContent = String(alive);
        if (lastAlive !== -1 && !reduced) gsap.fromTo(aliveRef.current, { scale: 1.7, rotate: -12 }, { scale: 1, rotate: 0, duration: 0.5, ease: 'elastic.out(1, 0.4)' });
        lastAlive = alive;
      }
      // round cards: slide in from the right, cover the screen while the camera cuts, slide off to the left
      for (let i = 1; i < CHAPTERS.length; i++) {
        const el = wipeRefs.current[i];
        if (!el) continue;
        const d = (p - CHAPTERS[i].a) / ZONE;
        if (d <= -1 || d >= 1) {
          if (el.style.visibility !== 'hidden') el.style.visibility = 'hidden';
          continue;
        }
        el.style.visibility = 'visible';
        const HOLD = 0.32;
        const x = d < -HOLD ? (-HOLD - d) / (1 - HOLD) : d > HOLD ? -(d - HOLD) / (1 - HOLD) : 0;
        const e = Math.sign(x) * (1 - Math.pow(1 - Math.abs(x), 2.2));
        if (reduced) {
          el.style.opacity = String(1 - Math.abs(e));
          el.style.transform = 'none';
        } else {
          el.style.opacity = '1';
          el.style.transform = `translate3d(${e * 118}%, 0, 0) skewX(${-8 * e}deg)`;
          (el.firstElementChild as HTMLElement).style.transform = `translate3d(${e * 40}%, 0, 0)`;
        }
        const b = CHAPTERS[i].a;
        if (lastP < b - ZONE * HOLD && p >= b - ZONE * HOLD) play('count', { vol: 0.5 });
      }
      lastP = p;
      if (tickRef.current) {
        const lp = localP(p, CHAPTERS[4]);
        const t = liveTick.current ?? Math.floor(lp * 3600);
        tickRef.current.textContent = String(t).padStart(6, '0');
      }
      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(raf);
  }, [webgl, near, prog, reduced]);

  // decorative: tick numbers drifting up out of the rollup (illustration, not real transactions)
  const chips = useMemo(() => Array.from({ length: 6 }, (_, i) => ({ s: String(1200 + i * 233).padStart(6, '0'), x: (i * 37) % 92, d: (i % 5) * 1.3, dur: 7 + (i % 4) * 1.6 })), []);

  return (
    <section ref={sec} id="story" className={'story' + (webgl ? '' : ' flat')} style={{ ['--dig' as string]: dig }} aria-label="How a Fall Rush show plays out">
      <div className="cloud-edge" aria-hidden="true" />
      <div className="st-sticky">
        {webgl && near && (
          <Suspense fallback={null}>
            <StoryCanvas prog={prog} onDigital={setDig} />
          </Suspense>
        )}
        {webgl && (
          <div className="st-hud" aria-hidden="true">
            <div className="st-alive">
              <span ref={aliveRef} className="n">12</span>
              <span className="l">Rushlings
                <br />left</span>
            </div>
          </div>
        )}
        {webgl &&
          CHAPTERS.map((c, i) => {
            const rc = ROUND_CARD[c.id];
            if (!rc || i === 0) return null;
            return (
              <div key={c.id} ref={(n) => void (wipeRefs.current[i] = n)} className={'st-wipe ' + rc.theme} aria-hidden="true" style={{ visibility: 'hidden' }}>
                <div className="wipe-in">
                  <span className="wipe-eyebrow">{rc.eyebrow}</span>
                  <span className="wipe-title">{rc.title}</span>
                  <span className="wipe-line">{rc.line}</span>
                </div>
              </div>
            );
          })}
        <div className="st-copy">
          {COPY.map((c) => (
            <article key={c.id} data-ch={c.id} className={'st-card ' + c.side}>
              <span className="st-eyebrow">{c.eyebrow}</span>
              <h2 className="st-title">{c.title}</h2>
              <p>{c.body}</p>
              {c.stat && <span className="st-stat">{c.stat}</span>}
              {c.id === 'lid' && (
                <div className="st-tick">
                  <span className="dot" /> TICK <span ref={tickRef}>000000</span> · 30 Hz {live.finalTick != null ? '· LIVE NOW' : '· devnet-as'}
                </div>
              )}
              {c.id === 'crown' && (
                <div className="row st-ctas">
                  <a className="btn btn-play mag" href={PLAY_URL}>
                    ▶ Play the beta
                  </a>
                  <Link className="btn btn-wait mag" to="/join">
                    Join the waitlist
                  </Link>
                </div>
              )}
            </article>
          ))}
          <div className="st-cards">
            {CARDS.map((cd, k) => (
              <div key={cd.k} data-card={k} className="st-mini">
                <b>{cd.k}</b>
                <span>{cd.v}</span>
                {'link' in cd && cd.link && (
                  <a href={cd.link[1]} target="_blank" rel="noopener">
                    {cd.link[0]} on explorer ↗
                  </a>
                )}
              </div>
            ))}
          </div>
          {webgl && (
            <div className="st-chips" aria-hidden="true">
              {chips.map((c, i) => (
                <span key={i} style={{ left: c.x + '%', animationDelay: `-${c.d}s`, animationDuration: c.dur + 's' }}>
                  tick {c.s}
                </span>
              ))}
            </div>
          )}
        </div>
      </div>
    </section>
  );
}
