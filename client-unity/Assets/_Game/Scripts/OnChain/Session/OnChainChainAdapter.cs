using FallRush.Client;
using FallRush.Shared;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-D: the chain chip for on-chain play. <c>◆ Rollup ✓</c> while Worlds arrive, <c>! Pending</c> on a stall
    /// (no World for 300 ms) or before the first one; <c>⬢ Solana ✓</c> once the arena reports the Final committed
    /// (Part 2). The emulator is labelled as such on result screens.
    /// </summary>
    public sealed class OnChainChainAdapter : IChainAdapter
    {
        public bool Live, Committed, Emulator = true;
        public string Arena = "", RollupTx, RollupUrl, SolanaTx, SolanaUrl, AccountUrl;
        private byte[] _seed = new byte[32];

        public ChainState State => Committed ? ChainState.SolanaConfirmed : Live ? ChainState.RollupConfirmed : ChainState.Pending;
        public string Detail => Emulator ? "Local chain emulator (not on Solana)" : "MagicBlock Ephemeral Rollup (devnet-as)";
        public bool IsPractice => Emulator;

        public DealProof Deal => new DealProof
        {
            SeedHex = Sha256.Hex(_seed),
            Headline = Emulator ? "Emulator deal (arena seed)" : "Arena seed (on-chain)",
            Verified = false,
        };

        public ReceiptLinks Receipt => new ReceiptLinks
        {
            RollupTx = RollupTx, RollupUrl = RollupUrl,
            SolanaTx = SolanaTx, SolanaUrl = SolanaUrl,
            MatchAccount = Arena, AccountUrl = AccountUrl,
        };

        public void OnMatchState(MatchStateMessage m)
        {
            if (m?.Seed != null && m.Seed.Length == 32)
                _seed = m.Seed;
        }
    }
}
