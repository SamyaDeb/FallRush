using System;
using FallRush.Net;
using FallRush.Shared;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// Menu (G3.5, doc 01 §12): the FALL RUSH title, your name, "My Rushling" (12 cosmetic presets with a live 3D preview,
    /// saved locally, sent in hello; no stats), Play (Quick Play), Settings, Connect Wallet (smoke test) and, in dev builds,
    /// the practice / tuning mode. UI kit look over the sky gradient.
    /// </summary>
    public sealed class MenuScreen : MonoBehaviour
    {
        private TMP_Text _target;
        private TMP_Text _wallet;
        private TMP_Text _presetName, _presetInfo;
        private Image _presetSwatch;
        private Button _walletButton;
        private bool _walletBusy;
        private GameObject _settings;

        private void Start()
        {
            var cam = Camera.main;
            UiStage.Ensure(cam);
            RushlingPreview.Create(cam, new Vector3(0.76f, 0.40f, 0f));
            GameAudio.SetMusic(GameAudio.Music.Lobby);

            var canvas = UiKit.CreateCanvas("MenuCanvas");
            var title = UiKit.Shout(canvas, "FALL RUSH", 104, UiKit.Magenta);
            var trt = (RectTransform)title.transform;
            trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0.5f, 1f);
            trt.anchoredPosition = new Vector2(-170f, -26f);
            trt.sizeDelta = new Vector2(760f, 130f);
            var tag = UiKit.Label(canvas, "12 wind-up capsule toys · 4 chaotic rounds · one crown", 20, UiKit.Ink, FontStyle.Bold);
            var tgrt = (RectTransform)tag.transform;
            tgrt.anchorMin = tgrt.anchorMax = tgrt.pivot = new Vector2(0.5f, 1f);
            tgrt.anchoredPosition = new Vector2(-170f, -148f);
            tgrt.sizeDelta = new Vector2(760f, 30f);

            var panel = UiKit.Panel(canvas, new Vector2(560, 548), UiKit.CloudWhite);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = new Vector2(-170f, -64f);
            panel.GetComponent<VerticalLayoutGroup>().spacing = 10;
            UiKit.Label(panel, "Practice match: you + bots, 4 rounds", 17, UiKit.Grape, FontStyle.Bold);
            NameField(panel);
            MyRushling(panel);
            var play = UiKit.Button(panel, "PLAY", UiKit.SunYellow, OnPlay);
            play.GetComponent<LayoutElement>().minHeight = 70;
            var row = UiKit.Row(panel, 56f, 12f, TextAnchor.MiddleCenter);
            row.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;
            var set = UiKit.Button(row, "Settings", UiKit.SkyLight, () => _settings.SetActive(!_settings.activeSelf));
            set.GetComponent<LayoutElement>().preferredWidth = 200;
            _walletButton = UiKit.Button(row, "Connect Wallet", UiKit.Mint, OnConnectWallet);
            _walletButton.GetComponent<LayoutElement>().preferredWidth = 200;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var dev = UiKit.Button(panel, "Practice / tuning (dev)", UiKit.Grey, () => NetBootstrap.StartPractice("BlockPartyDash"));
            dev.GetComponent<LayoutElement>().minHeight = 44;
            // G4.1: the sandbox's G4 section (ball lane + 7 × 7 lid grid, east of the spawn: walk to the edge, bridge at z −20).
            var g4 = UiKit.Button(panel, "Practice: G4 hazards sandbox (dev)", UiKit.Grey, () => NetBootstrap.StartPractice("Sandbox"));
            g4.GetComponent<LayoutElement>().minHeight = 44;
            // P1-N: the Feel Lab (Classic vs rush-core A/B, Scripts/FeelLab). Dev builds only; an in-process flag, so Classic is unchanged.
            var lab = UiKit.Button(panel, "Feel Lab: Classic vs rush-core (dev)", UiKit.Grey, () =>
            {
                System.AppDomain.CurrentDomain.SetData("fallrush.feellab", true);
                NetBootstrap.StartPractice("Sandbox");
            });
            lab.GetComponent<LayoutElement>().minHeight = 44;
            // P3: the on-chain Final against the local chain emulator / the rollup (Scripts/OnChain). Its own entry only.
            if (OnChainEntry.Available)
            {
                var oc = UiKit.Button(panel, "Play On-Chain (preview)", UiKit.Mint, () => OnChainEntry.Play());
                oc.GetComponent<LayoutElement>().minHeight = 44;
            }
#endif
            _wallet = UiKit.Label(panel, string.Empty, 14, UiKit.Ink, FontStyle.Normal, 20);
            _target = UiKit.Label(panel, string.Empty, 13, UiKit.Grey, FontStyle.Normal, 18);

            var foot = UiKit.Label(canvas, "WASD move · Space jump · Shift / left click dive · mouse look · 1-4 emotes · Esc settings", 15, UiKit.Ink, FontStyle.Bold);
            var frt = (RectTransform)foot.transform;
            frt.anchorMin = frt.anchorMax = frt.pivot = new Vector2(0.5f, 0f);
            frt.anchoredPosition = new Vector2(0f, 14f);
            frt.sizeDelta = new Vector2(1200f, 24f);
            var ver = UiKit.Label(canvas, $"build {Application.version} · protocol v{Protocol.Version}", 12, UiKit.Grape);
            var vrt = (RectTransform)ver.transform;
            vrt.anchorMin = vrt.anchorMax = vrt.pivot = new Vector2(1f, 0f);
            vrt.anchoredPosition = new Vector2(-14f, 12f);
            vrt.sizeDelta = new Vector2(320f, 20f);
            UiKit.Align(ver, TextAnchor.LowerRight);

            var sp = SettingsPanel.Build(canvas, new Vector2(600, 600));
            sp.anchorMin = sp.anchorMax = sp.pivot = new Vector2(1f, 0.5f);
            sp.anchoredPosition = new Vector2(-20f, 0f);
            _settings = sp.gameObject;
            _settings.SetActive(false);
            NetBootstrap.PreferredName = GameSettings.PlayerName;
            NetBootstrap.PreferredPreset = GameSettings.CosmeticPreset;
            ScreenFx.Open();
            var shot = Arg("-fallrushMenuShot"); // proof screenshot of the menu (dev tool): -fallrushMenuShot <png> [-fallrushMenuSettings]
            if (shot != null)
                StartCoroutine(Shot(shot, System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-fallrushMenuSettings") >= 0));

            var boot = NetBootstrap.Instance;
            if (boot == null)
            {
                _target.text = "NetBootstrap missing: start from the Boot scene.";
                return;
            }

            boot.ResolveTarget();
            _target.text = $"Server: {boot.Target.Url} (from {boot.TargetSource})";

            if (!WalletBridge.Available)
            {
                _walletButton.interactable = false;
                _wallet.text = "Wallet: WebGL only (smoke test).";
            }
            else
                _wallet.text = WalletBridge.ProviderLabel ?? string.Empty;
        }

        private static string Arg(string name)
        {
            var a = System.Environment.GetCommandLineArgs();
            for (var i = 0; i < a.Length - 1; i++)
                if (a[i] == name)
                    return a[i + 1];
            return null;
        }

        private System.Collections.IEnumerator Shot(string path, bool settings)
        {
            if (settings)
                _settings.SetActive(true);
            yield return new WaitForSecondsRealtime(2.5f);
            ScreenCapture.CaptureScreenshot(path);
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit(0);
        }

        /// <summary>G1 §3.6: the display name (saved locally; sent in hello unless the URL has ?name=).</summary>
        private static void NameField(Transform panel)
        {
            var go = TMP_DefaultControls.CreateInputField(default);
            go.transform.SetParent(panel, false);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 50f;
            le.preferredWidth = 420f;
            var bg = go.GetComponent<Image>();
            bg.sprite = UiKit.Round;
            bg.type = Image.Type.Sliced;
            bg.color = new Color(0.93f, 0.95f, 1f);
            var input = go.GetComponent<TMP_InputField>();
            input.characterLimit = GameSettings.MaxNameChars;
            foreach (var t in go.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                t.font = UiKit.Body;
                t.fontSize = 22;
                t.fontStyle = FontStyles.Bold;
                t.alignment = TextAlignmentOptions.Center;
                t.color = t.gameObject.name == "Placeholder" ? new Color(0.16f, 0.07f, 0.31f, 0.4f) : UiKit.Ink;
            }

            ((TMP_Text)input.placeholder).text = "Your name";
            input.text = GameSettings.PlayerName;
            input.onEndEdit.AddListener(v =>
            {
                GameSettings.PlayerName = v;
                GameSettings.Save();
                input.text = GameSettings.PlayerName;
                NetBootstrap.PreferredName = GameSettings.PlayerName;
            });
        }

        /// <summary>G2.2 "My Rushling": ◀ preset ▶ with a shell swatch and what it wears (the 3D preview shows it).</summary>
        private void MyRushling(Transform panel)
        {
            var row = UiKit.Row(panel, 58f, 10f, TextAnchor.MiddleCenter);
            var prev = UiKit.Button(row, "◀", UiKit.Magenta, () => Step(-1));
            prev.GetComponent<LayoutElement>().preferredWidth = 58;
            var mid = new GameObject("Preset", typeof(RectTransform), typeof(LayoutElement), typeof(VerticalLayoutGroup));
            mid.transform.SetParent(row, false);
            mid.GetComponent<LayoutElement>().preferredWidth = 290;
            var v = mid.GetComponent<VerticalLayoutGroup>();
            v.childControlHeight = v.childControlWidth = true;
            v.childForceExpandHeight = false;
            v.spacing = 0;
            var top = UiKit.Row(mid.transform, 30f, 8f, TextAnchor.MiddleCenter);
            _presetSwatch = UiKit.Dot(top, Color.white, 20f);
            _presetName = UiKit.Label(top, string.Empty, 26, UiKit.Ink, FontStyle.Bold);
            _presetName.GetComponent<LayoutElement>().preferredWidth = 220;
            _presetInfo = UiKit.Label(mid.transform, string.Empty, 13, UiKit.Grape, FontStyle.Normal, 18);
            var next = UiKit.Button(row, "▶", UiKit.Magenta, () => Step(1));
            next.GetComponent<LayoutElement>().preferredWidth = 58;
            ShowPreset();
        }

        private void Step(int d)
        {
            GameSettings.CosmeticPreset = (GameSettings.CosmeticPreset + d + CosmeticPresets.Count) % CosmeticPresets.Count;
            GameSettings.Save();
            NetBootstrap.PreferredPreset = GameSettings.CosmeticPreset;
            GameAudio.Ui(GameAudio.UiCue.Pop);
            ShowPreset();
        }

        private void ShowPreset()
        {
            var p = CosmeticPresets.Get(GameSettings.CosmeticPreset);
            _presetName.text = $"My Rushling: {p.Name}";
            _presetSwatch.color = UiKit.Hex(p.Shell);
            _presetInfo.text = $"{p.Emblem} emblem · {Topper(p.Topper)} · {p.Face.ToString().ToLowerInvariant()} face · {p.Key.ToString().ToLowerInvariant()} key";
        }

        private static string Topper(CosmeticPresets.Topper t) => t switch
        {
            CosmeticPresets.Topper.ChefHat => "chef hat",
            CosmeticPresets.Topper.Propeller => "propeller cap",
            CosmeticPresets.Topper.Headphones => "headphones",
            _ => "aviator goggles",
        };

        private void OnPlay()
        {
            var boot = NetBootstrap.Instance;
            if (boot == null)
                return;
            boot.Connect();
            ScreenFx.Close();
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
