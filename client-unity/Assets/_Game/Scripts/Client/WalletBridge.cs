using System;
using System.Threading.Tasks;

namespace FallRush.Client
{
    /// <summary>
    /// Seam between the menu (all client platforms) and <c>FallRush.Client.Wallet</c>, the only assembly that
    /// references Solana.Unity-SDK (WebGL + Editor only). The wallet assembly registers itself at startup.
    /// </summary>
    public static class WalletBridge
    {
        /// <summary>Connects a wallet and returns its base58 public key. Null when no wallet assembly is present.</summary>
        public static Func<Task<string>> ConnectAsync;

        /// <summary>Short label for the menu (e.g. "Solana.Unity-SDK 1.2.9 · devnet").</summary>
        public static string ProviderLabel;

        public static bool Available => ConnectAsync != null;
    }
}
