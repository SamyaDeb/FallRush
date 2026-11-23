namespace FallRush.Client
{
    /// <summary>The chain status chip's four states (doc 17 §5).</summary>
    public enum ChainState : byte
    {
        Server = 0,          // ● Server (grey): results live on our game server only (practice, or before the rollup confirms)
        RollupConfirmed = 1, // ◆ Rollup ✓ (mint): recorded on the MagicBlock Ephemeral Rollup
        SolanaConfirmed = 2, // ⬢ Solana ✓ (yellow): committed to Solana
        Pending = 3,         // ! Pending (amber): a transaction is in flight
    }

    /// <summary>The deal's provenance for the Deal screen: the seed and, on a ranked match, the VRF proof.</summary>
    public struct DealProof
    {
        public string SeedHex;        // 64 hex chars
        public string Headline;       // "Practice deal (local seed)" | "VRF ✓ MagicBlock VRF"
        public bool Verified;         // true once the VRF callback is confirmed
        public string VrfSignature;   // base58 tx signature of the VRF callback, null in practice
        public string ExplorerUrl;    // explorer link for VrfSignature, null in practice
    }

    /// <summary>The receipt's explorer links (M6); null fields show as placeholders.</summary>
    public struct ReceiptLinks
    {
        public string RollupTx, RollupUrl;      // last ER transaction (match events)
        public string SolanaTx, SolanaUrl;      // the final commit / undelegate on Solana
        public string MatchAccount, AccountUrl; // the Match PDA
    }

    /// <summary>
    /// G3.6 MagicBlock-ready game UI: the client-side presentation of chain state. The game screens read only this
    /// interface; M6 implements it (fed by the server's <c>evt.chainStatus</c> and the VRF deal), so no game code changes
    /// then. Contract in doc 04 §7: the client never talks to the chain itself (no RPC, no wallet calls here).
    /// </summary>
    public interface IChainAdapter
    {
        /// <summary>The chip state now.</summary>
        ChainState State { get; }

        /// <summary>A short line under the chip on result screens ("Practice: not recorded on chain").</summary>
        string Detail { get; }

        /// <summary>True for the practice adapter (local seed, nothing on chain).</summary>
        bool IsPractice { get; }

        DealProof Deal { get; }

        ReceiptLinks Receipt { get; }

        /// <summary>Called by the match HUD with every <c>match</c> state the client receives.</summary>
        void OnMatchState(FallRush.Shared.MatchStateMessage m);
    }

    /// <summary>P3: the adapter the match screens read. The on-chain session sets <see cref="Override"/> while it runs;
    /// otherwise the practice adapter (Classic play is unchanged).</summary>
    public static class ChainAdapters
    {
        public static IChainAdapter Override;
        public static IChainAdapter Current => Override ?? PracticeChainAdapter.Current;
    }

    /// <summary>Chip look per state (glyph, label, colour). Pure; tested.</summary>
    public static class ChainChipStyle
    {
        public static string Glyph(ChainState s) => s switch
        {
            ChainState.RollupConfirmed => "◆",
            ChainState.SolanaConfirmed => "⬢",
            ChainState.Pending => "!",
            _ => "●",
        };

        public static string Label(ChainState s) => s switch
        {
            ChainState.RollupConfirmed => "Rollup ✓",
            ChainState.SolanaConfirmed => "Solana ✓",
            ChainState.Pending => "Pending",
            _ => "Server",
        };

        public static UnityEngine.Color Color(ChainState s) => s switch
        {
            ChainState.RollupConfirmed => UiKit.Mint,
            ChainState.SolanaConfirmed => UiKit.SunYellow,
            ChainState.Pending => UiKit.Amber,
            _ => UiKit.Grey,
        };

        public static string Text(ChainState s) => $"{Glyph(s)} {Label(s)}";
    }
}
