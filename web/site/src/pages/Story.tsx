/**
 * The scroll story (below the hero): a tall section with a sticky stage. The 3D scene (StoryCanvas) and this DOM copy
 * read the same progress; GSAP ScrollTrigger scrubs the copy in and out per chapter. Without WebGL the chapters
 * simply stack as readable cards.
 */
import { Suspense, lazy, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { gsap, SplitText, useStoryProgress } from '../lib/scroll';
import { CHAPTERS, ROUND_LABEL, ZONE, aliveAt, chapterAt, localP, type ChapterId } from '../three/story/timeline';
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
  const labelRef = useRef<HTMLSpanElement>(null);
  const tickRef = useRef<HTMLSpanElement>(null);
  const dotsRef = useRef<HTMLDivElement>(null);
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
        const inAt = ch.a === 0 ? 0 : ch.a + ZONE * 0.6;
        const outAt = ch.b - ZONE - 0.025;
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

  // counters: Rushlings left, round label, Final tick, chapter dots
  useEffect(() => {
    if (!webgl) return;
    let raf = 0;
    let lastAlive = -1, lastCh = -1;
    const loop = () => {
      const pr = prog.current;
      const p = near ? pr.p : pr.raw;
      const alive = aliveAt(p);
      if (alive !== lastAlive && aliveRef.current) {
        aliveRef.current.textContent = String(alive);
        if (lastAlive !== -1 && !reduced) gsap.fromTo(aliveRef.current, { scale: 1.7, rotate: -12 }, { scale: 1, rotate: 0, duration: 0.5, ease: 'elastic.out(1, 0.4)' });
        lastAlive = alive;
      }
      const ch = chapterAt(p);
      if (ch !== lastCh) {
        if (labelRef.current) labelRef.current.textContent = ROUND_LABEL[CHAPTERS[ch].id];
        dotsRef.current?.querySelectorAll('i').forEach((d, k) => d.classList.toggle('on', k <= ch));
        lastCh = ch;
      }
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
  const chips = useMemo(() => Array.from({ length: 9 }, (_, i) => ({ s: String(1200 + i * 233).padStart(6, '0'), x: (i * 37) % 92, d: (i % 5) * 1.3, dur: 7 + (i % 4) * 1.6 })), []);

  return (
    <section ref={sec} className={'story' + (webgl ? '' : ' flat')} style={{ ['--dig' as string]: dig }} aria-label="How a Fall Rush show plays out">
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
            <div className="st-round">
              <span ref={labelRef}>Drop in</span>
              <div ref={dotsRef} className="dots">
                {CHAPTERS.map((c) => (
                  <i key={c.id} />
                ))}
              </div>
            </div>
          </div>
        )}
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
