using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// The Fall Rush palette (doc 17 §3.1) as one shared asset (<c>Resources/FallRush/Palette.asset</c>), read by the map
    /// art pass (Editor) and the client (UI, effects, Rushlings). The code defaults below are the source of truth; the
    /// asset is regenerated from them by <c>MapArt</c>. Readability rules (tested): walkable tops light and desaturated,
    /// hazards saturated (and always striped), goals mint.
    /// </summary>
    [CreateAssetMenu(menuName = "Fall Rush/Palette tokens")]
    public sealed class PaletteTokens : ScriptableObject
    {
        public const string ResourcePath = "FallRush/Palette";

        [Header("doc 17 §3.1 tokens")]
        public Color skyTop = Hex(0x3FA9F5);
        public Color skyHorizon = Hex(0xBFE6FF);
        public Color magenta = Hex(0xFF3EA5);
        public Color sunYellow = Hex(0xFFD21F);
        public Color grape = Hex(0x7B2FF7);
        public Color cloudWhite = Hex(0xFFFFFF);
        public Color mint = Hex(0x5BE3B5);
        public Color hazardYellow = Hex(0xFFD21F);
        public Color hazardMagenta = Hex(0xFF3EA5);
        public Color goo = Hex(0x9B5CFF);

        [Header("Derived (ours)")]
        public Color ink = Hex(0x2A1250);           // outlines, text
        public Color amber = Hex(0xFFA51F);          // "! Pending", golden star
        public Color walkTop = Hex(0xF2F0FF);        // walkable tops: light, desaturated
        public Color walkBand = Hex(0xFFFFFF);
        public Color walkSide = Hex(0xD9DDF7);
        public Color wallSide = Hex(0xCBC0F5);
        public Color shade = new Color(0.74f, 0.72f, 0.92f);
        public Color ambientSky = new Color(0.80f, 0.88f, 1.00f);
        public Color ambientGround = new Color(0.66f, 0.62f, 0.86f);
        public Color sun = new Color(1.00f, 0.97f, 0.90f);

        /// <summary>Pastel toy-block side colours (sides only; the tops you stand on stay <see cref="walkTop"/>).</summary>
        public Color[] blockSides =
        {
            Hex(0xFFB8DC), Hex(0xA9DAFF), Hex(0xA8F0D6), Hex(0xFFE58A), Hex(0xCDB8FF),
        };

        public static Color Hex(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        private static PaletteTokens s_Loaded;

        /// <summary>The shared asset, or the code defaults if it is missing (tests, tools).</summary>
        public static PaletteTokens Get()
        {
            if (s_Loaded != null)
                return s_Loaded;
            s_Loaded = Resources.Load<PaletteTokens>(ResourcePath);
            if (s_Loaded == null)
                s_Loaded = CreateInstance<PaletteTokens>();
            return s_Loaded;
        }

        /// <summary>HSV saturation and value (readability checks).</summary>
        public static void Sv(Color c, out float s, out float v)
        {
            Color.RGBToHSV(c, out _, out s, out v);
        }
    }
}
