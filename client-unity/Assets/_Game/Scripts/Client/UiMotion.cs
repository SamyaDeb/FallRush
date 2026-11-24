using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// P7 A1.1: the shared UI tween helper (no package, no tween library). Every panel, card and row asks for
    /// <see cref="Show"/> / <see cref="Hide"/> / <see cref="SetVisible"/> instead of <c>SetActive</c>; the motion is played by
    /// a small <see cref="UiAnim"/> component added once per target (allocation-free per frame, unscaled time).
    /// <list type="bullet">
    /// <item>Enter: scale 0.92 → 1 with a back-out overshoot + alpha 0 → 1, 0.22 s (rows: + 40 ms stagger per index).</item>
    /// <item>Exit: scale 1 → 0.95 + alpha → 0, 0.15 s, then inactive.</item>
    /// <item>Bump: scale 1 → 1.12 → 1, 0.18 s. Slide-in: from an edge offset, 0.25 s ease-out. Shake: a refused action.</item>
    /// <item>Count-up: ease-out, at most 0.6 s (<see cref="CountUp"/>).</item>
    /// </list>
    /// <see cref="GameSettings.ReducedMotion"/>: fades only (no scale, slide or shake).
    /// </summary>
    public static class UiMotion
    {
        public const float EnterSeconds = 0.22f, ExitSeconds = 0.15f, StaggerSeconds = 0.04f, BumpSeconds = 0.18f, SlideSeconds = 0.25f, ShakeSeconds = 0.32f, CountMax = 0.6f;
        public const float EnterFrom = 0.92f, ExitTo = 0.95f, BumpPeak = 0.12f, ShakePixels = 14f;

        // ------------------------------------------------------------------ curves (pure; tested)

        /// <summary>Back-out ease (overshoots ~10 % then settles at 1): 0 → 0, 1 → 1.</summary>
        public static float BackOut(float t)
        {
            t = Mathf.Clamp01(t);
            const float c1 = 1.70158f, c3 = c1 + 1f;
            var u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        /// <summary>Cubic ease-out.</summary>
        public static float EaseOut(float t)
        {
            t = 1f - Mathf.Clamp01(t);
            return 1f - t * t * t;
        }

        /// <summary>Enter scale at normalised time t (0.92 → 1 with overshoot).</summary>
        public static float EnterScale(float t) => Mathf.LerpUnclamped(EnterFrom, 1f, BackOut(t));

        /// <summary>Bump scale: 1 → 1.12 at t = 0.35 → 1 at t = 1.</summary>
        public static float BumpScale(float t)
        {
            t = Mathf.Clamp01(t);
            var k = t < 0.35f ? EaseOut(t / 0.35f) : 1f - EaseOut((t - 0.35f) / 0.65f);
            return 1f + BumpPeak * k;
        }

        /// <summary>Shake offset in pixels: a decaying sine, 0 at both ends.</summary>
        public static float ShakeOffset(float t)
        {
            t = Mathf.Clamp01(t);
            return Mathf.Sin(t * Mathf.PI * 7f) * ShakePixels * (1f - t);
        }

        /// <summary>The count-up duration for a change of <paramref name="delta"/>: short for small changes, ≤ 0.6 s.</summary>
        public static float CountSeconds(double delta) => Mathf.Clamp(0.18f + 0.09f * Mathf.Log10(1f + (float)System.Math.Abs(delta)), 0.18f, CountMax);

        // ------------------------------------------------------------------ verbs

        public static UiAnim Of(RectTransform rt)
        {
            var a = rt.GetComponent<UiAnim>();
            return a != null ? a : rt.gameObject.AddComponent<UiAnim>();
        }

        /// <summary>Activates and plays the enter motion (after <paramref name="delay"/> seconds).</summary>
        public static void Show(RectTransform rt, float delay = 0f) => Of(rt).Enter(delay);

        /// <summary>Plays the exit motion, then deactivates.</summary>
        public static void Hide(RectTransform rt)
        {
            if (rt.gameObject.activeSelf)
                Of(rt).Exit();
        }

        /// <summary>Show / Hide only on a change of the wanted state (cheap to call every refresh).</summary>
        public static void SetVisible(RectTransform rt, bool on, float delay = 0f)
        {
            var a = rt.GetComponent<UiAnim>();
            var shown = a != null ? a.Wanted && rt.gameObject.activeSelf : rt.gameObject.activeSelf;
            if (on == shown)
                return; // already shown / entering, or already hidden / exiting
            if (on)
                Show(rt, delay);
            else
                Hide(rt);
        }

        /// <summary>True while shown or entering (false while hidden or exiting).</summary>
        public static bool IsShown(RectTransform rt)
        {
            var a = rt.GetComponent<UiAnim>();
            return rt.gameObject.activeSelf && (a == null || a.Wanted);
        }

        /// <summary>Row stagger: enter after index × 40 ms.</summary>
        public static void Stagger(RectTransform rt, int index) => Show(rt, index * StaggerSeconds);

        public static void Bump(RectTransform rt) => Of(rt).Bump();

        public static void Shake(RectTransform rt) => Of(rt).Shake();

        /// <summary>Slides in from <paramref name="from"/> (an anchored-position offset, e.g. (0, 160) = from above).</summary>
        public static void SlideIn(RectTransform rt, Vector2 from) => Of(rt).Slide(from);

        /// <summary>Visual-only offset that eases back to 0 (leaderboard rank slide).</summary>
        public static void SlideFrom(RectTransform rt, Vector2 from) => Of(rt).Slide(from, false);
    }

    /// <summary>A number that counts up (ease-out, ≤ 0.6 s, unscaled time) toward its target. A struct: no allocation.</summary>
    public struct CountUp
    {
        private double _from, _to;
        private float _t0, _dur;
        private bool _set;

        public double Target => _to;

        public void Set(double target, float now)
        {
            if (!_set)
            {
                _from = _to = target;
                _set = true;
                _dur = 0f;
                return;
            }

            if (target == _to)
                return;
            _from = Value(now);
            _to = target;
            _t0 = now;
            _dur = GameSettings.ReducedMotion ? 0f : UiMotion.CountSeconds(target - _from);
        }

        public double Value(float now)
        {
            if (_dur <= 0f)
                return _to;
            var u = UiMotion.EaseOut((now - _t0) / _dur);
            return _from + (_to - _from) * u;
        }

        public bool Running(float now) => _dur > 0f && now - _t0 < _dur;
    }
}
