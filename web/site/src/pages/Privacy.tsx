import { useEffect } from 'react';
import { Link } from 'react-router-dom';

const CONTACT = String(import.meta.env.VITE_CONTACT_EMAIL || '');

export default function Privacy() {
  useEffect(() => {
    document.title = 'Privacy — Fall Rush waitlist';
  }, []);
  return (
    <div className="page privacy-page">
      <div className="ol" style={{ position: 'relative' }}>
        <header className="top">
          <Link to="/" className="brand" aria-label="Fall Rush home">
            <span className="word">
              FALL <span className="yellow">RUSH</span>
            </span>
          </Link>
          <Link to="/join" className="playfree btn">
            Join waitlist
          </Link>
        </header>
        <main className="privacy-main">
          <article className="fr-card privacy-card">
            <h1>Privacy</h1>
            <p className="small">Fall Rush waitlist · last updated 22 November 2026</p>

            <h2>What we store</h2>
            <ul>
              <li>Your email address</li>
              <li>The Rushling you picked, your invite code and your place in line</li>
              <li>Who invited you (their invite code) and how many friends joined with yours</li>
              <li>The time you joined, and your race poster image</li>
            </ul>

            <h2>Why</h2>
            <p>
              Only to keep your place in line and to tell you when Fall Rush opens. We don’t sell or share your email, and we don’t
              send marketing from anyone else.
            </p>

            <h2>Where it lives</h2>
            <p>
              On Cloudflare (database, file storage and hosting), sent over HTTPS. Your email is never shown publicly or returned by
              the site. Your poster is public at your share link, so it can appear in posts on X.
            </p>

            <h2>Bot protection</h2>
            <p>
              The join form uses Cloudflare Turnstile to check that you’re human. Cloudflare processes that check under its own{' '}
              <a href="https://www.cloudflare.com/turnstile-privacy-policy/" target="_blank" rel="noopener">
                Turnstile privacy policy
              </a>
              .
            </p>

            <h2>Your choices</h2>
            <p>
              You can ask us to delete your spot and email at any time
              {CONTACT ? (
                <>
                  {' '}
                  by writing to <a href={`mailto:${CONTACT}`}>{CONTACT}</a> from the address you signed up with.
                </>
              ) : (
                <> by contacting the Fall Rush team.</>
              )}{' '}
              No wallet is needed to join, and none is ever asked for.
            </p>

            <p style={{ marginTop: 18 }}>
              <Link to="/" className="linkbtn" style={{ padding: 0 }}>
                ← Back to the course
              </Link>
            </p>
          </article>
        </main>
      </div>
    </div>
  );
}
