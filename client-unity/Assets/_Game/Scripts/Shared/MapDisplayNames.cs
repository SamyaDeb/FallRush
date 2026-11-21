namespace FallRush.Shared
{
    /// <summary>
    /// G4-3: the player-facing title of each map, separate from its key. Keys, ids, modes and scene names stay as in
    /// <c>map_pool_v1.json</c> (its sha256 is compiled into the <c>fall_rush</c> program); only these strings are shown
    /// on screen (round intro, HUD round line, deal / intermission cards, lobby button). A rename is one line here.
    /// </summary>
    public static class MapDisplayNames
    {
        /// <summary>Indexed by map id (= <see cref="MapPoolV1.Keys"/> order).</summary>
        public static readonly string[] Names = { "Capsule Run", "Goo Tower", "Star Fountain", "Lid Flip" };

        public static string Of(byte mapId) =>
            mapId < Names.Length ? Names[mapId] : mapId == MapPoolV1.Lobby ? "Lobby" : "Sandbox";

        /// <summary>The display name for a map key (unknown keys are returned unchanged).</summary>
        public static string OfKey(string key)
        {
            for (var i = 0; i < MapPoolV1.Keys.Length; i++)
                if (MapPoolV1.Keys[i] == key)
                    return Names[i];
            return key;
        }
    }
}
