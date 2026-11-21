using FallRush.Net;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// G4.8 map look switch (render only, never on the wire): <see cref="LookId.Soft"/> (default) or <see cref="LookId.Candy"/>
    /// (G4.6). Chosen by <c>?look=soft|candy</c> (WebGL) or <c>-fallrushLook soft|candy</c> (desktop); F7 toggles it in game
    /// (<see cref="SandboxView"/>) so the two can be compared on the spot.
    /// <para>How a look reaches the screen (decision G4.8-1, the "two colour channels" variant): the map meshes are one set; their
    /// vertex colour is the soft albedo and uv3 = (candy albedo, 2 + pattern + 8 × role), so the toon shader picks the colour per
    /// vertex from the global <c>_FR_Look</c> (0 soft, 1 candy) and gates the soft-only shading (patterns, foam ramp, gloss, sky
    /// Fresnel, real shadows) on it. The grade, the sun, the ambient, the fog, the sky and the shadows follow here.</para>
    /// </summary>
    public static class LookState
    {
        private static readonly int LookId_ = Shader.PropertyToID("_FR_Look");
        private static readonly int SkyOn = Shader.PropertyToID("_FR_SkyOn");
        private static readonly int SkyTop = Shader.PropertyToID("_FR_SkyTop");
        private static readonly int SkyHorizon = Shader.PropertyToID("_FR_SkyHorizon");
        private static readonly int SkyHaze = Shader.PropertyToID("_FR_SkyHaze");
        private static bool s_Init;
        private static LookId s_Current = LookId.Soft;

        /// <summary>Raised after every switch (Lid Flip view recolours its lids).</summary>
        public static event System.Action Changed;

        public static LookId Current
        {
            get
            {
                if (!s_Init)
                {
                    s_Init = true;
                    s_Current = Parse(FallRush.Shared.NetTargets.GetQueryParam(Application.absoluteURL, "look") ?? Arg("-fallrushLook")) ?? LookId.Soft;
                    Shader.SetGlobalFloat(LookId_, s_Current == LookId.Candy ? 1f : 0f);
                }

                return s_Current;
            }
        }

        public static LookId? Parse(string s) => s == "soft" ? LookId.Soft : s == "candy" ? LookId.Candy : (LookId?)null;

        public static string Name(LookId l) => l == LookId.Soft ? "soft" : "candy";

        private static string Arg(string name)
        {
            var a = System.Environment.GetCommandLineArgs();
            for (var i = 0; i < a.Length - 1; i++)
                if (a[i] == name)
                    return a[i + 1].ToLowerInvariant();
            return null;
        }

        /// <summary>Switches the look (also the Editor proof shots and tests). Map scene state follows if a map is loaded.</summary>
        public static void Set(LookId look)
        {
            _ = Current;
            s_Current = look;
            Shader.SetGlobalFloat(LookId_, look == LookId.Candy ? 1f : 0f);
            ApplyScene();
            GraphicsQuality.ApplyLook();
            Changed?.Invoke();
            Debug.Log($"[look] {Name(look)}");
        }

        public static void Toggle() => Set(Current == LookId.Soft ? LookId.Candy : LookId.Soft);

        /// <summary>
        /// The look's scene state: sun colour, gradient ambient globals, base fog colour, sky colours (globals the sky shader
        /// reads) and the soft-only backdrop. Called on map load (<see cref="WorldLook.OnMapLoaded"/>) and on every switch.
        /// </summary>
        public static void ApplyScene()
        {
            var look = Current;
            var p = PaletteTokens.Get();
            Shader.SetGlobalFloat(LookId_, look == LookId.Candy ? 1f : 0f);
            var amb = p.AmbientSky(look);
            var gnd = p.AmbientGround(look);
            Shader.SetGlobalColor(Shader.PropertyToID("_FR_AmbSky"), new Color(amb.r, amb.g, amb.b, 1f));
            Shader.SetGlobalColor(Shader.PropertyToID("_FR_AmbGround"), new Color(gnd.r, gnd.g, gnd.b, 1f));
            Shader.SetGlobalFloat(SkyOn, 1f);
            Shader.SetGlobalColor(SkyTop, p.SkyTop(look));
            Shader.SetGlobalColor(SkyHorizon, p.SkyHorizon(look));
            Shader.SetGlobalColor(SkyHaze, look == LookId.Soft ? p.softHaze : p.skyHorizon);
            if (RenderSettings.fog && RenderSettings.fogMode == FogMode.Linear)
                RenderSettings.fogColor = p.SkyHorizon(look);
            WorldLook.OnLookChanged(p.SkyHorizon(look));
            var sun = RenderSettings.sun;
            if (sun == null)
                foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional)
                        sun = l;
            if (sun != null)
                sun.color = p.Sun(look);
            foreach (var b in Object.FindObjectsByType<LookOnly>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                b.Apply(look);
        }
    }
}
