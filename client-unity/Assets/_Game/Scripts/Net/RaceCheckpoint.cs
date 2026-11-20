using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// One ordered race checkpoint (doc 08 §8, doc 11 "checkpoint skipping"): a server-side trigger box (no collider; the
    /// server tests the player's feet against it) and the respawn spots behind it. The last one is the finish.
    /// Visual-only gates are separate meshes without colliders.
    /// </summary>
    public sealed class RaceCheckpoint : MonoBehaviour
    {
        [Tooltip("1-based order; the finish has the highest index.")]
        public int index = 1;
        public Vector3 triggerSize = new Vector3(12f, 4f, 2f);
        [Tooltip("Respawn spots (feet position, forward = facing the course).")]
        public Transform[] respawns = new Transform[0];
        [Tooltip("Distance along the course polyline from the start, metres (set by the map builder).")]
        public float courseDistance;
        public bool isFinish;

        public CheckpointDef Resolve()
        {
            var c = transform.position;
            var r = new int[respawns.Length * 4];
            for (var i = 0; i < respawns.Length; i++)
            {
                var p = respawns[i].position;
                var f = respawns[i].forward;
                r[i * 4] = Mm(p.x);
                r[i * 4 + 1] = Mm(p.y);
                r[i * 4 + 2] = Mm(p.z);
                r[i * 4 + 3] = Yaw256.FromVector(Mathf.RoundToInt(f.x * 1000f), Mathf.RoundToInt(f.z * 1000f), 0);
            }

            return new CheckpointDef
            {
                CenterXMm = Mm(c.x), CenterYMm = Mm(c.y), CenterZMm = Mm(c.z),
                HalfXMm = Mm(triggerSize.x * 0.5f), HalfYMm = Mm(triggerSize.y * 0.5f), HalfZMm = Mm(triggerSize.z * 0.5f),
                RespawnMm = r,
                CourseMm = Mm(courseDistance),
            };
        }

        private static int Mm(float m) => Mathf.RoundToInt(m * 1000f);
    }
}
