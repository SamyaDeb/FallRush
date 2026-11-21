using System;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>Menu: name entry (G1), "Play (connect)", Settings (G1), "Connect Wallet (smoke test)". Placeholder UI.</summary>
    public sealed class MenuScreen : MonoBehaviour
    {
        private Text _target;
        private Text _wallet;
        private Button _walletButton;
        private bool _walletBusy;
        private GameObject _settings;

        private void Start()
        {
            var canvas = UiKit.CreateCanvas("MenuCanvas");
            var panel = UiKit.Panel(canvas, new Vector2(760, 760), UiKit.CloudWhite);

            UiKit.Label(panel, "FALL RUSH", 72, UiKit.Magenta, FontStyle.Bold, 90);
            UiKit.Label(panel, "Practice match: you + bots, 4 rounds", 22, UiKit.Grape);
            NameField(panel);
            MyRushling(panel);
            _target = UiKit.Label(panel, string.Empty, 18, UiKit.Ink, FontStyle.Normal, 44);
            UiKit.Button(panel, "Play (Quick Play)", UiKit.SunYellow, OnPlay);
            UiKit.Button(panel, "Settings", UiKit.SkyLight, () => _settings.SetActive(!_settings.activeSelf));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UiKit.Button(panel, "Practice / tuning (dev)", UiKit.Mint, () => NetBootstrap.StartPractice("BlockPartyDash"));
#endif
            _walletButton = UiKit.Button(panel, "Connect Wallet (smoke test)", UiKit.Mint, OnConnectWallet);
            _wallet = UiKit.Label(panel, string.Empty, 18, UiKit.Ink, FontStyle.Normal, 48);
            UiKit.Label(panel, "WASD move · Space jump · Shift / left click dive · mouse look · 1-4 emotes (lobby) · Esc settings", 16, UiKit.Ink);
            UiKit.Label(panel, $"build {Application.version} · protocol v{Protocol.Version}", 16, UiKit.Grape);
            var sp = SettingsPanel.Build(canvas, new Vector2(640, 520));
            sp.anchorMin = sp.anchorMax = sp.pivot = new Vector2(1f, 0.5f);
            sp.anchoredPosition = new Vector2(-24f, 0f);
            _settings = sp.gameObject;
            _settings.SetActive(false);
            NetBootstrap.PreferredName = GameSettings.PlayerName;
            NetBootstrap.PreferredPreset = GameSettings.CosmeticPreset;

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

        /// <summary>G2.2 "My Rushling": cycle the cosmetic preset (saved locally, sent in hello; no stats).</summary>
        private static void MyRushling(Transform panel)
        {
            Button b = null;
            string Label() => $"My Rushling: {CosmeticPresets.Get(GameSettings.CosmeticPreset).Name}  >";
            b = UiKit.Button(panel, Label(), UiKit.Magenta, () =>
            {
                GameSettings.CosmeticPreset = (GameSettings.CosmeticPreset + 1) % CosmeticPresets.Count;
                GameSettings.Save();
                NetBootstrap.PreferredPreset = GameSettings.CosmeticPreset;
                UiKit.SetButtonText(b, Label());
            });
        }

        /// <summary>G1 §3.6: the display name (saved locally; sent in hello unless the URL has ?name=).</summary>
        private static void NameField(Transform panel)
        {
            var go = DefaultControls.CreateInputField(default);
            go.transform.SetParent(panel, false);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 48f;
            le.preferredWidth = 420f;
            go.GetComponent<Image>().color = UiKit.SkyLight;
            var input = go.GetComponent<InputField>();
            input.characterLimit = GameSettings.MaxNameChars;
            foreach (var t in go.GetComponentsInChildren<Text>(true))
            {
                t.font = UiKit.Font;
                t.fontSize = 24;
                t.alignment = TextAnchor.MiddleCenter;
                t.color = t.gameObject.name == "Placeholder" ? new Color(0.16f, 0.07f, 0.31f, 0.45f) : UiKit.Ink;
            }

            input.placeholder.GetComponent<Text>().text = "Your name";
            input.text = GameSettings.PlayerName;
            input.onEndEdit.AddListener(v =>
            {
                GameSettings.PlayerName = v;
                GameSettings.Save();
                input.text = GameSettings.PlayerName;
                NetBootstrap.PreferredName = GameSettings.PlayerName;
            });
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
