// P7: the UI icons as inline SVG (same set as the game's code-drawn atlas, Client/UiIcons.cs): crown (our own: a band
// with five rounded capsule prongs and ball tips), lock, capsule, check, cross, bolt, swirl, eye, trophy, chip, link.
const S = (body: string, vb = '0 0 24 24') => `<svg viewBox="${vb}" aria-hidden="true" fill="currentColor">${body}</svg>`;
export const ICON = {
  crown: S('<rect x="3.5" y="14.5" width="17" height="4.5" rx="1.4"/><g stroke="currentColor" stroke-width="2.6" stroke-linecap="round"><path d="M5.5 15V9.8M8.8 15V8.2M12 15V6.4M15.2 15V8.2M18.5 15V9.8"/></g><circle cx="5.5" cy="8.6" r="1.8"/><circle cx="8.8" cy="7" r="1.8"/><circle cx="12" cy="5.2" r="1.9"/><circle cx="15.2" cy="7" r="1.8"/><circle cx="18.5" cy="8.6" r="1.8"/>'),
  lock: S('<rect x="5" y="10.5" width="14" height="10" rx="2.6"/><path d="M8 10.5V8a4 4 0 0 1 8 0v2.5" fill="none" stroke="currentColor" stroke-width="2.6"/><circle cx="12" cy="15" r="1.6" fill="#fff"/>'),
  capsule: S('<path d="M6.5 11.2V9a5.5 5.5 0 0 1 11 0v2.2z"/><path d="M6.5 12.8h11V15a5.5 5.5 0 0 1-11 0z" opacity=".55"/>'),
  check: S('<path d="M4.5 12.5l4.6 4.6L19.5 6.7" fill="none" stroke="currentColor" stroke-width="3.4" stroke-linecap="round" stroke-linejoin="round"/>'),
  cross: S('<path d="M6 6l12 12M18 6L6 18" fill="none" stroke="currentColor" stroke-width="3.2" stroke-linecap="round"/>'),
  bolt: S('<path d="M13.5 2.5L5 13.5h5.5l-1 8 8.5-11h-5.5z"/>'),
  swirl: S('<path d="M12 12m-1.6 0a1.6 1.6 0 1 0 3.2 0a3.6 3.6 0 1 0-7.2 0a5.6 5.6 0 1 0 11.2 0a7.6 7.6 0 1 0-15.2 0" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"/>'),
  eye: S('<path d="M2 12s3.8-6.5 10-6.5S22 12 22 12s-3.8 6.5-10 6.5S2 12 2 12z"/><circle cx="12" cy="12" r="3.6" fill="#fff"/><circle cx="12" cy="12" r="2"/>'),
  trophy: S('<path d="M7 3.5h10v5a5 5 0 0 1-10 0z"/><path d="M7 5H4.2a3 3 0 0 0 3 4.2M17 5h2.8a3 3 0 0 1-3 4.2" fill="none" stroke="currentColor" stroke-width="2"/><rect x="11" y="13" width="2" height="4.5"/><rect x="7.5" y="17.5" width="9" height="3" rx="1.2"/>'),
  chip: S('<path d="M12 2.5l8.2 4.75v9.5L12 21.5l-8.2-4.75v-9.5z"/><path d="M12 7.2l4.2 2.4v4.8L12 16.8l-4.2-2.4V9.6z" fill="#fff" opacity=".85"/>'),
  link: S('<path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/>'),
  tiles: S('<rect x="3.5" y="3.5" width="7.5" height="7.5" rx="2"/><rect x="13" y="3.5" width="7.5" height="7.5" rx="2" opacity=".55"/><rect x="3.5" y="13" width="7.5" height="7.5" rx="2" opacity=".55"/><rect x="13" y="13" width="7.5" height="7.5" rx="2"/>'),
  ball: S('<circle cx="12" cy="12" r="8"/><circle cx="9.2" cy="9" r="2.2" fill="#fff" opacity=".6"/>'),
  wind: S('<path d="M3 8.5h11a3 3 0 1 0-3-3M3 13h15a3 3 0 1 1-3 3M3 17.5h7" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"/>'),
  ring: S('<circle cx="12" cy="12" r="7.5" fill="none" stroke="currentColor" stroke-width="3.2"/><path d="M12 4.5v3" stroke="currentColor" stroke-width="3.2" stroke-linecap="round"/>'),
  play: S('<path d="M7 4.5v15l12.5-7.5z"/>'),
};
export type IconName = keyof typeof ICON;
export const icon = (n: IconName) => ICON[n];

/** 2D capsule art for an asset tile in its rarity colour (the Crown Capsule is gold with the crown on top). */
export function capsuleArt(color: string, crown = false): string {
  const top = crown ? '#fff8dc' : '#ffffff';
  return `<svg class="art" viewBox="0 0 64 76" aria-hidden="true">
    <ellipse cx="32" cy="71" rx="20" ry="4" fill="rgba(42,18,80,.22)"/>
    <path d="M8 38V30a24 24 0 0 1 48 0v8z" fill="${top}" stroke="#2a1250" stroke-width="3.5"/>
    <path d="M8 38h48v6a24 24 0 0 1-48 0z" fill="${color}" stroke="#2a1250" stroke-width="3.5"/>
    <rect x="6" y="35" width="52" height="6" rx="3" fill="#2a1250"/>
    <ellipse cx="20" cy="24" rx="5" ry="8" fill="rgba(79,166,236,.28)"/>
    <ellipse cx="44" cy="52" rx="7" ry="4" fill="rgba(255,255,255,.35)"/>
    ${crown ? `<g transform="translate(17 1) scale(.62)" fill="#ffb703" stroke="#2a1250" stroke-width="2.4"><rect x="3.5" y="14.5" width="17" height="4.5" rx="1.4"/><circle cx="5.5" cy="9" r="2.2"/><circle cx="12" cy="5.6" r="2.4"/><circle cx="18.5" cy="9" r="2.2"/></g>` : ''}
  </svg>`;
}
export const RARITY_COLOUR = ['#b9c0d4', '#4cc2ff', '#a77dff', '#ffd21f'];
