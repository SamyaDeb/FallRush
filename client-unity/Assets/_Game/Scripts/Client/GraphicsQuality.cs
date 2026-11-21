using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FallRush.Client
{
    /// <summary>
    /// G2.1 "Graphics: High / Low" (Settings). High: post-processing (low bloom, colour adjustments, a subtle vignette)
    /// and full render scale. Low: no post and the WebGL quality level's 0.8 render scale (must hold 60 FPS on the M2 Air).
    /// The post volume is created at runtime (one global volume per map view), so no scene carries it.
    /// </summary>
    public static class GraphicsQuality
    {
        private static Volume s_Volume;
        private static float s_BaseScale = -1f;

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
            }

            if (cam != null)
                cam.GetUniversalAdditionalCameraData().renderPostProcessing = high;
            EnsureVolume();
            if (s_Volume != null)
                s_Volume.enabled = high;
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
        public static bool IsHigh => s_Volume != null && s_Volume.enabled;

        private static void EnsureVolume()
        {
            if (s_Volume != null)
                return;
            var go = new GameObject("PostVolume");
            Object.DontDestroyOnLoad(go);
            s_Volume = go.AddComponent<Volume>();
            s_Volume.isGlobal = true;
            s_Volume.priority = 10f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.value = 1.15f;
            bloom.intensity.value = 0.35f;
            bloom.scatter.value = 0.6f;
            var ca = profile.Add<ColorAdjustments>(true);
            ca.saturation.value = 10f;
            ca.contrast.value = 8f;
            ca.postExposure.value = 0f;
            var vig = profile.Add<Vignette>(true);
            vig.intensity.value = 0.16f;
            vig.smoothness.value = 0.45f;
            s_Volume.sharedProfile = profile;
        }
    }
}
