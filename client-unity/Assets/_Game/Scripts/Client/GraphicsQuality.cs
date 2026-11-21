using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FallRush.Client
{
    /// <summary>
    /// G2.1 "Graphics: High / Low" (Settings), G4.6 grade. One global post volume, created at runtime (no scene carries it):
    /// <list type="bullet">
    /// <item>the colour grade on <b>both</b> levels: Neutral tonemapping, Colour Adjustments saturation +22, contrast +12,
    /// post-exposure −0.15 and a pre-tonemap gain (<see cref="PreTonemapGain"/>, the colour filter) that keeps the candy tops at
    /// 85–95 % value after the tonemapper (decision G4.6-3). Tonemapping needs URP's HDR grading mode, which
    /// <see cref="Apply"/> sets on the active pipeline asset;</item>
    /// <item>High adds Bloom (threshold 1.15: lit tops stay under it) and the subtle Vignette, at full render scale;</item>
    /// <item>Low drops Bloom + Vignette only and keeps the WebGL quality level's 0.8 render scale (G4.6: Low no longer turns
    /// post off, so the palette reads the same on both levels; cost: one uber post pass, decision G4.6-4).</item>
    /// </list>
    /// </summary>
    public static class GraphicsQuality
    {
        /// <summary>Linear gain before the Neutral tonemapper (measured on Editor shots: tops V 0.85–0.95 after the grade).</summary>
        public const float PreTonemapGain = 1.6f;

        public const float Saturation = 22f, Contrast = 12f, PostExposure = -0.15f, BloomThreshold = 1.15f;

        /// <summary>The map sun's intensity (MapArt sets it; the albedo bake below assumes it).</summary>
        public const float SunIntensity = 1.2f;

        /// <summary>Upper bound of a baked albedo channel (keeps pale / white parts under the bloom threshold).</summary>
        public const float MaxAlbedo = 1.25f;

        // ------------------------------------------------------------------ CPU model of the grade (G4.6-3)
        // The URP HDR chain for this profile: × 2^postExposure → LogC contrast about ACEScc mid grey → × colour filter (gain)
        // → saturation about Rec. 709 luma → Neutral tonemap → sRGB. Checked against Editor shot pixels (≤ 3 / 255).

        private const float LcCut = 0.011361f, LcA = 5.555556f, LcB = 0.047996f, LcC = 0.244161f, LcD = 0.386036f, LcE = 5.301883f, LcF = 0.092819f;
        private const float MidGrey = 0.4135884f;

        private static float ToLogC(float x) => x > LcCut ? LcC * Mathf.Log10(LcA * x + LcB) + LcD : LcE * x + LcF;

        private static float FromLogC(float y) => y > LcE * LcCut + LcF ? (Mathf.Pow(10f, (y - LcD) / LcC) - LcB) / LcA : (y - LcF) / LcE;

        private static float NeutralCurve(float x)
        {
            const float a = 0.2f, b = 0.29f, c = 0.24f, d = 0.272f, e = 0.02f, f = 0.3f;
            return (x * (a * x + c * b) + d * e) / (x * (a * x + b) + d * f) - e / f;
        }

        private static float Neutral(float x)
        {
            var ws = 1f / NeutralCurve(5.3f);
            return NeutralCurve(x * ws) * ws;
        }

        private static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        /// <summary>The grade of a linear scene colour → the sRGB colour on screen.</summary>
        public static Color Grade(Color lin)
        {
            var k = 1f + Contrast / 100f;
            var ex = Mathf.Pow(2f, PostExposure);
            float Pre(float v) => Mathf.Max(0f, FromLogC((ToLogC(Mathf.Max(v * ex, 1e-6f)) - MidGrey) * k + MidGrey) * PreTonemapGain);
            var x = new Color(Pre(lin.r), Pre(lin.g), Pre(lin.b));
            var l = Luma(x);
            var s = 1f + Saturation / 100f;
            float Sat(float v) => Mathf.Max(0f, l + (v - l) * s);
            return new Color(Mathf.LinearToGammaSpace(Mathf.Clamp01(Neutral(Sat(x.r)))), Mathf.LinearToGammaSpace(Mathf.Clamp01(Neutral(Sat(x.g)))),
                Mathf.LinearToGammaSpace(Mathf.Clamp01(Neutral(Sat(x.b)))));
        }

        /// <summary>The linear scene colour the grade turns into <paramref name="srgb"/> (exact inverse where it exists).</summary>
        public static Color InverseGrade(Color srgb)
        {
            float ITm(float t)
            {
                float lo = 0f, hi = 5.3f;
                for (var i = 0; i < 48; i++)
                {
                    var m = (lo + hi) * 0.5f;
                    if (Neutral(m) < t)
                        lo = m;
                    else
                        hi = m;
                }

                return (lo + hi) * 0.5f;
            }

            var y = new Color(ITm(Mathf.Min(0.995f, Mathf.GammaToLinearSpace(srgb.r))), ITm(Mathf.Min(0.995f, Mathf.GammaToLinearSpace(srgb.g))),
                ITm(Mathf.Min(0.995f, Mathf.GammaToLinearSpace(srgb.b))));
            var l = Luma(y);
            var s = 1f + Saturation / 100f;
            var k = 1f + Contrast / 100f;
            var ex = Mathf.Pow(2f, PostExposure);
            float Back(float v)
            {
                var x = Mathf.Max(1e-6f, l + (v - l) / s) / PreTonemapGain;
                return FromLogC((ToLogC(x) - MidGrey) / k + MidGrey) / ex;
            }

            return new Color(Back(y.r), Back(y.g), Back(y.b));
        }

        /// <summary>Linear light on a sun-facing top under the map light (toon shader: sun × 0.58 + sky ambient × 0.34).</summary>
        public static Color TopLight(Color sunSrgb, Color ambientSkySrgb) => sunSrgb.linear * (SunIntensity * 0.58f) + ambientSkySrgb.linear * 0.34f;

        /// <summary>
        /// G4.6-3: the linear albedo (vertex colour, or the linearised material colour) that shows <paramref name="srgb"/> on a
        /// lit top after the grade: the palette is authored as on-screen colours, the bake undoes the grade and the light.
        /// </summary>
        public static Color AlbedoFor(Color srgb, Color light)
        {
            var g = InverseGrade(srgb);
            return new Color(Mathf.Min(MaxAlbedo, g.r / light.r), Mathf.Min(MaxAlbedo, g.g / light.g), Mathf.Min(MaxAlbedo, g.b / light.b), srgb.a);
        }

        private static Volume s_Volume;
        private static float s_BaseScale = -1f;
        private static bool s_High = true;

        public static void Apply(Camera cam)
        {
            var high = GameSettings.GraphicsHigh;
            // Measurement override: ?gfx=low|high (WebGL) or -fallrushGfx low|high.
            var q = FallRush.Shared.NetTargets.GetQueryParam(Application.absoluteURL, "gfx") ?? Arg("-fallrushGfx");
            if (q == "low" || q == "high")
                high = q == "high";
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                if (s_BaseScale < 0f)
                    s_BaseScale = urp.renderScale;
                urp.renderScale = high ? 1f : Mathf.Min(s_BaseScale, 0.8f);
                urp.colorGradingMode = ColorGradingMode.HighDynamicRange; // Neutral tonemapping is HDR-grading only
            }

            if (cam != null)
                cam.GetUniversalAdditionalCameraData().renderPostProcessing = true; // both levels keep the grade
            EnsureVolume();
            SetLevel(s_Volume, high);
            FallRush.Net.GateLogger.Gfx = high ? "high" : "low";
        }

        private static string Arg(string name)
        {
            var a = System.Environment.GetCommandLineArgs();
            for (var i = 0; i < a.Length - 1; i++)
                if (a[i] == name)
                    return a[i + 1].ToLowerInvariant();
            return null;
        }

        /// <summary>The level actually in use (after the URL / command-line override), for the gate log.</summary>
        public static bool IsHigh => s_Volume != null && s_High;

        private static void EnsureVolume()
        {
            if (s_Volume != null)
                return;
            s_Volume = CreateVolume(true);
        }

        /// <summary>The global post volume (also used by the Editor proof shots; <paramref name="persistent"/> only in play mode).</summary>
        public static Volume CreateVolume(bool persistent)
        {
            var go = new GameObject("PostVolume");
            if (persistent && Application.isPlaying)
                Object.DontDestroyOnLoad(go);
            var v = go.AddComponent<Volume>();
            v.isGlobal = true;
            v.priority = 10f;
            v.sharedProfile = BuildProfile();
            return v;
        }

        /// <summary>High: the whole volume. Low: the grade only (Tonemapping + Colour Adjustments), no Bloom, no Vignette.</summary>
        public static void SetLevel(Volume v, bool high)
        {
            s_High = high;
            if (v == null)
                return;
            v.enabled = true;
            if (v.sharedProfile.TryGet<Bloom>(out var bloom))
                bloom.active = high;
            if (v.sharedProfile.TryGet<Vignette>(out var vig))
                vig.active = high;
        }

        /// <summary>Editor proof shots: the runtime post volume at a level (not persistent).</summary>
        public static GameObject CreateShotVolume(bool high)
        {
            var v = CreateVolume(false);
            SetLevel(v, high);
            return v.gameObject;
        }

        public static void DestroyShotVolume(GameObject go)
        {
            var v = go != null ? go.GetComponent<Volume>() : null;
            if (v != null)
                Object.DestroyImmediate(v.sharedProfile);
            Object.DestroyImmediate(go);
        }

        /// <summary>Editor proof shots: a camera that renders the post volume.</summary>
        public static void PrepareShotCamera(Camera cam)
        {
            var d = cam.GetUniversalAdditionalCameraData();
            d.renderPostProcessing = true;
            d.antialiasing = AntialiasingMode.None;
        }

        /// <summary>Editor cost proxy: post on / off for a camera.</summary>
        public static void SetPostOnCamera(Camera cam, bool on) => cam.GetUniversalAdditionalCameraData().renderPostProcessing = on;

        public static string Describe() => GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset u
            ? $"{u.name} grading {u.colorGradingMode} HDR {u.supportsHDR} scale {u.renderScale:F2}"
            : "no URP";

        /// <summary>Editor proof shots: the pipeline settings <see cref="Apply"/> would make; <paramref name="restore"/> undoes them.</summary>
        public static void ConfigurePipelineForShots(bool high, out System.Action restore)
        {
            // The WebGL player runs quality level 0 (Mobile_RPAsset); the Editor defaults to PC.
            var level = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, true);
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            var mode = urp != null ? urp.colorGradingMode : ColorGradingMode.LowDynamicRange;
            if (urp != null)
                urp.colorGradingMode = ColorGradingMode.HighDynamicRange;
            restore = () =>
            {
                if (urp != null)
                    urp.colorGradingMode = mode; // the asset on disk stays LDR; only the runtime switches
                QualitySettings.SetQualityLevel(level, true);
            };
        }

        public static VolumeProfile BuildProfile()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.value = TonemappingMode.Neutral;
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.value = BloomThreshold;
            bloom.intensity.value = 0.35f;
            bloom.scatter.value = 0.6f;
            var ca = profile.Add<ColorAdjustments>(true);
            ca.saturation.value = Saturation;
            ca.contrast.value = Contrast;
            ca.postExposure.value = PostExposure;
            var g = Mathf.LinearToGammaSpace(PreTonemapGain); // the filter is given in gamma space (URP linearises it)
            ca.colorFilter.value = new Color(g, g, g, 1f);
            var vig = profile.Add<Vignette>(true);
            vig.intensity.value = 0.16f;
            vig.smoothness.value = 0.45f;
            return profile;
        }
    }
}
