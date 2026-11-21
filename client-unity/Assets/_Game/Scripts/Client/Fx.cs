using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// G3.3 effects facade (doc 17 §4 feel checklist). Client-only visuals: nothing here touches the simulation, the
    /// tick, input or prediction. Pooled particle systems (one draw each when alive), every effect honours reduced motion
    /// (fewer, slower particles; confetti and streaks off).
    /// </summary>
    public static class Fx
    {
        public static void Dust(Vector3 at, float impact) => FxDirector.Get()?.Dust(at, impact);
        public static void Streaks(Vector3 at, Vector3 dir) => FxDirector.Get()?.Streaks(at, dir);
        public static void SlideDust(Vector3 at) => FxDirector.Get()?.SlideDust(at);
        public static void HitStars(Vector3 at) => FxDirector.Get()?.HitStars(at);
        public static void Ring(Vector3 at) => FxDirector.Get()?.Ring(at);
        public static void Confetti(Vector3 at, int count = 70) => FxDirector.Get()?.Confetti(at, count);
        public static void Splash(Vector3 at) => FxDirector.Get()?.Splash(at);
        public static void Debris(Vector3 at, Color c) => FxDirector.Get()?.Debris(at, c);
        public static void Sparkle(Vector3 at, bool gold) => FxDirector.Get()?.Sparkle(at, gold);
        public static void Poof(Vector3 at) => FxDirector.Get()?.Poof(at);
    }
}
