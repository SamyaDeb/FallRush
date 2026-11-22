using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FallRush.Client;
using UnityEngine;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-E (OC-6): the menu's "Connect Wallet" without Solana.Unity-SDK. WebGL: Phantom (<c>window.solana</c>) connect
    /// only, through <c>FallRushChain.jslib</c> (no signing here; claims are P5). On-chain play itself uses the bundle's
    /// burner + session key (packages/chain). Other platforms: no wallet (as before: the SDK adapter was WebGL only).
    /// </summary>
    public static class ChainWallet
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void fr_phantom_connect();
        [DllImport("__Internal")] private static extern string fr_phantom_result();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WalletBridge.ProviderLabel = "Phantom (window.solana) · connect only";
            WalletBridge.ConnectAsync = ConnectAsync;
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private static async Task<string> ConnectAsync()
        {
            fr_phantom_connect();
            var t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 120f)
            {
                await Task.Yield();
                var r = fr_phantom_result();
                if (string.IsNullOrEmpty(r))
                    continue;
                if (r.StartsWith("err:", StringComparison.Ordinal))
                {
                    Debug.LogWarning("[wallet] " + r);
                    return null;
                }

                return r;
            }

            return null;
        }
#endif
    }
}
