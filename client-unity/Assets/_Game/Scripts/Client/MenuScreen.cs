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
    /// P7-UX (OC-48): the pre-match flow, built like a game's front end (it was one dev-style panel):
    ///   1. Title: the logo over the character ring, "click to play" (the click also unlocks WebGL audio);
    ///   2. Character ring: all 12 Rushlings on a turntable, A / D or ◀ ▶ (drag, scroll) to turn, your name, SELECT;
    ///   3. Play hub: your Rushling on a pedestal, your tier, and three modes: QUICK MATCH (skill-tier matchmaking, bots
    ///      fill the empty seats), CREATE ROOM (a private room with an invite code, bots fill to 12 when the host starts),
    ///      JOIN ROOM (a code, or ?room= from an invite link). One big PLAY.
    /// Dev items (?dev=1) sit behind a Dev button. Cosmetics only, no stats (doc 01 "skill over spending").
    /// </summary>
    public sealed class MenuScreen : MonoBehaviour
    {
        private enum Page : byte { Title, Pick, Hub, Busy }
        private enum Mode : byte { Quick, Create, Join }

        private TMP_Text _target;
        private TMP_Text _wallet;
        private TMP_Text _presetName, _presetInfo, _hubName, _hubTier, _hubTierInfo, _status, _busyText, _playLabel;
        private Image _presetSwatch;
        private Button _walletButton, _play;
        private bool _walletBusy;
        private GameObject _settings;
        private static bool _quickPlayed, _seenTitle;
        private RushlingRing _ring;
        private RectTransform _titleGroup, _pickGroup, _hubGroup, _busyGroup, _devPanel;
        private RectTransform _logo, _tagline;
        private TMP_InputField _codeField;
        private readonly UiKit.Card[] _modeCards = new UiKit.Card[3];
        private readonly Button[] _mixButtons = new Button[3];
        private Page _screen = (Page)255;
        private Mode _mode = Mode.Quick;
        private int _botMix = 1;
        private CapsuleMachineWidget _busyMachine;
        private float _dragX;
        private bool _dragging;
        private static readonly string[] MixNames = { "Easy", "Mixed", "Hard" };

        private void Start()
        {
            var cam = Camera.main;
            UiStage.Ensure(cam);
            _ring = RushlingRing.Create(cam);
            GameAudio.SetMusic(GameAudio.Music.Lobby);
            NetBootstrap.SkillTier = PlayerTier.Tier;

            var canvas = UiKit.CreateCanvas("MenuCanvas");
            BuildTitle(canvas);
            BuildPick(canvas);
            BuildHub(canvas);
            BuildBusy(canvas);

            var ver = UiKit.Label(canvas, $"build {Application.version} · protocol v{Protocol.Version} · devnet", 11, UiKit.Grape);
            var vrt = (RectTransform)ver.transform;
            vrt.anchorMin = vrt.anchorMax = vrt.pivot = new Vector2(1f, 0f);
            vrt.anchoredPosition = new Vector2(-14f, 8f);
            vrt.sizeDelta = new Vector2(320f, 18f);
            UiKit.Align(ver, TextAnchor.LowerRight);

            var sp = SettingsPanel.Build(canvas, () => UiMotion.SetVisible((RectTransform)_settings.transform, false));
            _settings = sp.gameObject;
            _settings.SetActive(false);
            NetBootstrap.PreferredName = GameSettings.PlayerName;
            NetBootstrap.PreferredPreset = GameSettings.CosmeticPreset;
            ScreenFx.Open();

            // an invite link (?room=CODE) goes ring → join; the old Quick Play URL (?quickplay=1, /play) joins at once
            var invite = Protocol.CleanRoom(NetTargets.GetQueryParam(Application.absoluteURL, "room"));
            if (invite != null)
            {
                _codeField.text = invite;
                _mode = Mode.Join;
            }

            if (!_quickPlayed && NetTargets.GetQueryParam(Application.absoluteURL, "quickplay") == "1")
            {
                _quickPlayed = true;
                // submission (judge mode): /play?judge=1 gets a judge room from /api/judge and passes ?room=&server=
                // with ?quickplay=1: join that room at once (no title, ring or hub click)
                if (invite != null)
                    NetBootstrap.RoomCode = invite;
                Show(Page.Hub);
                Invoke(nameof(Connect), 0.4f);
            }
            else
                Show(_seenTitle ? Page.Hub : invite != null ? Page.Pick : Page.Title);
            _seenTitle = true;

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
            _target.text = DevMenu.Enabled ? $"Server: {boot.Target.Url} (from {boot.TargetSource})" : string.Empty;
            if (!WalletBridge.Available)
            {
                _walletButton.interactable = false;
                _wallet.text = DevMenu.Enabled ? "Wallet: WebGL only (smoke test)." : string.Empty;
            }
            else
                _wallet.text = DevMenu.Enabled ? WalletBridge.ProviderLabel ?? string.Empty : string.Empty;
        }

        // ------------------------------------------------------------------ screens

        private static RectTransform Group(Transform canvas, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasGroup));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas, false);
            UiKit.Stretch(rt);
            return rt;
        }

        private static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        private void BuildTitle(Transform canvas)
        {
            _titleGroup = Group(canvas, "Title");
            var logo = UiKit.Shout(_titleGroup, "FALL RUSH", 132, UiKit.Magenta);
            _logo = Place((RectTransform)logo.transform, new Vector2(0.5f, 1f), new Vector2(0f, -34f), new Vector2(900f, 160f));
            var tag = UiKit.Label(_titleGroup, "12 capsule toys · 4 rounds · one crown · on Solana", 22, UiKit.Ink, FontStyle.Bold);
            _tagline = Place((RectTransform)tag.transform, new Vector2(0.5f, 1f), new Vector2(0f, -184f), new Vector2(900f, 32f));
            var go = UiKit.Button(_titleGroup, "CLICK TO PLAY", UiKit.SunYellow, () => Show(Page.Pick));
            Place((RectTransform)go.transform, new Vector2(0.5f, 0f), new Vector2(0f, 46f), new Vector2(380f, 74f));
            go.GetComponentInChildren<TMP_Text>().fontSize = 34;
        }

        private void BuildPick(Transform canvas)
        {
            _pickGroup = Group(canvas, "Pick");
            var head = UiKit.Shout(_pickGroup, "CHOOSE YOUR RUSHLING", 46, UiKit.SunYellow);
            Place((RectTransform)head.transform, new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(900f, 64f));
            var sub = UiKit.Label(_pickGroup, "Looks only: every Rushling plays the same", 16, UiKit.Ink, FontStyle.Bold);
            Place((RectTransform)sub.transform, new Vector2(0.5f, 1f), new Vector2(0f, -84f), new Vector2(700f, 24f));
            UiKit.RoundButton(_pickGroup, UiIcon.None, "◀", UiKit.Magenta, 76f, () => Turn(-1), Color.white);
            Place((RectTransform)_pickGroup.GetChild(_pickGroup.childCount - 1), new Vector2(0.5f, 0.5f), new Vector2(-330f, 10f), new Vector2(76f, 76f));
            UiKit.RoundButton(_pickGroup, UiIcon.None, "▶", UiKit.Magenta, 76f, () => Turn(1), Color.white);
            Place((RectTransform)_pickGroup.GetChild(_pickGroup.childCount - 1), new Vector2(0.5f, 0.5f), new Vector2(330f, 10f), new Vector2(76f, 76f));

            var card = UiKit.MakeCard(_pickGroup, UiKit.CardSize.M, UiKit.CardHue.Grape, "MY RUSHLING", new Vector2(520f, 196f), UiIcon.Capsule);
            Place(card.Root, new Vector2(0.5f, 0f), new Vector2(0f, 14f), new Vector2(520f, 196f));
            var top = UiKit.Row(card.Body, 40f, 10f, TextAnchor.MiddleCenter);
            _presetSwatch = UiKit.Dot(top, Color.white, 22f);
            _presetName = UiKit.Label(top, string.Empty, 32, UiKit.Ink, FontStyle.Bold, 40);
            _presetName.GetComponent<LayoutElement>().preferredWidth = 260;
            _presetInfo = UiKit.Label(card.Body, string.Empty, 14, UiKit.Grape, FontStyle.Bold, 20);
            var row = UiKit.Row(card.Body, 54f, 10f, TextAnchor.MiddleCenter);
            NameField(row, 250f);
            var select = UiKit.Button(row, "SELECT", UiKit.SunYellow, () => Show(Page.Hub));
            var sle = select.GetComponent<LayoutElement>();
            sle.preferredWidth = sle.minWidth = 180f;
            sle.minHeight = 52f;
            var keys = UiKit.Row(_pickGroup, 26f, 10f, TextAnchor.MiddleCenter);
            Place(keys, new Vector2(1f, 0f), new Vector2(-20f, 20f), new Vector2(240f, 26f));
            UiKit.KeyHint(keys, "A D", "Turn", 14, 24f);
            UiKit.KeyHint(keys, "Enter", "Select", 14, 24f);
            ShowPreset();
        }

        private void BuildHub(Transform canvas)
        {
            _hubGroup = Group(canvas, "Hub");
            // top bar: logo · tier · profile · season · settings
            var logo = UiKit.Shout(_hubGroup, "FALL RUSH", 54, UiKit.Magenta);
            Place((RectTransform)logo.transform, new Vector2(0f, 1f), new Vector2(24f, -12f), new Vector2(360f, 70f));
            logo.alignment = TextAlignmentOptions.Left;
            var bar = UiKit.Row(_hubGroup, 40f, 8f, TextAnchor.MiddleRight);
            Place(bar, new Vector2(1f, 1f), new Vector2(-18f, -20f), new Vector2(560f, 40f));
            bar.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = false;
            TopButton(bar, "Profile", () => Application.OpenURL("/me"));
            TopButton(bar, "Season", () => Application.OpenURL("/season"));
            TopButton(bar, "Watch", () => Application.OpenURL("/live"));
            TopButton(bar, "Settings", () => UiMotion.SetVisible((RectTransform)_settings.transform, !UiMotion.IsShown((RectTransform)_settings.transform)));

            // left: the three mode cards
            var col = new GameObject("Modes", typeof(RectTransform), typeof(VerticalLayoutGroup)).GetComponent<RectTransform>();
            col.SetParent(_hubGroup, false);
            Place(col, new Vector2(0f, 0.5f), new Vector2(24f, -10f), new Vector2(470f, 520f));
            var v = col.GetComponent<VerticalLayoutGroup>();
            v.spacing = 16;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            _modeCards[0] = ModeCard(col, Mode.Quick, UiKit.CardHue.Soda, "QUICK MATCH", UiIcon.Race, 150f);
            _hubTier = UiKit.Label(_modeCards[0].Body, string.Empty, 20, UiKit.Ink, FontStyle.Bold, 26);
            _hubTier.alignment = TextAlignmentOptions.Left;
            var q = UiKit.Label(_modeCards[0].Body, "Matched with players of your tier · bots fill empty seats", 15, UiKit.Ink, FontStyle.Normal, 22);
            q.alignment = TextAlignmentOptions.Left;
            _hubTierInfo = UiKit.Label(_modeCards[0].Body, string.Empty, 13, UiKit.Grape, FontStyle.Bold, 18);
            _hubTierInfo.alignment = TextAlignmentOptions.Left;

            _modeCards[1] = ModeCard(col, Mode.Create, UiKit.CardHue.Grape, "CREATE ROOM", UiIcon.Link, 150f);
            var c = UiKit.Label(_modeCards[1].Body, "A private room · invite friends with a code · you start it", 15, UiKit.Ink, FontStyle.Normal, 22);
            c.alignment = TextAlignmentOptions.Left;
            var mix = UiKit.Row(_modeCards[1].Body, 34f, 6f);
            var ml = UiKit.Label(mix, "Bots", 15, UiKit.Ink, FontStyle.Bold, 30);
            ml.GetComponent<LayoutElement>().preferredWidth = 50f;
            for (var i = 0; i < 3; i++)
            {
                var ii = i;
                _mixButtons[i] = UiKit.Button(mix, MixNames[i], UiKit.SkyLight, () => { _botMix = ii; _mode = Mode.Create; RefreshHub(); });
                var le = _mixButtons[i].GetComponent<LayoutElement>();
                le.preferredWidth = le.minWidth = 104f;
                le.minHeight = le.preferredHeight = 32f;
                var t = _mixButtons[i].GetComponentInChildren<TMP_Text>();
                t.fontSize = 16;
                t.enableAutoSizing = false;
            }

            _modeCards[2] = ModeCard(col, Mode.Join, UiKit.CardHue.Bubblegum, "JOIN ROOM", UiIcon.Capsule, 136f);
            var jr = UiKit.Row(_modeCards[2].Body, 50f, 10f);
            _codeField = CodeField(jr);
            var j = UiKit.Label(jr, "Type the code from your friend's invite", 14, UiKit.Ink, FontStyle.Normal, 40);
            j.alignment = TextAlignmentOptions.Left;
            j.GetComponent<LayoutElement>().preferredWidth = 220f;

            // right: your Rushling (the ring in solo mode) + its name plate + change
            var plate = UiKit.MakeCard(_hubGroup, UiKit.CardSize.S, UiKit.CardHue.Lemon, "YOU", new Vector2(330f, 92f), UiIcon.Crown);
            Place(plate.Root, new Vector2(1f, 0f), new Vector2(-310f, 128f), new Vector2(330f, 92f));
            var pr = UiKit.Row(plate.Body, 50f, 10f);
            _hubName = UiKit.Label(pr, string.Empty, 22, UiKit.Ink, FontStyle.Bold, 46);
            _hubName.alignment = TextAlignmentOptions.Left;
            _hubName.GetComponent<LayoutElement>().preferredWidth = 190f;
            var change = UiKit.Button(pr, "Change", UiKit.SkyLight, () => Show(Page.Pick));
            var cle = change.GetComponent<LayoutElement>();
            cle.preferredWidth = cle.minWidth = 100f;
            cle.minHeight = 40f;
            change.GetComponentInChildren<TMP_Text>().fontSize = 18;

            // the big PLAY, bottom-right
            _play = UiKit.Button(_hubGroup, "PLAY", UiKit.SunYellow, OnPlay);
            Place((RectTransform)_play.transform, new Vector2(1f, 0f), new Vector2(-24f, 26f), new Vector2(270f, 84f));
            _playLabel = _play.GetComponentInChildren<TMP_Text>();
            _playLabel.fontSize = 40;
            _status = UiKit.Label(_hubGroup, string.Empty, 15, UiKit.Magenta, FontStyle.Bold, 22);
            Place((RectTransform)_status.transform, new Vector2(1f, 0f), new Vector2(-24f, 4f), new Vector2(560f, 22f));
            _status.alignment = TextAlignmentOptions.Right;

            // dev (?dev=1): the old dev entries behind one button
            _wallet = UiKit.Label(_hubGroup, string.Empty, 12, UiKit.Ink, FontStyle.Normal, 18);
            Place((RectTransform)_wallet.transform, new Vector2(0f, 0f), new Vector2(24f, 30f), new Vector2(460f, 18f));
            _target = UiKit.Label(_hubGroup, string.Empty, 12, UiKit.Grey, FontStyle.Normal, 18);
            Place((RectTransform)_target.transform, new Vector2(0f, 0f), new Vector2(24f, 10f), new Vector2(460f, 18f));
            _devPanel = UiKit.Panel(_hubGroup, new Vector2(420f, 520f), UiKit.CloudWhite);
            Place(_devPanel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(420f, 520f));
            _walletButton = UiKit.Button(_devPanel, "Connect Wallet (dev)", UiKit.Mint, OnConnectWallet);
            if (DevMenu.Enabled || NetBootstrap.PracticeAllowed)
            {
                // 2026-11-24: practice on every map (localhost / dev builds only, see NetBootstrap.PracticeAllowed)
                foreach (var (label, key) in new[] { ("Capsule Run", "BlockPartyDash"), ("Goo Tower", "GooTower"), ("Star Fountain", "StarScramble"), ("Crumble Shelf", "CrumbleShelf"), ("Hazard sandbox", "Sandbox") })
                    UiKit.Button(_devPanel, "Practice: " + label, UiKit.Grey, () => NetBootstrap.StartPractice(key)).GetComponent<LayoutElement>().minHeight = 40;
                UiKit.Button(_devPanel, "Feel Lab (dev)", UiKit.Grey, () =>
                {
                    System.AppDomain.CurrentDomain.SetData("fallrush.feellab", true);
                    NetBootstrap.StartPractice("Sandbox");
                }).GetComponent<LayoutElement>().minHeight = 44;
                if (OnChainEntry.Available)
                    UiKit.Button(_devPanel, "On-chain Final only (dev)", UiKit.Grey, () => OnChainEntry.Play()).GetComponent<LayoutElement>().minHeight = 44;
                TopButton(bar, DevMenu.Enabled ? "Dev" : "Practice", () => _devPanel.gameObject.SetActive(!_devPanel.gameObject.activeSelf));
            }

            _devPanel.gameObject.SetActive(false);
        }

        private void BuildBusy(Transform canvas)
        {
            _busyGroup = Group(canvas, "Busy");
            var card = UiKit.MakeCard(_busyGroup, UiKit.CardSize.L, UiKit.CardHue.Soda, "FINDING A MATCH", new Vector2(520f, 330f), UiIcon.Capsule);
            Place(card.Root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(520f, 330f));
            var mslot = new GameObject("MachineSlot", typeof(RectTransform), typeof(LayoutElement)).GetComponent<RectTransform>();
            mslot.SetParent(card.Body, false);
            var mle = mslot.GetComponent<LayoutElement>();
            mle.minHeight = mle.preferredHeight = 150f;
            _busyMachine = CapsuleMachineWidget.Create(mslot, new Vector2(120f, 150f));
            _busyText = UiKit.Label(card.Body, string.Empty, 20, UiKit.Ink, FontStyle.Bold, 56);
            var cancel = UiKit.Button(card.Body, "Cancel", UiKit.SkyLight, () => { StopAllCoroutines(); Show(Page.Hub); });
            cancel.GetComponent<LayoutElement>().minHeight = 46;
        }

        private UiKit.Card ModeCard(Transform col, Mode mode, UiKit.CardHue hue, string title, UiIcon icon, float height)
        {
            var slot = new GameObject("ModeSlot", typeof(RectTransform), typeof(LayoutElement)).GetComponent<RectTransform>();
            slot.SetParent(col, false);
            var le = slot.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = height;
            var card = UiKit.MakeCard(slot, UiKit.CardSize.M, hue, title, new Vector2(470f, height), icon);
            UiKit.Stretch(card.Root);
            card.Body.GetComponent<VerticalLayoutGroup>().childAlignment = TextAnchor.UpperLeft;
            var b = card.Fill.gameObject.AddComponent<Button>();
            card.Fill.raycastTarget = true;
            b.targetGraphic = card.Fill;
            b.transition = Selectable.Transition.None;
            b.onClick.AddListener(() => { _mode = mode; GameAudio.Ui(GameAudio.UiCue.Tick); UiMotion.Bump(card.Root); RefreshHub(); });
            return card;
        }

        private static void TopButton(Transform bar, string text, UnityEngine.Events.UnityAction a)
        {
            var b = UiKit.Button(bar, text, UiKit.Cream, a);
            var le = b.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = text.Length * 11f + 40f;
            le.minHeight = le.preferredHeight = 38f;
            var t = b.GetComponentInChildren<TMP_Text>();
            t.fontSize = 17;
            t.enableAutoSizing = false;
        }

        private TMP_InputField CodeField(Transform row)
        {
            var go = TMP_DefaultControls.CreateInputField(default);
            go.transform.SetParent(row, false);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 48f;
            le.preferredWidth = le.minWidth = 170f;
            var bg = go.GetComponent<Image>();
            bg.sprite = UiKit.Round;
            bg.type = Image.Type.Sliced;
            bg.color = Color.white;
            var input = go.GetComponent<TMP_InputField>();
            input.characterLimit = Protocol.MaxRoomBytes;
            input.characterValidation = TMP_InputField.CharacterValidation.Alphanumeric;
            foreach (var t in go.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                t.font = UiKit.Display;
                t.fontSize = 28;
                t.alignment = TextAlignmentOptions.Center;
                t.characterSpacing = 8f;
                t.color = t.gameObject.name == "Placeholder" ? new Color(0.16f, 0.07f, 0.31f, 0.35f) : UiKit.Ink;
            }

            ((TMP_Text)input.placeholder).text = "CODE";
            input.onValueChanged.AddListener(v =>
            {
                var up = v.ToUpperInvariant();
                if (up != v)
                    input.text = up;
                _mode = Mode.Join;
                RefreshHub();
            });
            input.onSubmit.AddListener(_ => OnPlay());
            return input;
        }

        private void Show(Page s)
        {
            if (_screen == s)
                return;
            _screen = s;
            GameAudio.Ui(GameAudio.UiCue.Whoosh);
            SetGroup(_titleGroup, s == Page.Title);
            SetGroup(_pickGroup, s == Page.Pick);
            SetGroup(_hubGroup, s == Page.Hub);
            SetGroup(_busyGroup, s == Page.Busy);
            _ring.Layout(s == Page.Hub || s == Page.Busy, false, s == Page.Pick);
            if (s == Page.Title)
            {
                UiMotion.SlideIn(_logo, new Vector2(0f, 160f));
                UiMotion.Show(_tagline, 0.12f);
            }

            if (s == Page.Hub)
            {
                GameSettings.Save();
                NetBootstrap.PreferredPreset = GameSettings.CosmeticPreset;
                RefreshHub();
            }

            if (s == Page.Busy)
                _busyMachine.Spin(true);
        }

        private static void SetGroup(RectTransform g, bool on)
        {
            if (on)
            {
                g.gameObject.SetActive(true);
                UiMotion.Show(g);
            }
            else if (g.gameObject.activeSelf)
                g.gameObject.SetActive(false);
        }

        private void Turn(int d)
        {
            _ring.Step(d);
            GameSettings.CosmeticPreset = _ring.Picked;
            NetBootstrap.PreferredPreset = GameSettings.CosmeticPreset;
            GameAudio.Ui(GameAudio.UiCue.Pop);
            ShowPreset();
        }

        private void Update()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var typing = _codeField != null && _codeField.isFocused || UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject?.GetComponent<TMP_InputField>() != null;
            switch (_screen)
            {
                case Page.Title:
                    if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame))
                        Show(Page.Pick);
                    break;
                case Page.Pick:
                    if (kb != null && !typing)
                    {
                        if (kb.aKey.wasPressedThisFrame || kb.leftArrowKey.wasPressedThisFrame)
                            Turn(-1);
                        if (kb.dKey.wasPressedThisFrame || kb.rightArrowKey.wasPressedThisFrame)
                            Turn(1);
                        if (kb.enterKey.wasPressedThisFrame)
                            Show(Page.Hub);
                    }

                    // drag (over the 3D ring, not on a panel) or scroll turns the ring
                    if (mouse != null)
                    {
                        var overUi = UnityEngine.EventSystems.EventSystem.current != null && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
                        if (mouse.leftButton.wasPressedThisFrame && !overUi)
                        {
                            _dragging = true;
                            _dragX = 0f;
                        }

                        if (_dragging && mouse.leftButton.isPressed)
                        {
                            var dx = mouse.delta.ReadValue().x;
                            _dragX += dx;
                            _ring.Nudge(dx * 0.25f);
                        }
                        else if (_dragging)
                        {
                            _dragging = false;
                            _ring.Snap();
                            if (_ring.Picked != GameSettings.CosmeticPreset)
                            {
                                GameSettings.CosmeticPreset = _ring.Picked;
                                NetBootstrap.PreferredPreset = GameSettings.CosmeticPreset;
                                ShowPreset();
                            }
                        }

                        var sc = mouse.scroll.ReadValue().y;
                        if (Mathf.Abs(sc) > 0.1f && !overUi)
                            Turn(sc > 0f ? -1 : 1);
                    }

                    break;
                case Page.Hub:
                    if (kb != null && kb.enterKey.wasPressedThisFrame && !typing)
                        OnPlay();
                    break;
            }
        }

        private void RefreshHub()
        {
            if (_hubName == null)
                return;
            var p = CosmeticPresets.Get(GameSettings.CosmeticPreset);
            var nm = string.IsNullOrWhiteSpace(GameSettings.PlayerName) ? "Guest" : GameSettings.PlayerName;
            _hubName.text = $"{nm}\n<size=15><color=#7B2FF7>{p.Name}</color></size>";
            _hubTier.text = $"Your tier: <color=#7B2FF7>{PlayerTier.Name}</color>";
            _hubTierInfo.text = PlayerTier.Tier < 2 ? $"{PlayerTier.ToNext} pts to {PlayerTier.Names[PlayerTier.Tier + 1]} · rounds +1, Finals +4, crowns +10" : $"Top tier · {PlayerTier.Crowns} crowns";
            for (var i = 0; i < 3; i++)
            {
                var on = (int)_mode == i;
                var card = _modeCards[i];
                card.Rim.color = on ? UiKit.SunYellow : UiKit.CardRim(card.Hue);
                card.Root.localScale = Vector3.one * (on ? 1.02f : 1f);
            }

            for (var i = 0; i < 3; i++)
            {
                var face = _mixButtons[i].transform.Find("Face/Fill");
                if (face != null)
                    face.GetComponent<Image>().color = i == _botMix ? UiKit.SunYellow : UiKit.SkyLight;
            }

            _playLabel.text = _mode == Mode.Quick ? "PLAY" : _mode == Mode.Create ? "CREATE" : "JOIN";
        }

        // ------------------------------------------------------------------ actions

        private void OnPlay()
        {
            if (_screen != Page.Hub)
                return;
            _status.text = string.Empty;
            switch (_mode)
            {
                case Mode.Quick:
                    StartCoroutine(QuickMatch());
                    break;
                case Mode.Create:
                    StartCoroutine(CreateRoom());
                    break;
                case Mode.Join:
                    var code = Protocol.CleanRoom(_codeField.text);
                    if (code == null)
                    {
                        _status.text = "Room codes are 4 letters or numbers";
                        UiMotion.Shake(_modeCards[2].Root);
                        return;
                    }

                    StartCoroutine(JoinRoom(code));
                    break;
            }
        }

        [System.Serializable] private sealed class MmReply { public bool ok; public string url, gs, reason, error, lobbyTier, phase; public int humans, size; }
        [System.Serializable] private sealed class RoomReply { public bool ok; public string code, url, gs, error, phase; public bool onchainFinal, onchainAll; public int humans; }

        private static bool HasApi => Application.platform == RuntimePlatform.WebGLPlayer || !string.IsNullOrEmpty(Arg("-fallrushApi"));

        private static string Api(string path)
        {
            var b = Arg("-fallrushApi");
            return (string.IsNullOrEmpty(b) ? string.Empty : b.TrimEnd('/')) + "/api" + path;
        }

        private System.Collections.IEnumerator Call(string method, string path, string body, System.Action<long, string> done)
        {
            using var req = new UnityEngine.Networking.UnityWebRequest(Api(path), method);
            req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
            if (body != null)
            {
                req.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
                req.SetRequestHeader("content-type", "application/json");
            }

            req.timeout = 10;
            yield return req.SendWebRequest();
            done(req.responseCode, req.downloadHandler?.text ?? string.Empty);
        }

        private System.Collections.IEnumerator QuickMatch()
        {
            Show(Page.Busy);
            _busyText.text = $"Finding a <color=#7B2FF7>{PlayerTier.Name}</color> lobby…";
            NetBootstrap.RoomCode = null;
            NetBootstrap.HubServerUrl = null;
            if (HasApi)
            {
                MmReply r = null;
                yield return Call("POST", "/mm", $"{{\"tier\":\"{PlayerTier.Name}\"}}", (code, text) => r = Parse<MmReply>(text));
                if (r != null && r.ok && !string.IsNullOrEmpty(r.url))
                {
                    NetBootstrap.HubServerUrl = r.url;
                    _busyText.text = r.humans > 0 ? $"Found a lobby: {r.humans} / {r.size} players\n<size=15>bots fill the empty seats</size>" : "Opening a new lobby\n<size=15>bots fill the empty seats</size>";
                }
                else
                    _busyText.text = "Joining the arena…";
            }

            yield return new WaitForSecondsRealtime(0.6f);
            Connect();
        }

        private System.Collections.IEnumerator CreateRoom()
        {
            if (!HasApi)
            {
                _status.text = "Rooms need the web build (or -fallrushApi)";
                yield break;
            }

            Show(Page.Busy);
            _busyText.text = "Opening your room…";
            RoomReply r = null;
            long status = 0;
            yield return Call("POST", "/room", $"{{\"botMix\":{_botMix}}}", (code, text) => { status = code; r = Parse<RoomReply>(text); });
            if (r == null || !r.ok || string.IsNullOrEmpty(r.url))
            {
                Show(Page.Hub);
                _status.text = r?.error ?? $"Could not open a room ({status})";
                yield break;
            }

            NetBootstrap.RoomCode = r.code;
            NetBootstrap.HubServerUrl = r.url;
            _busyText.text = $"Room <color=#7B2FF7>{r.code}</color> is ready\n<size=15>{(r.onchainAll ? "every round runs on-chain" : r.onchainFinal ? "the Final runs on-chain" : "Classic arena")}</size>";
            yield return new WaitForSecondsRealtime(0.6f);
            Connect();
        }

        private System.Collections.IEnumerator JoinRoom(string code)
        {
            if (!HasApi)
            {
                NetBootstrap.RoomCode = code; // desktop dev: the configured server
                Connect();
                yield break;
            }

            Show(Page.Busy);
            _busyText.text = $"Looking for room <color=#7B2FF7>{code}</color>…";
            RoomReply r = null;
            yield return Call("GET", "/room/" + code, null, (c, text) => r = Parse<RoomReply>(text));
            if (r == null || !r.ok || string.IsNullOrEmpty(r.url))
            {
                Show(Page.Hub);
                _status.text = r?.error ?? "No room with that code";
                UiMotion.Shake(_modeCards[2].Root);
                yield break;
            }

            NetBootstrap.RoomCode = r.code;
            NetBootstrap.HubServerUrl = r.url;
            Connect();
        }

        private static T Parse<T>(string json) where T : class
        {
            try
            {
                return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<T>(json);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private void Connect()
        {
            var boot = NetBootstrap.Instance;
            if (boot == null)
                return;
            NetBootstrap.SkillTier = PlayerTier.Tier;
            boot.ResolveTarget();
            boot.Connect();
            ScreenFx.Close();
            SceneManager.LoadScene(NetBootstrap.LobbyScene);
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
        private static void NameField(Transform panel, float width = 420f)
        {
            var go = TMP_DefaultControls.CreateInputField(default);
            go.transform.SetParent(panel, false);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 50f;
            le.preferredWidth = le.minWidth = width;
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

        private void ShowPreset()
        {
            var p = CosmeticPresets.Get(GameSettings.CosmeticPreset);
            _presetName.text = p.Name;
            _presetSwatch.color = UiKit.Hex(p.Shell);
            _presetInfo.text = $"{p.Emblem} emblem · {Topper(p.Topper)} · {p.Face.ToString().ToLowerInvariant()} face · {p.Key.ToString().ToLowerInvariant()} key";
            if (_ring != null)
                _ring.Pick(GameSettings.CosmeticPreset, false);
        }

        private static string Topper(CosmeticPresets.Topper t) => t switch
        {
            CosmeticPresets.Topper.ChefHat => "chef hat",
            CosmeticPresets.Topper.Propeller => "propeller cap",
            CosmeticPresets.Topper.Headphones => "headphones",
            _ => "aviator goggles",
        };

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
