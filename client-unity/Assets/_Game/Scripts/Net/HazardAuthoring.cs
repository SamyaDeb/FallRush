using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// Base of the per-type scene authoring components (doc 08 §2 data model): a stable <see cref="obstacleId"/>, the phase
    /// range the seeded phase is mapped into, and an optional variant B def. Subclasses hold the integer def for variant A
    /// and resolve it for a round: variant → seeded phase → modifier rescaling (<see cref="HazardTuning"/>). No behaviour
    /// of their own: the simulation reads the resolved defs through <see cref="MapInfo.BuildHazards"/>; clients pose the
    /// visuals at render time.
    /// </summary>
    public abstract class HazardAuthoring : MonoBehaviour
    {
        [Tooltip("Stable string id, e.g. bpd_spinner_02. Hashed with the round's hazard seed for the start phase.")]
        public string obstacleId = string.Empty;
        [Tooltip("Seeded phase is mapped into [phaseMin16, phaseMax16] (0..65535 = one cycle).")]
        public int phaseMin16;
        public int phaseMax16 = 65535;
        [Tooltip("Variant B uses the variantB def instead of def.")]
        public bool hasVariantB;

        public abstract HazardType Type { get; }

        /// <summary>Seeded start phase for this round (doc 08 §2); see <see cref="ObstaclePhase"/>.</summary>
        public int PhaseFor(ulong hazardSeed) => ObstaclePhase.Phase16(hazardSeed, obstacleId, phaseMin16, phaseMax16);

        public string HashId => $"{(byte)Type}:{obstacleId}";
    }
}
