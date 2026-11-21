namespace FallRush.Shared
{
    /// <summary>
    /// "My Rushling" cosmetic presets (G2.2, doc 17 §2.4): shell colour, emblem, topper, LED face colour, key style.
    /// No stats, no gameplay effect. A human picks one on the menu (saved locally, sent in <c>hello</c> as the preset id);
    /// bots get a deterministic preset from their slot. The server relays every slot's preset in <c>match</c>.
    /// Pure data (no UnityEngine), shared by the server, the client and the tests.
    /// </summary>
    public static class CosmeticPresets
    {
        public enum Emblem : byte { Bolt, Star, Heart, Flame, Diamond, Moon }
        public enum Topper : byte { Goggles, Propeller, ChefHat, Headphones }
        public enum FaceTheme : byte { Mint, Cyan, Amber, Pink }
        public enum KeyStyle : byte { Classic, Star, Gear }

        public struct Preset
        {
            public string Name;
            public int Shell;      // 0xRRGGBB bottom shell
            public Emblem Emblem;
            public int EmblemColor;
            public Topper Topper;
            public FaceTheme Face;
            public KeyStyle Key;
            public int KeyColor;
        }

        /// <summary>The 12 presets. Index 0 is Bolt, the hero from the logo.</summary>
        public static readonly Preset[] All =
        {
            new Preset { Name = "Bolt", Shell = 0xFF3EA5, Emblem = Emblem.Bolt, EmblemColor = 0xFFD21F, Topper = Topper.Goggles, Face = FaceTheme.Mint, Key = KeyStyle.Classic, KeyColor = 0x7B2FF7 },
            new Preset { Name = "Splash", Shell = 0x3FA9F5, Emblem = Emblem.Star, EmblemColor = 0xFFFFFF, Topper = Topper.Propeller, Face = FaceTheme.Cyan, Key = KeyStyle.Star, KeyColor = 0xFFD21F },
            new Preset { Name = "Sunny", Shell = 0xFFD21F, Emblem = Emblem.Heart, EmblemColor = 0xFF3EA5, Topper = Topper.ChefHat, Face = FaceTheme.Pink, Key = KeyStyle.Classic, KeyColor = 0xFF3EA5 },
            new Preset { Name = "Minty", Shell = 0x5BE3B5, Emblem = Emblem.Diamond, EmblemColor = 0xFFFFFF, Topper = Topper.Headphones, Face = FaceTheme.Mint, Key = KeyStyle.Gear, KeyColor = 0x7B2FF7 },
            new Preset { Name = "Grape", Shell = 0x7B2FF7, Emblem = Emblem.Moon, EmblemColor = 0xFFD21F, Topper = Topper.Goggles, Face = FaceTheme.Amber, Key = KeyStyle.Star, KeyColor = 0xFFD21F },
            new Preset { Name = "Peach", Shell = 0xFF8A3D, Emblem = Emblem.Flame, EmblemColor = 0xFFFFFF, Topper = Topper.Propeller, Face = FaceTheme.Cyan, Key = KeyStyle.Classic, KeyColor = 0x7B2FF7 },
            new Preset { Name = "Lagoon", Shell = 0x2EC4B6, Emblem = Emblem.Bolt, EmblemColor = 0xFFFFFF, Topper = Topper.ChefHat, Face = FaceTheme.Pink, Key = KeyStyle.Gear, KeyColor = 0xFF3EA5 },
            new Preset { Name = "Cherry", Shell = 0xE84855, Emblem = Emblem.Heart, EmblemColor = 0xFFFFFF, Topper = Topper.Headphones, Face = FaceTheme.Amber, Key = KeyStyle.Classic, KeyColor = 0xFFD21F },
            new Preset { Name = "Bubblegum", Shell = 0xFF9BD2, Emblem = Emblem.Star, EmblemColor = 0x7B2FF7, Topper = Topper.Goggles, Face = FaceTheme.Cyan, Key = KeyStyle.Star, KeyColor = 0x7B2FF7 },
            new Preset { Name = "Midnight", Shell = 0x3A2E8C, Emblem = Emblem.Moon, EmblemColor = 0x5BE3B5, Topper = Topper.Propeller, Face = FaceTheme.Mint, Key = KeyStyle.Gear, KeyColor = 0x5BE3B5 },
            new Preset { Name = "Lemonade", Shell = 0xFFE45C, Emblem = Emblem.Diamond, EmblemColor = 0x3FA9F5, Topper = Topper.Headphones, Face = FaceTheme.Pink, Key = KeyStyle.Classic, KeyColor = 0x3FA9F5 },
            new Preset { Name = "Lavender", Shell = 0xB79CFF, Emblem = Emblem.Flame, EmblemColor = 0xFFD21F, Topper = Topper.ChefHat, Face = FaceTheme.Amber, Key = KeyStyle.Star, KeyColor = 0xFF3EA5 },
        };

        public static int Count => All.Length;

        /// <summary>Any byte maps to a valid preset (unknown ids from a newer client never break an older one).</summary>
        public static byte Clamp(int id) => (byte)(id >= 0 && id < All.Length ? id : 0);

        /// <summary>Bots: a deterministic preset from the slot (slot 0 would be Bolt; bots usually sit in slots 1–11).</summary>
        public static byte ForBot(int slot) => (byte)(((slot % All.Length) + All.Length) % All.Length);

        public static Preset Get(int id) => All[Clamp(id)];
    }
}
