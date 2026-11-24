using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// G3.3 pooled particle systems for <see cref="Fx"/>: dust, dive streaks, the bonk (G4.8z: was hit stars), checkpoint ring, confetti, goo
    /// splash, tile debris, star sparkle, poof. Built in code on one material (FallRush/Fx + a 2×2 sheet); world space,
    /// manual bursts. Budget: ≤ 220 live particles per system, ≤ 700 in total; small, short-lived sprites (low overdraw).
    /// Lives with the map view (destroyed on scene change).
    /// </summary>
    public sealed class FxDirector : MonoBehaviour
    {
        private static FxDirector s_Instance;
        private ParticleSystem _dust, _streak, _puff, _ring, _confetti, _splash, _debris, _sparkle;
        private Material _mat;

        public static FxDirector Get()
        {
            if (s_Instance != null)
                return s_Instance;
            var mat = Resources.Load<Material>("FallRush/Fx");
            if (mat == null)
                return null;
            var go = new GameObject("FxDirector");
            s_Instance = go.AddComponent<FxDirector>();
            s_Instance._mat = mat;
            s_Instance.Build();
            return s_Instance;
        }

        private void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
        }

        private ParticleSystem Make(string name, int frame, int max, float gravity, bool stretch = false)
        {
            var go = new GameObject("Fx_" + name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = max;
            main.gravityModifier = gravity;
            main.startLifetime = 0.5f;
            main.startSpeed = 0f;
            main.startSize = 0.3f;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            var em = ps.emission;
            em.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;
            var sheet = ps.textureSheetAnimation;
            sheet.enabled = true;
            sheet.numTilesX = 2;
            sheet.numTilesY = 2;
            sheet.animation = ParticleSystemAnimationType.WholeSheet;
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
            sheet.startFrame = new ParticleSystem.MinMaxCurve(frame / 4f);
            sheet.cycleCount = 1;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = _mat;
            r.renderMode = stretch ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            if (stretch)
            {
                r.velocityScale = 0.12f;
                r.lengthScale = 1.5f;
            }

            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortingFudge = 1f;
            return ps;
        }

        private void Build()
        {
            _dust = Make("Dust", 0, 160, -0.05f);
            var sz = _dust.sizeOverLifetime;
            sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.6f, 1f, 1.6f));
            _streak = Make("Streak", 0, 80, 0f, stretch: true);
            // G4.8z bonk puff (soft dot, grows and fades fast). The sparks reuse the streak system (stretched dots).
            _puff = Make("Bonk", 0, 40, -0.05f);
            var ps = _puff.sizeOverLifetime;
            ps.enabled = true;
            ps.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.5f, 1f, 1.5f));
            _ring = Make("Ring", 3, 12, 0f);
            var rs = _ring.sizeOverLifetime;
            rs.enabled = true;
            rs.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.3f, 1f, 1f));
            _confetti = Make("Confetti", 2, 220, 0.45f);
            var cr = _confetti.rotationOverLifetime;
            cr.enabled = true;
            cr.z = new ParticleSystem.MinMaxCurve(-6f, 6f);
            var drag = _confetti.limitVelocityOverLifetime;
            drag.enabled = true;
            drag.drag = 1.4f;
            _splash = Make("Splash", 0, 80, 1.2f);
            _debris = Make("Debris", 2, 120, 2.2f);
            var dr = _debris.rotationOverLifetime;
            dr.enabled = true;
            dr.z = new ParticleSystem.MinMaxCurve(-5f, 5f);
            _sparkle = Make("Sparkle", 1, 80, -0.1f);
        }

        private static bool Reduced => GameSettings.ReducedMotion;

        private static void Burst(ParticleSystem ps, Vector3 at, int n, float speed, float up, float size, float life, Color c, float spread = 1f)
        {
            if (ps == null)
                return;
            var p = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (var i = 0; i < n; i++)
            {
                var a = Random.value * Mathf.PI * 2f;
                var h = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (speed * (0.5f + Random.value * 0.5f));
                p.position = at + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (0.1f * spread);
                p.velocity = h + Vector3.up * (up * (0.6f + Random.value * 0.6f));
                p.startSize = size * (0.7f + Random.value * 0.6f);
                p.startLifetime = life * (0.75f + Random.value * 0.5f);
                p.startColor = c;
                p.rotation = Random.value * 360f;
                ps.Emit(p, 1);
            }
        }

        public void Dust(Vector3 at, float impact)
        {
            var n = Mathf.RoundToInt(Mathf.Lerp(4f, 14f, impact) * (Reduced ? 0.5f : 1f));
            Burst(_dust, at + Vector3.up * 0.05f, n, 1.2f + impact * 1.8f, 0.4f, 0.28f + impact * 0.22f, 0.45f, new Color(0.97f, 0.95f, 1f, 0.85f));
        }

        public void SlideDust(Vector3 at) => Burst(_dust, at + Vector3.up * 0.04f, Reduced ? 1 : 2, 0.6f, 0.3f, 0.22f, 0.35f, new Color(0.97f, 0.95f, 1f, 0.7f));

        public void Streaks(Vector3 at, Vector3 dir)
        {
            if (Reduced || _streak == null)
                return;
            var p = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (var i = 0; i < 8; i++)
            {
                p.position = at + Vector3.up * (0.25f + Random.value * 0.6f) + Random.insideUnitSphere * 0.25f;
                p.velocity = -dir * (5f + Random.value * 3f);
                p.startSize = 0.07f;
                p.startLifetime = 0.25f;
                p.startColor = new Color(1f, 1f, 1f, 0.85f);
                _streak.Emit(p, 1);
            }
        }

        /// <summary>
        /// Hit (dizzy hits, 2026-11-24): a few tiny yellow twinkles pop out round the head. No white puff and no white sparks
        /// any more: under the bloom they made a big white starburst that hid the flopped Rushling for the whole stun (it read
        /// as "vanished"). The dizzy stars over the head (RushlingParts.DizzyStars) are the hit sign.
        /// </summary>
        public void Bonk(Vector3 at)
        {
            if (_sparkle == null)
                return;
            var c = at + Vector3.up * 1.25f;
            var p = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            var n = BonkSparks(Reduced);
            for (var i = 0; i < n; i++)
            {
                var a = (i + Random.value * 0.5f) / n * Mathf.PI * 2f;
                var d = new Vector3(Mathf.Cos(a), 0.35f, Mathf.Sin(a)).normalized;
                p.position = c + d * 0.2f;
                p.velocity = d * (1.4f + Random.value * 0.6f);
                p.startSize = 0.11f;
                p.startLifetime = 0.35f;
                p.startColor = new Color(1f, 0.88f, 0.3f, 1f);
                p.rotation = Random.value * 360f;
                _sparkle.Emit(p, 1);
            }
        }

        /// <summary>Sparks per bonk: 5 at full motion, 3 with reduced motion (within the 3–5 rule).</summary>
        public static int BonkSparks(bool reduced) => 0; // 2026-11-25 (user): no stars at all on a hit — the hit is the dizzy, tired look

        /// <summary>Puffs per bonk: none.</summary>
        public static int BonkPuffs(bool reduced) => 0; // no white puff (it hid the hit Rushling)

        public void Ring(Vector3 at)
        {
            if (_ring == null)
                return;
            var p = new ParticleSystem.EmitParams { applyShapeToPosition = false, position = at + Vector3.up * 0.8f, velocity = Vector3.zero, startSize = Reduced ? 2.2f : 3.6f, startLifetime = 0.55f, startColor = UiKit.Mint };
            _ring.Emit(p, 1);
            Burst(_sparkle, at + Vector3.up * 0.8f, Reduced ? 3 : 8, 2.5f, 1.5f, 0.22f, 0.5f, UiKit.Mint);
        }

        private static readonly Color[] Party = { UiKit.Magenta, UiKit.SunYellow, UiKit.Mint, UiKit.Sky, UiKit.Grape, Color.white };

        public void Confetti(Vector3 at, int count)
        {
            if (_confetti == null)
                return;
            var n = Reduced ? count / 4 : count;
            var p = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (var i = 0; i < n; i++)
            {
                var a = Random.value * Mathf.PI * 2f;
                p.position = at + Vector3.up * 0.5f;
                p.velocity = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (1.5f + Random.value * 3.5f) + Vector3.up * (4f + Random.value * 4f);
                p.startSize = 0.12f + Random.value * 0.08f;
                p.startLifetime = 1.6f + Random.value * 0.8f;
                p.startColor = Party[i % Party.Length];
                p.rotation = Random.value * 360f;
                _confetti.Emit(p, 1);
            }
        }

        public void Splash(Vector3 at) => Burst(_splash, at, Reduced ? 6 : 18, 2.4f, 5f, 0.35f, 0.7f, UiKit.Hex(0xB98CFF));

        public void Debris(Vector3 at, Color c) => Burst(_debris, at, Reduced ? 3 : 8, 1.6f, 2.5f, 0.22f, 0.8f, c, 3f);

        public void Sparkle(Vector3 at, bool gold) => Burst(_sparkle, at, gold ? (Reduced ? 5 : 14) : (Reduced ? 3 : 7), gold ? 3f : 2f, 2f, gold ? 0.32f : 0.24f, 0.55f, gold ? UiKit.Hex(0xFFB020) : UiKit.SunYellow);

        public void Poof(Vector3 at) => Burst(_dust, at + Vector3.up * 0.4f, Reduced ? 4 : 10, 1.8f, 1f, 0.45f, 0.5f, new Color(1f, 1f, 1f, 0.9f));
    }
}
