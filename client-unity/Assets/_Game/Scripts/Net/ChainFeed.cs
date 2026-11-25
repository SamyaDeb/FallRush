using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// P3-D: an external simulation source for <see cref="ClientSim"/> (the on-chain session: the World of a MagicBlock
    /// rollup arena). While <see cref="ClientSim.Feed"/> is set and <see cref="OverridesServer"/> is true, ClientSim ignores
    /// the game server's snap / round / rstate / evt / match / rx messages and takes the same structures from the feed
    /// (<c>ClientSim.Feed*</c> methods), so the HUD, Deal screen, claw, crown, receipt and views run unchanged. The own
    /// player comes from the feed's prediction. Null in Classic play: nothing changes there.
    /// </summary>
    public interface IChainFeed
    {
        /// <summary>True while the feed (not the game server) drives the match screens and the players.</summary>
        bool OverridesServer { get; }

        /// <summary>The own player's render state from the feed's prediction; false = draw it like a remote player.</summary>
        bool TrySampleOwn(out Vector3 feet, out Vector3 vel, out byte flags, out byte yaw, out byte stun);

        /// <summary>Smooth run: another slot's render state from the feed (bots at the predicted present); false = interpolate it.</summary>
        bool TrySampleRemote(int slot, out Vector3 feet, out Vector3 vel, out byte flags, out byte yaw, out byte stun);

        /// <summary>The predicted present (fractional tick) for hazards and the local timeline; NaN = use interpolation.</summary>
        double PresentTick { get; }
    }
}
