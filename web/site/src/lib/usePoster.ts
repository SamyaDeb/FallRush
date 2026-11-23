import { useCallback, useEffect, useRef, useState } from 'react';
import { PLAYABLE, presetByName } from './presets';
import { API_BASE, SITE_HOST, saveSpot, uploadPoster, type Spot } from './waitlist';

export type PosterState = { status: 'idle' | 'making' | 'uploading' | 'ready' | 'error'; url?: string; blob?: Blob; note?: string };

import { loadPosterModule as loadPoster } from './preload';

/** Draws the personal poster (lazy three.js), uploads it, and keeps an object URL for preview / download / share. */
export function usePoster(onSpot?: (s: Spot) => void) {
  const [poster, setPoster] = useState<PosterState>({ status: 'idle' });
  const urlRef = useRef<string | undefined>(undefined);
  const cb = useRef(onSpot);
  cb.current = onSpot;
  useEffect(() => () => {
    if (urlRef.current) URL.revokeObjectURL(urlRef.current);
  }, []);

  const make = useCallback(async (s: Spot) => {
    const p = presetByName(s.rushling) ?? PLAYABLE[0];
    setPoster({ status: 'making' });
    try {
      const { generatePoster } = await loadPoster();
      const blob = await generatePoster({ preset: p, rank: s.rank, siteHost: SITE_HOST });
      if (urlRef.current) URL.revokeObjectURL(urlRef.current);
      const url = URL.createObjectURL(blob);
      urlRef.current = url;
      if (!s.token) {
        setPoster({ status: 'ready', url, blob });
        return;
      }
      setPoster({ status: 'uploading', url, blob });
      await uploadPoster(s, blob);
      const next = { ...s, hasPoster: true, posterRank: s.rank };
      saveSpot(next);
      cb.current?.(next);
      setPoster({ status: 'ready', url, blob });
    } catch (e) {
      console.warn('poster', e);
      setPoster((cur) => ({ ...cur, status: cur.url ? 'ready' : 'error', note: 'Your poster couldn’t upload, so your post will show the standard Fall Rush card.' }));
    }
  }, []);

  /** Use the uploaded poster when it's current, otherwise re-make it. */
  const fromSaved = useCallback(
    async (s: Spot) => {
      if (s.hasPoster && (!s.token || s.posterRank === s.rank)) {
        const url = `${API_BASE}/p/${s.code}.png?r=${s.posterRank ?? s.rank}`;
        setPoster({ status: 'ready', url });
        try {
          const blob = await (await fetch(url)).blob();
          if (blob.type === 'image/png') setPoster({ status: 'ready', url, blob });
        } catch {
          /* preview still works from the URL */
        }
      } else make(s);
    },
    [make],
  );

  return { poster, make, fromSaved };
}
