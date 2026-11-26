/** The last screen: two separate doors (play the live beta / join the waitlist), live numbers, recent matches, footer. */
import { Link } from 'react-router-dom';
import { ARENAS_URL, LIVE_URL, PLAY_URL, receiptUrl, replayUrl } from '../lib/links';
import { shortKey, type Live } from '../lib/live';

export default function Finale({ live }: { live: Live }) {
  const matches = live.matches.slice(0, 10);
  return (
    <section className="finale" aria-label="Play Fall Rush">
      <div className="cloud-edge" aria-hidden="true" />
      <div className="fin-inner">
        <header className="fin-head">
          <span className="st-eyebrow light">THE NEXT SHOW STARTS WHEN YOU DO</span>
          <h2 className="fin-title">
            <span className="word">Your</span> <span className="word yellow">move.</span>
          </h2>
          <p>Two doors. Play the live private beta right now, or save your spot for launch. Or both.</p>
        </header>
        <div className="fin-doors">
          <div className="door play">
            <span className="st-eyebrow">PRIVATE BETA · LIVE NOW</span>
            <h2>Play it now</h2>
            <p>The beta is open to everyone on Solana devnet while the judges look. Grab a guest Rushling and queue in a few seconds. No wallet, no popups.</p>
            <a className="btn btn-play mag" href={PLAY_URL}>
              ▶ Play the beta
            </a>
            <a className="fin-sub" href={LIVE_URL}>
              or watch a live arena →
            </a>
          </div>
          <div className="door wait">
            <span className="st-eyebrow">LAUNCH WAITLIST</span>
            <h2>Save your spot</h2>
            <p>Pick a Rushling, get your place in line and a poster to share. Every friend who joins with your link moves you up 25 spots.</p>
            <Link className="btn btn-join mag" to="/join">
              Join the waitlist
            </Link>
          </div>
        </div>

        {live.ok && (
          <div className="fin-live">
            <div className="stat">
              <b>{live.arenasUp}</b>
              <span>{live.arenasUp === 1 ? 'arena up' : 'arenas up'}</span>
            </div>
            <div className="stat">
              <b>{live.humans}</b>
              <span>playing now</span>
            </div>
            <div className="stat">
              <b>{live.finalTick != null ? live.finalTick : '—'}</b>
              <span>{live.finalTick != null ? 'on-chain tick, live' : 'no on-chain match running'}</span>
            </div>
            <a className="stat link" href={ARENAS_URL}>
              <b>→</b>
              <span>all arenas</span>
            </a>
          </div>
        )}

        {matches.length > 0 && (
          <div className="marquee" aria-label="Recent matches">
            <div className="track">
              {[...matches, ...matches].map((m, i) => (
                <span key={i} className="mrow" aria-hidden={i >= matches.length}>
                  <b>{m.champion != null && m.champion < 12 ? `👑 slot ${m.champion + 1}${(m.humanMask >> m.champion) & 1 ? '' : ' (bot)'}` : 'match'}</b>
                  <span className="mono">{shortKey(m.match)}</span>
                  <a href={receiptUrl(m.match)} tabIndex={i >= matches.length ? -1 : 0}>
                    receipt
                  </a>
                  {m.replay && (
                    <a href={replayUrl(m.match)} tabIndex={i >= matches.length ? -1 : 0}>
                      replay
                    </a>
                  )}
                </span>
              ))}
            </div>
          </div>
        )}

        <footer className="fin-foot">
          <span className="word small">
            FALL <span className="yellow">RUSH</span>
          </span>
          <span>Every round is computed by the program on a MagicBlock Ephemeral Rollup (if the rollup is down, a match falls back to our game server and the receipt says so). Devnet test chips only, no real-money value.</span>
          <nav>
            <Link to="/privacy">Privacy</Link>
            <a href={ARENAS_URL}>Arenas</a>
            <a href={LIVE_URL}>Live</a>
            <a
              href="#top"
              onClick={(e) => {
                e.preventDefault();
                window.scrollTo({ top: 0, behavior: 'smooth' });
              }}
            >
              Back to top ↑
            </a>
          </nav>
        </footer>
      </div>
    </section>
  );
}
