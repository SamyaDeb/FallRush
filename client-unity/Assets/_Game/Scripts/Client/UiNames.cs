using System.Text;
using FallRush.Shared;

namespace FallRush.Client
{
    /// <summary>G1 §3.5.3: player-facing names for config keys (maps: G4-3 display names, e.g. BlockPartyDash → "Capsule Run"; None → "No modifier").</summary>
    public static class UiNames
    {
        private static readonly string[] MapCache = new string[32];
        private static readonly string[] ModCache = new string[32];

        public static string Map(int mapId)
        {
            if ((uint)mapId < (uint)MapCache.Length && MapCache[mapId] != null)
                return MapCache[mapId];
            var s = MapDisplayNames.Of((byte)mapId); // G4-3: display name, never the key
            if ((uint)mapId < (uint)MapCache.Length)
                MapCache[mapId] = s;
            return s;
        }

        public static string Modifier(int modifierId)
        {
            if ((uint)modifierId < (uint)ModCache.Length && ModCache[modifierId] != null)
                return ModCache[modifierId];
            var key = ModifierEffects.Get(modifierId).Key;
            var s = key == "None" ? "No modifier" : Spaced(key);
            if ((uint)modifierId < (uint)ModCache.Length)
                ModCache[modifierId] = s;
            return s;
        }

        /// <summary>One line on what the modifier does (round intro).</summary>
        public static string ModifierHint(int modifierId) => ModifierEffects.Get(modifierId).Key switch
        {
            "MoonBounce" => "Lower gravity, higher hops",
            "ButterFloors" => "Slippery floors: you slide when you stop",
            "TurboToys" => "Obstacles run faster",
            "BigBonk" => "Hazards hit harder",
            "TinyTilt" => "A tighter view and some fog",
            _ => string.Empty,
        };

        public static string Spaced(string key)
        {
            if (string.IsNullOrEmpty(key))
                return key;
            var sb = new StringBuilder(key.Length + 4);
            for (var i = 0; i < key.Length; i++)
            {
                if (i > 0 && char.IsUpper(key[i]) && !char.IsUpper(key[i - 1]))
                    sb.Append(' ');
                sb.Append(key[i]);
            }

            return sb.ToString();
        }
    }
}
