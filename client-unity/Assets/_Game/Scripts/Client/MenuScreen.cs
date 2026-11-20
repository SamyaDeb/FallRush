using System;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>Menu: "Play (connect)" and "Connect Wallet (smoke test)". Placeholder UI.</summary>
    public sealed class MenuScreen : MonoBehaviour
    {
        private Text _target;
        private Text _wallet;
        private Button _walletButton;
        private bool _walletBusy;

        private void Start()
        {
            var canvas = UiKit.CreateCanvas("MenuCanvas");
            var panel = UiKit.Panel(canvas, new Vector2(760, 560), UiKit.CloudWhite);

            UiKit.Label(panel, "FALL RUSH", 72, UiKit.Magenta, FontStyle.Bold, 90);
            UiKit.Label(panel, "Practice match (M4): you + bots, 4 rounds", 22, UiKit.Grape);
            _target = UiKit.Label(panel, string.Empty, 20, UiKit.Ink, FontStyle.Normal, 56);
            UiKit.Button(panel, "Play (Quick Play)", UiKit.SunYellow, OnPlay);
            _walletButton = UiKit.Button(panel, "Connect Wallet (smoke test)", UiKit.Mint, OnConnectWallet);
            _wallet = UiKit.Label(panel, string.Empty, 18, UiKit.Ink, FontStyle.Normal, 48);
            UiKit.Label(panel, $"build {Application.version} · protocol v{Protocol.Version}", 16, UiKit.Grape);

            var boot = NetBootstrap.Instance;
            if (boot == null)
            {
                _target.text = "NetBootstrap missing — start from the Boot scene.";
                return;
            }

            boot.ResolveTarget();
            _target.text = $"Server: {boot.Target.Url}\n(from {boot.TargetSource})";

            if (!WalletBridge.Available)
            {
                _walletButton.interactable = false;
                _wallet.text = "Wallet not available on this platform (WebGL only).";
            }
            else
            {
                _wallet.text = WalletBridge.ProviderLabel ?? string.Empty;
            }
        }

        private void OnPlay()
        {
            var boot = NetBootstrap.Instance;
            if (boot == null)
                return;
            boot.Connect();
            SceneManager.LoadScene(NetBootstrap.LobbyScene);
        }

        private async void OnConnectWallet()
        {
            if (_walletBusy || !WalletBridge.Available)
                return;
            _walletBusy = true;
            _walletButton.interactable = false;
            _wallet.text = "Opening wallet…";
            try
            {
                var pubkey = await WalletBridge.ConnectAsync();
                _wallet.text = string.IsNullOrEmpty(pubkey)
                    ? "Wallet connect cancelled or failed (see console)."
                    : $"Connected (devnet): {pubkey}";
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (_wallet != null)
                    _wallet.text = $"Wallet error: {e.Message}";
            }
            finally
            {
                _walletBusy = false;
                if (_walletButton != null)
                    _walletButton.interactable = true;
            }
        }
    }
}
