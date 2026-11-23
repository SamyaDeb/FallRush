/**
 * The slim bar that slides in once the hero is behind you: logo (back to top), the story's round rail (click to jump),
 * sound, and an always-there Play the beta.
 */
import { useEffect, useRef, useState } from 'react';
import { CHAPTERS, chapterAt } from '../three/story/timeline';
import { PLAY_URL } from '../lib/links';

const LABELS = ['Drop', 'Race', 'Goo', 'Stars', 'Final', 'Crown'];

export function TopBar({ sound, setSound, hidden }: { sound: boolean; setSound: (v: boolean) => void; hidden?: boolean }) {
  const [shown, setShown] = useState(false);
  const [inStory, setInStory] = useState(false);
  const [ch, setCh] = useState(0);
  const fill = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    let raf = 0;
    const read = () => {
      raf = 0;
      const hero = document.querySelector<HTMLElement>('.landing > .page');
      const story = document.getElementById('story');
      const heroH = hero?.offsetHeight ?? window.innerHeight;
      setShown(window.scrollY > heroH - 90);
      if (!story) return;
      const r = story.getBoundingClientRect();
      const range = r.height - window.innerHeight;
      const p = range > 0 ? Math.min(1, Math.max(0, -r.top / range)) : 0;
      setInStory(r.top < window.innerHeight * 0.5 && r.bottom > window.innerHeight * 0.5);
      setCh(chapterAt(p));
      if (fill.current) fill.current.style.transform = `scaleX(${p})`;
    };
    const onScroll = () => {
      if (!raf) raf = requestAnimationFrame(read);
    };
    read();
    window.addEventListener('scroll', onScroll, { passive: true });
    window.addEventListener('resize', onScroll);
    return () => {
      window.removeEventListener('scroll', onScroll);
      window.removeEventListener('resize', onScroll);
      cancelAnimationFrame(raf);
    };
  }, []);

  const jump = (i: number) => {
    const story = document.getElementById('story');
    if (!story) return;
    const c = CHAPTERS[i];
    const p = i === 0 ? 0.04 : (c.a + c.b) / 2;
    const top = story.offsetTop + p * (story.offsetHeight - window.innerHeight);
    window.scrollTo({ top, behavior: 'smooth' });
  };

  return (
    <div className={'topbar' + (shown && !hidden ? ' on' : '')} aria-hidden={!shown || hidden}>
      <div className={'tb-inner' + (inStory ? ' story' : '')}>
        <a
          href="#top"
          className="tb-brand"
          onClick={(e) => {
            e.preventDefault();
            window.scrollTo({ top: 0, behavior: 'smooth' });
          }}
          tabIndex={shown ? 0 : -1}
        >
          <span className="word">
            FALL <span className="yellow">RUSH</span>
          </span>
        </a>
        <nav className={'tb-rail' + (inStory ? ' on' : '')} aria-label="Jump to a round">
          <span className="tb-track" aria-hidden="true">
            <span ref={fill} className="tb-fill" />
          </span>
          {CHAPTERS.map((c, i) => (
            <button
              key={c.id}
              type="button"
              className={'tb-stop' + (i < ch ? ' done' : i === ch ? ' cur' : '')}
              onClick={() => jump(i)}
              tabIndex={shown && inStory ? 0 : -1}
            >
              <i />
              <span>{LABELS[i]}</span>
            </button>
          ))}
        </nav>
        <div className="tb-right">
          <button type="button" className="tb-icon" aria-pressed={sound} aria-label={sound ? 'Sound off' : 'Sound on'} onClick={() => setSound(!sound)} tabIndex={shown ? 0 : -1}>
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <path d="M4 9v6h4l5 4V5L8 9z" fill="currentColor" />
              {sound ? <path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12" /> : <path d="M17 9l5 6M22 9l-5 6" />}
            </svg>
          </button>
          <a className="tb-play mag" href={PLAY_URL} tabIndex={shown ? 0 : -1}>
            ▶ Play the beta
          </a>
        </div>
      </div>
    </div>
  );
}
