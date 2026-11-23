/**
 * `/join` — 1 Pick · 2 Join · 3 Poster · 4 Share, over the 3D turntable.
 */
import { Suspense, lazy, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { loadJoinScene } from '../lib/preload';
import { PLAYABLE, kitLine, presetByName } from '../lib/presets';
import { usePoster } from '../lib/usePoster';
import { useTurnstile } from '../lib/turnstile';
import { ApiError, fmt, forgetSpot, joinWaitlist, loadSpot, refreshSpot, shareCaption, xIntentUrl, type Spot } from '../lib/waitlist';

const JoinScene = lazy(loadJoinScene);
const EMAIL_RE = /^[^\s@]{1,64}@[^\s@]{1,190}\.[^\s@]{2,63}$/;
export const JUST_JOINED_KEY = 'fallrush-just-joined';

type Step = 1 | 2 | 3 | 4;
const STEPS = ['Pick', 'Join', 'Poster', 'Share'];

const webglOk = () => {
  try {
    const c = document.createElement('canvas');
    return !!(c.getContext('webgl2') || c.getContext('webgl'));
  } catch {
    return false;
  }
};

const XLogo = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="#FFFFFF" aria-hidden="true">
    <path d="M17.8 3h3.1l-6.8 7.7L22 21h-6.2l-4.9-6.4L5.3 21H2.2l7.3-8.3L2 3h6.4l4.4 5.8zm-1.1 16.2h1.7L7.4 4.7H5.6z" />
  </svg>
);

export default function Join() {
  const saved = useMemo(loadSpot, []);
  const [spot, setSpot] = useState<Spot | null>(saved);
  const [step, setStep] = useState<Step>(saved ? 3 : 1);
  const [sel, setSel] = useState(() => {
    const i = PLAYABLE.findIndex((p) => p.name === saved?.rushling);
    return i >= 0 ? i : 0;
  });
  const [party, setParty] = useState(0);
  const [failed] = useState(() => !webglOk());
  const [show3d, setShow3d] = useState(false);
  const { poster, make, fromSaved } = usePoster((s) => setSpot(s));
  const preset = PLAYABLE[sel];
  const heading = useRef<HTMLHeadingElement>(null);
  const navigate = useNavigate();

  useEffect(() => {
    document.title = 'Join the waitlist — Fall Rush';
    const id = requestAnimationFrame(() => setShow3d(true));
    return () => cancelAnimationFrame(id);
  }, []);

  // arriving already joined: start at the poster (saved one, or re-made if they climbed)
  useEffect(() => {
    if (!saved) return;
    refreshSpot(saved)
      .then((s) => (setSpot(s), fromSaved(s)))
      .catch((e) => {
        if (e instanceof ApiError && e.code === 'not_found') {
          forgetSpot(); // the server no longer has this spot: start the flow from the top
          setSpot(null);
          setStep(1);
        } else fromSaved(saved);
      });
  }, [saved, fromSaved]);

  // move focus to the step title for screen readers / keyboard users
  useEffect(() => {
    heading.current?.focus({ preventScroll: true });
  }, [step]);

  const move = (d: number) => setSel((s) => (s + d + PLAYABLE.length) % PLAYABLE.length);

  // ← → keys · Enter to choose (step 1 only, never while typing)
  useEffect(() => {
    if (step !== 1) return;
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement | null)?.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA') return;
      if (e.key === 'ArrowLeft') {
        e.preventDefault();
        move(-1);
      } else if (e.key === 'ArrowRight') {
        e.preventDefault();
        move(1);
      } else if (e.key === 'Enter' && tag !== 'BUTTON' && tag !== 'A') {
        e.preventDefault();
        setStep(2);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [step]);

  const onJoined = (s: Spot) => {
    const i = PLAYABLE.findIndex((p) => p.name === s.rushling);
    if (i >= 0) setSel(i); // an email that already joined keeps its Rushling
    setSpot(s);
    setParty((n) => n + 1);
    try {
      sessionStorage.setItem(JUST_JOINED_KEY, '1');
    } catch {
      /* ignore */
    }
    make(s);
    setStep(3);
  };

  const joinedPreset = spot ? presetByName(spot.rushling) ?? preset : preset;

  return (
    <div className="page join-page">
      {show3d && !failed && (
        <Suspense fallback={null}>
          <JoinScene selected={sel} party={party} celebrate={step >= 3} />
        </Suspense>
      )}

      <div className="ol">
        <header className="top join-top">
          <Link to="/" className="brand" aria-label="Fall Rush home">
            <span className="word">
              FALL <span className="yellow">RUSH</span>
            </span>
          </Link>
          <div className="top-right">
          <button
            type="button"
            className="btn back-round"
            aria-label={step === 2 && !spot ? 'Back to picking your Rushling' : 'Back to the course'}
            title={step === 2 && !spot ? 'Back to Pick' : 'Back to the course'}
            onClick={() => (step === 2 && !spot ? setStep(1) : navigate('/'))}
          >
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="#2A1250" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <path d="M19 12H5" />
              <path d="M11 6l-6 6 6 6" />
            </svg>
          </button>
          <ol className="tracker" aria-label="Steps">
            {STEPS.map((label, i) => {
              const n = i + 1;
              const state = n < step ? 'done' : n === step ? 'cur' : '';
              return (
                <li key={label} className={state} aria-current={n === step ? 'step' : undefined}>
                  <span className="n">
                    {n < step ? (
                      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="#2A1250" strokeWidth="4" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                        <path d="M5 12l5 5 9-10" />
                      </svg>
                    ) : (
                      n
                    )}
                  </span>
                  <span className="lbl">{label}</span>
                </li>
              );
            })}
          </ol>
          </div>
        </header>

        <main className="join-main">
          <div className="stage-gap" aria-hidden="true" />
          <div className="step-col" key={step}>
          <div className="join-title">
            <h1 ref={heading} tabIndex={-1}>
              {step === 1 ? 'Pick your Rushling' : step === 2 ? `Lock in ${preset.name}` : step === 3 ? 'Your race poster' : 'Post it on X'}
            </h1>
          </div>

          {step === 1 && (
            <section className="fr-card panel pick-panel" aria-label="Choose your Rushling">
              <div className="carousel">
                <button type="button" className="btn round" aria-label="Previous Rushling" onClick={() => move(-1)}>
                  <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="#2A1250" strokeWidth="3.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                    <path d="M15 5l-7 7 7 7" />
                  </svg>
                </button>
                <div className="swatches" role="radiogroup" aria-label="Rushling colour">
                  {PLAYABLE.map((p, i) => {
                    let d = (i - sel + PLAYABLE.length) % PLAYABLE.length;
                    if (d > 2) d -= PLAYABLE.length;
                    const ad = Math.abs(d);
                    const scale = ad === 0 ? 1.3 : ad === 1 ? 0.9 : 0.7;
                    return (
                      <button
                        key={p.name}
                        type="button"
                        role="radio"
                        aria-checked={i === sel}
                        aria-label={p.name}
                        tabIndex={i === sel ? 0 : -1}
                        className={'cap' + (i === sel ? ' sel' : '')}
                        onClick={() => (i === sel ? setStep(2) : setSel(i))}
                        style={{
                          background: `linear-gradient(180deg, #FFFFFF 0 46%, #2A1250 46% 52%, ${p.shell} 52% 100%)`,
                          transform: `translate(-50%, -50%) translateX(calc(${d} * var(--sp, 54px))) scale(${scale})`,
                          filter: ad === 0 ? 'none' : ad === 1 ? 'blur(1.6px)' : 'blur(3px)',
                          opacity: ad === 0 ? 1 : ad === 1 ? 0.7 : 0.45,
                          zIndex: 3 - ad,
                        }}
                      />
                    );
                  })}
                </div>
                <button type="button" className="btn round" aria-label="Next Rushling" onClick={() => move(1)}>
                  <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="#2A1250" strokeWidth="3.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                    <path d="M9 5l7 7-7 7" />
                  </svg>
                </button>
              </div>
              <div className="cap-info" aria-live="polite">
                <span key={preset.name} className="name" style={{ color: preset.shell === '#FFD21F' ? '#2A1250' : undefined }}>
                  {preset.name}
                </span>
                <span>{preset.personality}</span>
                <span className="kit">{kitLine(preset)}</span>
              </div>
              <button type="button" className="btn btn-join wide-btn" onClick={() => setStep(2)}>
                Choose {preset.name}
              </button>
              <p className="small center kb-hint">← → keys · Enter to choose</p>
            </section>
          )}

          {step === 2 && <JoinCard name={preset.name} rushling={preset.name} shell={preset.shell} onJoined={onJoined} onBack={() => setStep(1)} />}

          {step === 3 && spot && (
            <section className="fr-card panel poster-panel" aria-label="Your poster">
              <p className="center" style={{ margin: 0 }}>
                <strong className="disp">{joinedPreset.name}</strong> is <strong className="disp">#{fmt(spot.rank)}</strong> in line for the crown.
              </p>
              <PosterFrame poster={poster} alt={`Your Fall Rush poster: ${joinedPreset.name}, number ${fmt(spot.rank)} in line`} onRetry={() => make(spot)} />
              <div className="row center-row">
                {poster.url && (
                  <a className="btn btn-s" href={poster.url} download={`fall-rush-${spot.code}.png`}>
                    Download poster
                  </a>
                )}
                <button type="button" className="btn btn-s x" onClick={() => setStep(4)} disabled={poster.status === 'making'}>
                  <XLogo />
                  Next: post it on X
                </button>
              </div>
              <p role="status" aria-live="polite" className="privacy center">
                {poster.status === 'making' ? 'Painting your poster…' : poster.status === 'uploading' ? 'Saving your poster for your X card…' : poster.note || ''}
              </p>
            </section>
          )}

          {step === 4 && spot && <ShareCard spot={spot} name={joinedPreset.name} poster={poster} />}
          </div>
        </main>
      </div>
    </div>
  );
}

function PosterFrame({ poster, alt, onRetry }: { poster: ReturnType<typeof usePoster>['poster']; alt: string; onRetry: () => void }) {
  return (
    <div className="poster-frame" aria-live="polite">
      {poster.url ? (
        <img src={poster.url} alt={alt} />
      ) : poster.status === 'error' ? (
        <button type="button" className="linkbtn" onClick={onRetry}>
          Poster failed. Try again
        </button>
      ) : (
        <span>Painting your poster…</span>
      )}
    </div>
  );
}

function JoinCard({ name, rushling, shell, onJoined, onBack }: { name: string; rushling: string; shell: string; onJoined: (s: Spot) => void; onBack: () => void }) {
  const [email, setEmail] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const bot = useTurnstile();

  const [waiting, setWaiting] = useState(false); // pressed Join before the human check finished

  const doJoin = async (v: string, token: string | null) => {
    setBusy(true);
    setError('');
    try {
      onJoined(await joinWaitlist(v, rushling, token));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Something went wrong. Try again.');
      bot.reset();
      setBusy(false);
    }
  };

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const v = email.trim();
    if (!EMAIL_RE.test(v)) {
      setError('Enter a valid email, like you@example.com.');
      input.current?.focus();
      return;
    }
    if (bot.enabled && !bot.token) {
      // finish automatically as soon as Turnstile passes (it may ask for one tick)
      setWaiting(true);
      setError('');
      return;
    }
    doJoin(v, bot.token);
  };

  useEffect(() => {
    if (!waiting || !bot.token) return;
    setWaiting(false);
    doJoin(email.trim(), bot.token);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [waiting, bot.token]);

  return (
    <form className="fr-card panel" onSubmit={submit} noValidate aria-label={`Lock in ${name}`}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
        <div aria-hidden="true" className="swatch" style={{ background: `linear-gradient(180deg, #FFFFFF 0 48%, #2A1250 48% 53%, ${shell} 53% 100%)` }} />
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <span className="eyebrow">YOUR RUSHLING</span>
          <h2 style={{ margin: 0 }}>{name}</h2>
        </div>
      </div>
      <label htmlFor="fr-email" className="disp">
        Your email
      </label>
      <div className="email-row">
      <input
        ref={input}
        id="fr-email"
        className="input"
        type="email"
        inputMode="email"
        autoComplete="email"
        placeholder="you@example.com"
        value={email}
        onChange={(e) => {
          setEmail(e.target.value);
          setError('');
        }}
        aria-invalid={!!error}
        aria-describedby="fr-email-err fr-privacy"
        disabled={busy}
        autoFocus
        required
      />
        <button type="submit" className="btn btn-join" disabled={busy}>
          {busy ? 'Joining…' : waiting ? 'Checking…' : 'Join the waitlist'}
        </button>
      </div>
      <div ref={bot.ref} className="turnstile" />
      <p id="fr-email-err" className="err" role="alert">
        {error || (waiting ? 'Quick check: tick “Verify you are human” above and you’re in.' : '')}
      </p>
      <p id="fr-privacy" className="privacy">
        We store your email, your Rushling and your invite code, only to tell you when Fall Rush opens. No spam, no wallet needed.{' '}
        <Link to="/privacy" target="_blank">
          Privacy
        </Link>
      </p>
      <button type="button" className="linkbtn" onClick={onBack} style={{ alignSelf: 'center' }}>
        Change Rushling
      </button>
    </form>
  );
}

function ShareCard({ spot, name, poster }: { spot: Spot; name: string; poster: ReturnType<typeof usePoster>['poster'] }) {
  const [copied, setCopied] = useState(false);
  const caption = shareCaption(name, spot.rank);
  const uploading = poster.status === 'making' || poster.status === 'uploading';

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(spot.shareUrl);
    } catch {
      /* ignore */
    }
    setCopied(true);
    setTimeout(() => setCopied(false), 1600);
  };

  return (
    <section className="fr-card panel share-panel" aria-label="Share your poster">
      <div className="xpost">
        <p>
          {caption} <span className="xlink">{spot.shareUrl.replace(/^https?:\/\//, '')}</span>
        </p>
        <PosterFrame poster={poster} alt={`Poster: ${name}, number ${fmt(spot.rank)} in line`} onRetry={() => undefined} />
      </div>
      <div className="row center-row">
        <a
          className="btn btn-s x big-x"
          href={xIntentUrl(caption, spot.shareUrl)}
          target="_blank"
          rel="noopener"
          aria-disabled={uploading}
          onClick={(e) => uploading && e.preventDefault()}
          style={uploading ? { opacity: 0.65, cursor: 'progress' } : undefined}
        >
          <XLogo />
          {uploading ? 'Preparing your poster…' : 'Share on X'}
        </a>
      </div>
      <div className="row">
        <label htmlFor="fr-ref" className="sr-only">
          Your invite link
        </label>
        <input id="fr-ref" className="field" readOnly value={spot.shareUrl} onFocus={(e) => e.currentTarget.select()} />
        <button type="button" className="btn btn-s" onClick={copy}>
          {copied ? 'Copied!' : 'Copy'}
        </button>
      </div>
      <p className="center" style={{ margin: 0, fontSize: 15 }}>
        Every friend who joins from your post moves <strong className="disp">{name}</strong> up 25 spots.
      </p>
      <Link to="/" className="linkbtn" style={{ alignSelf: 'center', display: 'inline-flex', alignItems: 'center' }}>
        Back to the course
      </Link>
    </section>
  );
}
