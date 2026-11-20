using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// Per-map data both sides read from the loaded scene: 12 spawn points (feet positions) and the test-bot roaming area.
    /// (M3/M4 replace this with MapDefinition ScriptableObjects; kept minimal for M2.)
    /// </summary>
    public sealed class MapInfo : MonoBehaviour
    {
        public Transform[] spawns = new Transform[Protocol.MaxPlayers];
        public Vector3 botAreaCenter = Vector3.zero;
        public Vector2 botAreaHalfExtents = new Vector2(10f, 10f);

        [Tooltip("Client-only placeholder material for player capsules (URP Lit; colour set per slot with a property block).")]
        public Material playerMaterial;

        public static MapInfo Current { get; private set; }

        private void OnEnable() => Current = this;

        private void OnDisable()
        {
            if (Current == this)
                Current = null;
        }

        public Vec3[] SpawnPoints()
        {
            var r = new Vec3[Protocol.MaxPlayers];
            for (var i = 0; i < r.Length; i++)
            {
                var p = spawns != null && i < spawns.Length && spawns[i] != null ? spawns[i].position : new Vector3(-11f + 2f * i, 0.1f, 0f);
                r[i] = new Vec3(p.x, p.y, p.z);
            }

            return r;
        }
    }
}
