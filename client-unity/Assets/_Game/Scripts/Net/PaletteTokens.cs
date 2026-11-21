using UnityEngine;

namespace FallRush.Net
{
    /// <summary>G4.6 surface hues: every walkable top carries one, one per map section (doc 17 §3.1).</summary>
    public enum SurfaceHue
    {
        Bubblegum = 0, LemonDrop = 1, SodaBlue = 2, SourApple = 3, GrapeFizz = 4, Tangerine = 5,
    }

    /// <summary>
    /// The Fall Rush palette (doc 17 §3.1) as one shared asset (<c>Resources/FallRush/Palette.asset</c>), read by the map
    /// art pass (Editor) and the client (UI, effects, Rushlings). The code defaults below are the source of truth; the
    /// asset is regenerated from them by <c>MapArt</c>. Readability rules (tested, G4.6): every walkable top carries a
    /// candy surface hue (S 0.45–0.75, V 0.85–0.95, never white), one hue per section, sides the same hue darker; the
    /// meaning colours (hit pink + stripe, lemon "about to move", mint goal / spring / spawn, goo, sky) are reserved and
    /// never on a walkable top; hazards are always striped.
    /// </summary>
    [CreateAssetMenu(menuName = "Fall Rush/Palette tokens")]
    public sealed class PaletteTokens : ScriptableObject
    {
        public const string ResourcePath = "FallRush/Palette";

        [Header("doc 17 §3.1 meaning colours (reserved: never on a walkable top)")]
        public Color skyTop = Hex(0x2E9BFF);
        public Color skyHorizon = Hex(0xA6E3FF);
        public Color hazardMagenta = Hex(0xFF2D8A);  // hit pink (+ the ink / yellow stripe)
        public Color hazardYellow = Hex(0xFFD43B);   // "about to move" lemon (pulsing) and the stripe's light band
        public Color mint = Hex(0x2FE0A8);           // goal / spring / spawn (+ checker)
        public Color goo = Hex(0x9B3CFF);            // goo (+ glitter)

        [Header("doc 17 §3.1 brand / UI tokens")]
        public Color magenta = Hex(0xFF3EA5);
        public Color sunYellow = Hex(0xFFD21F);
        public Color grape = Hex(0x7B2FF7);
        public Color cloudWhite = Hex(0xFFFFFF);

        /// <summary>
        /// Surface hue tops (index = <see cref="SurfaceHue"/>): the plan's hues (Bubblegum #FF6FAE, Lemon drop #FFD43B, Soda
        /// blue #4CC2FF, Sour apple #86E04E, Grape fizz #A77DFF, Tangerine #FF9A3D) brought into the top band (V 0.92,
        /// S ≤ 0.74; Sour apple already fits): decision G4.6-1.
        /// </summary>
        [Header("doc 17 §3.1 surface hues (G4.6)")]
        public Color[] hueTops = { Hex(TopHex[0]), Hex(TopHex[1]), Hex(TopHex[2]), Hex(TopHex[3]), Hex(TopHex[4]), Hex(TopHex[5]) };

        /// <summary>Surface hue sides: the same hue, darker and more saturated (the plan's side values).</summary>
        public Color[] hueSides = { Hex(SideHex[0]), Hex(SideHex[1]), Hex(SideHex[2]), Hex(SideHex[3]), Hex(SideHex[4]), Hex(SideHex[5]) };

        public static readonly int[] TopHex = { 0xEB66A0, 0xEBC53D, 0x46B2EB, 0x86E04E, 0x9A73EB, 0xEB903D };
        public static readonly int[] SideHex = { 0xD8327F, 0xF09A12, 0x1F82E0, 0x3FA82F, 0x6D3FE0, 0xE05A1F };

        [Header("Derived (ours)")]
        public Color ink = Hex(0x2A1250);           // outlines, text, hazard ink
        public Color amber = Hex(0xFFA51F);          // "! Pending", golden star
        public Color walkTop = Hex(0xF2F0FF);        // dressing prop tops only (not walkable; walkable tops use hueTops)
        public Color walkBand = Hex(0xFFFFFF);
        public Color walkSide = Hex(0xD9DDF7);
        public Color wallSide = Hex(0xCBC0F5);
        public Color shade = new Color(0.74f, 0.72f, 0.92f);
        public Color ambientSky = Hex(0xBFD9FF);     // G4.6: cool sky ambient
        public Color ambientGround = Hex(0xC2A6D6);  // G4.6: warm violet ground ambient
        public Color sun = Hex(0xFFF1DC);            // G4.6: warm sun

        /// <summary>Pastel prop colours (dressing only, out of reach).</summary>
        public Color[] blockSides =
        {
            Hex(0xFFB8DC), Hex(0xA9DAFF), Hex(0xA8F0D6), Hex(0xFFE58A), Hex(0xCDB8FF),
        };

        public Color HueTop(SurfaceHue h) => hueTops[(int)h];
        public Color HueSide(SurfaceHue h) => hueSides[(int)h];

        /// <summary>One map section and its surface hue (the builders tag their objects with these names).</summary>
        public readonly struct Section
        {
            public readonly string Map, Name;
            public readonly SurfaceHue Hue;
            /// <summary>A yellow telegraph shows in this section (lit lids, gate / belt / chute pulse, the crown glow): never Lemon drop here.</summary>
            public readonly bool YellowTelegraph;

            public Section(string map, string name, SurfaceHue hue, bool yellow)
            {
                Map = map;
                Name = name;
                Hue = hue;
                YellowTelegraph = yellow;
            }
        }

        /// <summary>
        /// The section table as built (G4.6, doc 17 §3.1, doc 08). Map keys are the scene keys (Capsule Run = BlockPartyDash,
        /// Star Fountain = StarScramble, Lid Flip = CrumbleShelf). Goals, springs and spawns stay mint (+ checker).
        /// </summary>
        public static readonly Section[] Sections =
        {
            new Section("BlockPartyDash", "start", SurfaceHue.SodaBlue, true),        // start gate pulse
            new Section("BlockPartyDash", "slope", SurfaceHue.Tangerine, true),       // ball chute hatches
            new Section("BlockPartyDash", "gates", SurfaceHue.GrapeFizz, true),       // gate door pulse
            new Section("BlockPartyDash", "hop", SurfaceHue.SourApple, false),
            new Section("BlockPartyDash", "sweep", SurfaceHue.Bubblegum, false),
            new Section("BlockPartyDash", "mallet", SurfaceHue.SodaBlue, false),
            new Section("BlockPartyDash", "belt", SurfaceHue.Tangerine, true),        // ledge ball chutes, belt chevrons
            new Section("GooTower", "ground", SurfaceHue.SourApple, false),
            new Section("GooTower", "k0", SurfaceHue.SourApple, false),
            new Section("GooTower", "k1", SurfaceHue.SodaBlue, true),                 // ball chute
            new Section("GooTower", "k2", SurfaceHue.Tangerine, false),
            new Section("GooTower", "k3", SurfaceHue.Bubblegum, false),
            new Section("GooTower", "k4", SurfaceHue.SodaBlue, false),
            new Section("GooTower", "k5", SurfaceHue.Tangerine, false),
            new Section("GooTower", "k6", SurfaceHue.Bubblegum, false),
            new Section("GooTower", "k7", SurfaceHue.SourApple, true),                // gate doors, ball chute
            new Section("StarScramble", "centre", SurfaceHue.GrapeFizz, true),        // the crown glow
            new Section("StarScramble", "table", SurfaceHue.SodaBlue, true),
            new Section("StarScramble", "outer", SurfaceHue.Tangerine, false),
            new Section("StarScramble", "islands", SurfaceHue.SourApple, false),
            new Section("CrumbleShelf", "ring4", SurfaceHue.Bubblegum, true),         // lit lids everywhere on Lid Flip
            new Section("CrumbleShelf", "ring3", SurfaceHue.Tangerine, true),
            new Section("CrumbleShelf", "ring2", SurfaceHue.SodaBlue, true),
            new Section("CrumbleShelf", "ring1", SurfaceHue.Bubblegum, true),         // G4.6-5: Sour apple was too close to the lit lemon in luma
            new Section("CrumbleShelf", "ring0", SurfaceHue.Bubblegum, true),
            new Section("CrumbleShelf", "lower", SurfaceHue.GrapeFizz, true),
            new Section("Lobby", "deck", SurfaceHue.SodaBlue, false),
            new Section("Lobby", "tileA", SurfaceHue.Bubblegum, false),
            new Section("Lobby", "tileB", SurfaceHue.Tangerine, false),
        };

        /// <summary>The hue of a section (unknown map or section: Soda blue, e.g. the test sandbox).</summary>
        public static SurfaceHue HueOf(string map, string section)
        {
            foreach (var s in Sections)
                if (s.Map == map && s.Name == section)
                    return s.Hue;
            return SurfaceHue.SodaBlue;
        }

        /// <summary>Code defaults (no asset needed): the Lid Flip view and tests use these.</summary>
        public static Color DefaultTop(SurfaceHue h) => Hex(TopHex[(int)h]);

        public static Color DefaultSide(SurfaceHue h) => Hex(SideHex[(int)h]);

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
