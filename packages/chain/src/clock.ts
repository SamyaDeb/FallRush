// Injectable time + timers so the transports are unit-testable with a fake clock.
export interface Clock {
  now(): number; // ms, monotonic (performance.now() in the browser)
  wallMs(): number; // Date.now()
  setTimeout(fn: () => void, ms: number): unknown;
  clearTimeout(h: unknown): void;
  setInterval(fn: () => void, ms: number): unknown;
  clearInterval(h: unknown): void;
}

export const realClock: Clock = {
  now: () => (typeof performance !== 'undefined' ? performance.now() : Date.now()),
  wallMs: () => Date.now(),
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (h) => clearTimeout(h as ReturnType<typeof setTimeout>),
  setInterval: (fn, ms) => setInterval(fn, ms),
  clearInterval: (h) => clearInterval(h as ReturnType<typeof setInterval>),
};

export function sleep(clock: Clock, ms: number): Promise<void> {
  return new Promise((r) => clock.setTimeout(r, ms));
}
