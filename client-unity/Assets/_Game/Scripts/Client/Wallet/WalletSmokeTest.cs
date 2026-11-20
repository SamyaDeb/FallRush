using System.Threading.Tasks;
using Solana.Unity.SDK;
using UnityEngine;

namespace FallRush.Client.Wallet
{
    /// <summary>
    /// M1 smoke test of Solana.Unity-SDK v1.2.9 (the only assembly that references it; WebGL + Editor only).
    /// Uses exactly the SDK's own WebGL path (Samples~/Solana Wallet LoginScreen → <c>Web3.Instance.LoginWalletAdapter()</c>).
    /// Devnet; connect only — no transactions, no signing, no airdrops (nonce signing is M5).
    /// </summary>
    public static class WalletSmokeTest
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            WalletBridge.ProviderLabel = "Solana.Unity-SDK 1.2.9 @1da8fd7f · devnet · connect only";
            WalletBridge.ConnectAsync = ConnectAsync;
        }

        private static async Task<string> ConnectAsync()
        {
#if UNITY_EDITOR
            // The wallet adapter is a WebGL jslib (window.walletAdapterLib); there is no Editor implementation.
            await Task.Yield();
            Debug.LogWarning("[wallet] Wallet adapter only runs in a WebGL build.");
            return null;
#else
            var web3 = Web3.Instance != null ? Web3.Instance : CreateWeb3();
            var account = await web3.LoginWalletAdapter();
            var pubkey = account?.PublicKey?.ToString();
            Debug.Log(pubkey != null ? $"[wallet] connected {pubkey} (devnet)" : "[wallet] connect returned no account");
            return pubkey;
#endif
        }

        private static Web3 CreateWeb3()
        {
            var go = new GameObject("Web3 (wallet smoke test)");
            go.SetActive(false); // configure before Awake/Start
            var web3 = go.AddComponent<Web3>();
            web3.rpcCluster = RpcCluster.DevNet;
            web3.autoConnectOnStartup = false;
            web3.solanaWalletAdapterOptions = new SolanaWalletAdapterOptions
            {
                // Prefabs left null: Web3.LoginWalletAdapter loads the SDK's own Resources/SolanaUnitySDK/WalletAdapter{UI,Button}.
                solanaWalletAdapterWebGLOptions = new SolanaWalletAdapterWebGLOptions(),
            };
            Object.DontDestroyOnLoad(go);
            go.SetActive(true);
            return web3;
        }
    }
}
