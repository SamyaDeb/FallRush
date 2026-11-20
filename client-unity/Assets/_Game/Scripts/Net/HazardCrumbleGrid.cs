using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// <c>CRUMBLE_TILE</c> grid (<see cref="TileGridDef"/>, Crumble Shelf): one component for all tiles; <see cref="tiles"/>
    /// holds each tile's box collider in tile-id order (switched off once dropped, from the reactive state). Tile visuals
    /// are drawn by the client with GPU instancing. Also sets when the top shelf's spinner bars start (per variant).
    /// </summary>
    public sealed class HazardCrumbleGrid : HazardAuthoring
    {
        public TileGridDef def;
        public Collider[] tiles = new Collider[0];
        [Tooltip("Hazard tick the spinner bars start sweeping (60 s = 1800), variant A / B.")]
        public int spinnerStartA = 1800;
        public int spinnerStartB = 1800;

        public override HazardType Type => HazardType.CrumbleTile;

        public int SpinnerStart(in RoundParams p) => p.Variant == 1 && hasVariantB ? spinnerStartB : spinnerStartA;
    }
}
