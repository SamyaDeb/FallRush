/**
 * Scroll → story progress. One ScrollTrigger over the tall story section writes a raw 0..1 value; the 3D scene eases
 * toward it every frame (so wheel jumps glide), and DOM timelines scrub on the same trigger.
 */
import { useEffect, useRef } from 'react';
import { gsap } from 'gsap';
import { ScrollTrigger } from 'gsap/ScrollTrigger';
import { SplitText } from 'gsap/SplitText';

gsap.registerPlugin(ScrollTrigger, SplitText);
export { gsap, ScrollTrigger, SplitText };

export interface Progress {
  /** raw scroll progress through the story section, 0..1 */
  raw: number;
  /** eased copy the 3D scene uses (updated by the scene) */
  p: number;
  /** scroll velocity (px/s), for motion blur / tilt effects */
  vel: number;
}

export function useStoryProgress(section: React.RefObject<HTMLElement | null>) {
  const prog = useRef<Progress>({ raw: 0, p: 0, vel: 0 });
  useEffect(() => {
    const el = section.current;
    if (!el) return;
    const st = ScrollTrigger.create({
      trigger: el,
      start: 'top top',
      end: 'bottom bottom',
      onUpdate: (self) => {
        prog.current.raw = self.progress;
        prog.current.vel = self.getVelocity();
      },
    });
    prog.current.raw = st.progress;
    return () => st.kill();
  }, [section]);
  return prog;
}

/** 0 → 1 as `p` goes from `a` to `b` (clamped). */
export const span = (p: number, a: number, b: number) => (p <= a ? 0 : p >= b ? 1 : (p - a) / (b - a));
/** smoothstep */
export const ease = (t: number) => t * t * (3 - 2 * t);
export const easeOut = (t: number) => 1 - Math.pow(1 - t, 3);
export const easeIn = (t: number) => t * t * t;
/** back-out overshoot (pop-ins) */
export const backOut = (t: number, s = 1.9) => 1 + (s + 1) * Math.pow(t - 1, 3) + s * Math.pow(t - 1, 2);
