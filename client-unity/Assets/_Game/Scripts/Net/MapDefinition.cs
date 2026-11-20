using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// Map metadata (doc 08 §8 checklist): id (= <c>map_pool_v1.json</c> map id), version, mode, player limits, target
    /// duration, checkpoint count, variants and allowed modifiers. One asset per map under
    /// <c>ScriptableObjects/Maps/</c>; the scene's <see cref="MapInfo"/> references it. Part of the map content hash.
    /// </summary>
    [CreateAssetMenu(menuName = "Fall Rush/Map Definition", fileName = "MapDefinition")]
    public sealed class MapDefinition : ScriptableObject
    {
        [Tooltip("map_pool_v1.json map id (255 = dev sandbox, not in the pool).")]
        public byte mapId = MapPoolV1.Sandbox;
        public string mapKey = "Sandbox";
        public int version = 1;
        [Tooltip("Race / Survival / Skill / Final / Sandbox")]
        public string mode = "Sandbox";
        public int minPlayers = 1;
        public int maxPlayers = Protocol.MaxPlayers;
        [Tooltip("Target round duration, seconds (min, max).")]
        public Vector2Int targetDuration = new Vector2Int(75, 110);
        [Tooltip("Round timer cap after RUSH, seconds (0 = none).")]
        public int timerCapSeconds;
        [Tooltip("Ordered checkpoints, the finish included.")]
        public int checkpointCount;
        public int variants = 2;
        [Tooltip("Modifier ids allowed on this map (map_pool_v1 weights).")]
        public int[] allowedModifiers = { 0 };
        [Tooltip("Fall volume: below this height (m) a player respawns.")]
        public float killY = -20f;
        [Tooltip("Round intro (camera fly-through) and countdown, seconds.")]
        public float introSeconds = 3f;
        public float countdownSeconds = 3f;

        public bool IsRace => mode == "Race";
    }
}
