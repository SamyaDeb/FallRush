/**
 * `/` — the landing: capsule loader → 3D hero race (click a Rushling to bonk it) → the scroll story through all four
 * rounds and the on-chain Final → the Rushling pit → the finale. Play (the live private beta, /play) and the waitlist
 * (/join → email → capsule reveal in the race → poster + Share on X) stay two separate paths.
 */
import { Component, Suspense, lazy, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { CapsuleLoader } from '../components/CapsuleLoader';
import { Juice, type JuiceHandle } from '../components/Juice';
import { TopBar } from '../components/TopBar';
import { LIVE_URL, PLAY_URL } from '../lib/links';
import { useLive } from '../lib/live';
import { play, restoreSound, useSound } from '../lib/sfx';
import Story from './Story';
import PitSection from './PitSection';
import Finale from './Finale';
import { loadHeroScene } from '../lib/preload';
import { PLAYABLE, TOPPER_NAME, presetByName, type Preset } from '../lib/presets';
import { usePoster, type PosterState } from '../lib/usePoster';
import { JUST_JOINED_KEY } from './Join';
import { ApiError, fmt, forgetSpot, loadSpot, refreshSpot, shareCaption, xIntentUrl, type Spot } from '../lib/waitlist';

const HeroScene = lazy(loadHeroScene);

type Stage = 'button' | 'opening' | 'card';

class SceneBoundary extends Component<{ onError: () => void; children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  componentDidCatch(e: unknown) {
    console.warn('Fall Rush scene failed, showing the key art', e);
    this.props.onError();
  }
  render() {
    return this.state.failed ? null : this.props.children;
  }
}

const webglOk = () => {
  try {
    const c = document.createElement('canvas');
    return !!(c.getContext('webgl2') || c.getContext('webgl'));
  } catch {
    return false;
  }
};

const frac = (s: Spot) => (s.total > 1 ? Math.min(1, (s.rank - 1) / (s.total - 1)) : 0);

export default function Home() {
  const [spot, setSpot] = useState<Spot | null>(() => loadSpot());
  // just came back from /join: their Rushling drops into the race in a capsule
  const [reveal] = useState(() => {
    try {
      const j = sessionStorage.getItem(JUST_JOINED_KEY) === '1';
      sessionStorage.removeItem(JUST_JOINED_KEY);
      return j && !!loadSpot() && webglOk();
    } catch {
      return false;
    }
  });
  const [stage, setStage] = useState<Stage>(() => (loadSpot() ? (reveal ? 'opening' : 'card') : 'button'));
  const [failed, setFailed] = useState(() => typeof document !== 'undefined' && !webglOk());
  const [showScene, setShowScene] = useState(false);
  const [playing, setPlaying] = useState(false);
  const [heroReady, setHeroReady] = useState(false);
  const { poster, make: makePoster, fromSaved } = usePoster((s) => setSpot(s));
  const juice = useRef<JuiceHandle>(null);
  const live = useLive(true);
  const [sound, setSound] = useSound();
  useEffect(restoreSound, []);

  useEffect(() => {
    document.title = 'Fall Rush — the provably fair knockout party game';
  }, []);

  // lazy-load the 3D bundle after first paint
  useEffect(() => {
    if (failed) return;
    const id = requestAnimationFrame(() => setTimeout(() => setShowScene(true), 0));
    return () => cancelAnimationFrame(id);
  }, [failed]);

  // safety net for the capsule stage (tab hidden, scene slow)
  useEffect(() => {
    if (stage !== 'opening') return;
    const id = setTimeout(() => setStage('card'), failed ? 0 : 7000);
    return () => clearTimeout(id);
  }, [stage, failed]);

  // Esc leaves play mode
  useEffect(() => {
    if (!playing) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setPlaying(false);
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [playing]);

  const preset = useMemo(() => (spot ? presetByName(spot.rushling) ?? PLAYABLE[0] : null), [spot]);

  // returning visitor: refresh the place in line; use the uploaded poster, or re-make it if they climbed
  useEffect(() => {
    const saved = loadSpot();
    if (!saved) return;
    let live = true;
    refreshSpot(saved)
      .then((s) => {
        if (live) (setSpot(s), fromSaved(s));
      })
      .catch((e) => {
        if (!live) return;
        if (e instanceof ApiError && e.code === 'not_found') {
          // this browser remembers a spot the server no longer has: start fresh
          forgetSpot();
          setSpot(null);
          setStage('button');
        } else fromSaved(saved);
      });
    return () => {
      live = false;
    };
  }, [fromSaved]);

  const sceneYou = useMemo(() => (spot && preset ? { preset, frac: frac(spot), reveal } : null), [spot, preset, reveal]);

  return (
    <div className={'landing' + (playing ? ' is-playing' : '')}>
    <CapsuleLoader ready={heroReady || failed} />
    <Juice ref={juice} />
    <TopBar sound={sound} setSound={setSound} hidden={playing} />
    <div id="top" className={'page' + (playing ? ' playing' : '')}>
      {showScene && !failed && (
        <SceneBoundary onError={() => setFailed(true)}>
          <Suspense fallback={null}>
            <HeroScene
              you={sceneYou}
              playing={playing}
              onRevealed={() => setStage('card')}
              onReady={() => setHeroReady(true)}
              onBonk={(x, y) => {
                play('bonk');
                play('chirp', { vol: 0.6 });
                juice.current?.bonk(x, y);
              }}
            />
          </Suspense>
        </SceneBoundary>
      )}

      <div className="ol">
        <header className="top">
          <a href="/" className="brand" aria-label="Fall Rush home">
            <span className="word">
              FALL <span className="yellow">RUSH</span>
            </span>
          </a>
          <nav className="topnav" aria-label="Main">
            <button type="button" className="iconbtn btn" aria-pressed={sound} aria-label={sound ? 'Sound off' : 'Sound on'} onClick={() => setSound(!sound)}>
              {sound ? <SoundOn /> : <SoundOff />}
            </button>
            <a className="navlink btn" href={LIVE_URL}>
              Watch live
            </a>
            <a className="playfree btn mag" href={PLAY_URL}>
              ▶ Play the beta
            </a>
          </nav>
        </header>

        <main className={'hero' + (playing ? ' away' : '') + (stage !== 'button' ? ' has-card' : '')} aria-hidden={playing}>
          <div className="col">
            <div className="chip">
              <span className="dot">
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#2A1250" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                  <path d="M3 18h18l-2-11-5 5-2-7-2 7-5-5z" />
                </svg>
              </span>
              <span>Private beta · live on devnet · open to everyone</span>
            </div>
            {live.ok && (
              <a className="livepill" href={LIVE_URL}>
                <span className="pulse" />
                {live.humans > 0 ? `${live.humans} playing now` : 'Arenas open'} · {live.arenasUp} {live.arenasUp === 1 ? 'arena' : 'arenas'} up
              </a>
            )}

            <div className="logo">
              <h1>
                <span className="word">FALL</span>
                <span className="word yellow">RUSH</span>
              </h1>
              <div className="underline" aria-hidden="true">
                <div className="bar" />
                <div className="flag">
                  <span className="p" /><span /><span className="p" /><span />
                  <span /><span className="p" /><span /><span className="p" />
                </div>
              </div>
            </div>

            <p className="tagline">
              The knockout party game whose Final runs <b>on-chain</b>. 12 players, 4 rounds, 1 crown. No wallet, no popups.
            </p>
            {stage === 'button' && (
              <div className="cta">
                <a href={PLAY_URL} className="btn btn-play mag">
                  ▶ Play the beta
                </a>
                <Link to="/join" className="btn btn-join mag">
                  Join waitlist
                  <Arrow />
                </Link>
              </div>
            )}
            {stage !== 'button' && (
              <div className="cta">
                <a href={PLAY_URL} className="btn btn-play small mag">
                  ▶ Play the beta
                </a>
              </div>
            )}
            {!failed && (
              <button type="button" className="linkbtn drive" aria-pressed={playing} onClick={() => setPlaying((p) => !p)}>
                {playing ? 'Exit (Esc)' : 'or take Bolt for a spin here (WASD / tap) · click a Rushling to bonk it'}
              </button>
            )}
            {stage === 'opening' && (
              <div role="status" aria-live="polite" className="status">
                Cracking open your capsule…
              </div>
            )}
            {stage === 'card' && spot && preset && <SpotCard spot={spot} preset={preset} poster={poster} onRetryPoster={() => makePoster(spot)} />}
          </div>
        </main>
        {playing && (
          <button type="button" className="exitplay btn" onClick={() => setPlaying(false)}>
            Exit
          </button>
        )}
        {!playing && (
          <div className="scrollhint" aria-hidden="true">
            <span>Scroll to drop in</span>
            <i />
          </div>
        )}
      </div>
    </div>
    <Story webgl={!failed} live={live} />
    <PitSection webgl={!failed} />
    <Finale live={live} />
    </div>
  );
}

const SoundOn = () => (
  <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M4 9v6h4l5 4V5L8 9z" fill="currentColor" />
    <path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12" />
  </svg>
);
const SoundOff = () => (
  <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M4 9v6h4l5 4V5L8 9z" fill="currentColor" />
    <path d="M17 9l5 6M22 9l-5 6" />
  </svg>
);

const Arrow = () => (
  <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="#2A1250" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M5 12h14" />
    <path d="M13 6l6 6-6 6" />
  </svg>
);

const XLogo = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="#FFFFFF" aria-hidden="true">
    <path d="M17.8 3h3.1l-6.8 7.7L22 21h-6.2l-4.9-6.4L5.3 21H2.2l7.3-8.3L2 3h6.4l4.4 5.8zm-1.1 16.2h1.7L7.4 4.7H5.6z" />
  </svg>
);

function SpotCard({ spot, preset, poster, onRetryPoster }: { spot: Spot; preset: Preset; poster: PosterState; onRetryPoster: () => void }) {
  const [copied, setCopied] = useState(false);
  const link = spot.shareUrl;
  const caption = shareCaption(preset.name, spot.rank);
  const pct = Math.round((1 - frac(spot)) * 92 + 8);
  const posterBusy = poster.status === 'making' || poster.status === 'uploading';

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(link);
    } catch {
      /* ignore */
    }
    setCopied(true);
    setTimeout(() => setCopied(false), 1600);
  };

  return (
    <section className="fr-card spot" aria-label="Your waitlist spot">
      <div className="spot-grid">
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12, minWidth: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
            <div
              aria-hidden="true"
              className="swatch"
              style={{ background: `linear-gradient(180deg, #FFFFFF 0 48%, #2A1250 48% 53%, ${preset.shell} 53% 100%)` }}
            />
            <div style={{ display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 }}>
              <span className="eyebrow">YOUR RUSHLING</span>
              <span className="big-name">
                {preset.name} <span className="rank">#{fmt(spot.rank)}</span>
              </span>
              <span style={{ fontSize: 14 }}>
                {preset.emblem} emblem · {TOPPER_NAME[preset.topper]}
                {spot.referrals ? ` · ${spot.referrals} ${spot.referrals === 1 ? 'friend' : 'friends'} joined` : ''}
              </span>
            </div>
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, fontSize: 14 }}>
              <span>{spot.rank <= 1 ? 'You are at the front of the line!' : `${fmt(spot.rank - 1)} ${spot.rank === 2 ? 'Rushling' : 'Rushlings'} ahead of you`}</span>
              <span style={{ fontFamily: 'var(--display)' }}>Finish</span>
            </div>
            <div className="bar-track">
              <div className="bar-fill" style={{ width: pct + '%', background: preset.shell }} />
            </div>
          </div>
        </div>
        <div className="poster-thumb" aria-live="polite">
          {poster.url ? (
            <a href={poster.url} target="_blank" rel="noopener" title="Open your poster">
              <img src={poster.url} alt={`Your Fall Rush poster: ${preset.name}, number ${fmt(spot.rank)} in line for the crown`} />
            </a>
          ) : poster.status === 'error' ? (
            <button type="button" className="linkbtn" onClick={onRetryPoster}>
              Try again
            </button>
          ) : (
            <span>Painting your poster…</span>
          )}
        </div>
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <span style={{ fontSize: 15 }}>
          <strong style={{ fontFamily: 'var(--display)', fontWeight: 400 }}>Climb the line:</strong> post your poster. Each friend who joins with your link moves you up 25 spots.
        </span>
        <div className="row">
          <a
            href={xIntentUrl(caption, link)}
            target="_blank"
            rel="noopener"
            className="btn btn-s x"
            aria-disabled={posterBusy}
            onClick={(e) => posterBusy && e.preventDefault()}
            style={posterBusy ? { opacity: 0.65, cursor: 'progress' } : undefined}
          >
            <XLogo />
            {posterBusy ? 'Preparing your poster…' : 'Share on X'}
          </a>
          {poster.url && (
            <a className="btn btn-s" href={poster.url} download={`fall-rush-${spot.code}.png`}>
              Download poster
            </a>
          )}
        </div>
        <div className="row">
          <label htmlFor="fr-ref" className="sr-only">
            Your invite link
          </label>
          <input id="fr-ref" className="field" readOnly value={link} onFocus={(e) => e.currentTarget.select()} />
          <button type="button" className="btn btn-s" onClick={copy}>
            {copied ? 'Copied!' : 'Copy link'}
          </button>
        </div>
        <p role="status" aria-live="polite" className="privacy" style={{ minHeight: '1em' }}>
          {poster.note || ''}
        </p>
      </div>
    </section>
  );
}
