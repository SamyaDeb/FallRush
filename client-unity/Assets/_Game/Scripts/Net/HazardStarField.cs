using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary><c>STAR_PICKUP</c> field (<see cref="StarFieldDef"/>, Star Scramble): anchors + the seeded timeline (pure); collected / dropped stars are reactive.</summary>
    public sealed class HazardStarField : HazardAuthoring
    {
        public StarFieldDef def;
        public StarFieldDef variantB;
        /// <summary>G4.4 Star Fountain: the fountain crown, where the view-only burst arcs start (null: no burst view). Not hashed, not simulated.</summary>
        public Transform burstOrigin;

        public override HazardType Type => HazardType.StarPickup;

        public StarFieldDef Resolve(in RoundParams p) => p.Variant == 1 && hasVariantB ? variantB : def;
    }
}
