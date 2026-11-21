using FallRush.Net;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// G2.1 client look per map: the toon shader's gradient ambient globals from the palette, the scene's base fog
    /// (light linear fog in the horizon colour, set by the map art pass) and the visual-only TinyTilt fog on top of it.
    /// </summary>
    public static class WorldLook
    {
        private static readonly int AmbSky = Shader.PropertyToID("_FR_AmbSky");
        private static readonly int AmbGround = Shader.PropertyToID("_FR_AmbGround");
        private static bool s_Captured;
        private static FogMode s_Mode;
        private static float s_Start, s_End, s_Density;
        private static Color s_Color;
        private static bool s_Fog;
        private static int s_Applied = -1;

        /// <summary>Call when a map's client view starts.</summary>
        public static void OnMapLoaded()
        {
            var p = PaletteTokens.Get();
            Shader.SetGlobalColor(AmbSky, new Color(p.ambientSky.r, p.ambientSky.g, p.ambientSky.b, 1f));
            Shader.SetGlobalColor(AmbGround, new Color(p.ambientGround.r, p.ambientGround.g, p.ambientGround.b, 1f));
            s_Fog = RenderSettings.fog;
            s_Mode = RenderSettings.fogMode;
            s_Start = RenderSettings.fogStartDistance;
            s_End = RenderSettings.fogEndDistance;
            s_Density = RenderSettings.fogDensity;
            s_Color = RenderSettings.fogColor;
            s_Captured = true;
            s_Applied = -1;
        }

        /// <summary>TinyTilt (visual-only modifier): exponential fog of <paramref name="permille"/>/1000; 0 = the map's base fog.</summary>
        public static void SetModifierFog(int permille)
        {
            if (!s_Captured || permille == s_Applied)
                return;
            s_Applied = permille;
            if (permille > 0)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogDensity = permille / 1000f;
                RenderSettings.fogColor = UiKit.SkyLight;
            }
            else
            {
                RenderSettings.fog = s_Fog;
                RenderSettings.fogMode = s_Mode;
                RenderSettings.fogStartDistance = s_Start;
                RenderSettings.fogEndDistance = s_End;
                RenderSettings.fogDensity = s_Density;
                RenderSettings.fogColor = s_Color;
            }
        }
    }
}
