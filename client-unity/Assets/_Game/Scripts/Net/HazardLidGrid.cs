using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// <c>LID_FLIP</c> grid (<see cref="LidGridDef"/>, G4.1b): one component for all lids. <see cref="lids"/> holds each lid's box
    /// collider in lid-index order (row-major); the motor world switches each one on/off from the pure lid state at every
    /// tick (server, prediction and rewind alike; no replication). <see cref="visuals"/> are the client-only lid meshes, posed
    /// by <c>HazardViewDriver</c> (lit = yellow pulse, flipped = a 180° flip over 6 ticks, retire warn = blinking, retired =
    /// drops away). The seed is the per-obstacle seed, so in M6 the pattern comes from the VRF seed with no change here.
    /// </summary>
    public sealed class HazardLidGrid : HazardAuthoring
    {
        public LidGridDef def;
        public LidGridDef variantB;
        public Collider[] lids = new Collider[0];
        [Tooltip("Client-only lid visuals (no colliders), lid-index order.")]
        public Transform[] visuals = new Transform[0];

        public override HazardType Type => HazardType.LidFlip;

        public LidGridDef Resolve(in RoundParams p, bool zeroPhase = false)
        {
            var d = p.Variant == 1 && hasVariantB ? variantB : def;
            HazardResolve.SetSeed(ref d, zeroPhase ? 0u : ObstaclePhase.Hash(p.HazardSeed, obstacleId));
            return d;
        }
    }
}
