namespace FallRush.Client
{
    /// <summary>P3: the menu's "Play On-Chain (preview)" entry. The on-chain assembly (Scripts/OnChain) sets
    /// <see cref="Play"/> at load; the Client assembly cannot reference it directly.</summary>
    public static class OnChainEntry
    {
        public static System.Action Play;
        public static bool Available => Play != null;
    }
}
