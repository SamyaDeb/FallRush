/** The Rushling pit section: a physics toy box (lazy: Rapier loads only when this comes near). */
import { Suspense, lazy, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import type { PitHandle } from '../three/PitScene';

const PitScene = lazy(() => import('../three/PitScene'));

export default function PitSection({ webgl }: { webgl: boolean }) {
  const sec = useRef<HTMLElement>(null);
  const pit = useRef<PitHandle>(null);
  const [near, setNear] = useState(false);
  const [ready, setReady] = useState(false);
  useEffect(() => {
    const el = sec.current;
    if (!el || !webgl) return;
    const io = new IntersectionObserver((e) => e[0].isIntersecting && setNear(true), { rootMargin: '80% 0px' });
    io.observe(el);
    return () => io.disconnect();
  }, [webgl]);
  return (
    <section ref={sec} className={'pit' + (ready ? ' ready' : '')} aria-label="The Rushling pit (a toy)">
      {webgl && near && (
        <Suspense fallback={null}>
          <PitScene ref={pit} onReady={() => setReady(true)} />
        </Suspense>
      )}
      <div className="pit-copy">
        <span className="st-eyebrow">INTERMISSION</span>
        <h2>The Rushling pit</h2>
        <p>{webgl ? 'Grab one. Fling it. Bonk the gumballs. That is the whole game, roughly.' : 'Twelve wind-up toys, one toy box, a lot of bonking.'}</p>
        <div className="row">
          {webgl && (
            <button type="button" className="btn btn-s pink mag" onClick={() => pit.current?.shake()} disabled={!ready}>
              Shake the box!
            </button>
          )}
          <Link to="/join" className="btn btn-s mag">
            Pick your Rushling →
          </Link>
        </div>
      </div>
    </section>
  );
}
