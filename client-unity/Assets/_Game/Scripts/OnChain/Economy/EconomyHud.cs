using System.Text;
using FallRush.Client;
using FallRush.Net;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace FallRush.OnChain
{
    /// <summary>
    /// P4 economy UI (G3 UiKit; Classic screens untouched): chips in the HUD corner, the bet panel (Rush Markets with
    /// odds, pool, your stake, ◆ program-settled / ● server-recorded), the drop bar for eliminated players and
    /// spectators during the on-chain Final, the Chaos Drop banner, the champion / receipt card (Crown + capsule
    /// rarity, payouts, Rollup ✓ → Solana ✓, Claim with Phantom), and an F3 section. B toggles the bet panel.
    /// </summary>
    public sealed class EconomyHud : MonoBehaviour
    {
        public static EconomyHud Instance { get; private set; }

        private RectTransform _canvas, _bet, _dropBar, _receipt;
        private TMP_Text _chips, _betTitle, _betBody, _dropInfo, _banner, _receiptText, _debug;
        private RectTransform _chipsPill, _bannerPill, _debugPanel;
        private readonly Button[] _dropButtons = new Button[5];
        private Button _claim, _backup;
        private readonly StringBuilder _sb = new StringBuilder(2048);
        private bool _betOpen;
        private int _betMarket;
        private int _seenDrops;
        private double _bannerUntil;
        private bool _showDebug;
        private string _receiptFor;

        public static readonly string[] Rarity = { "Common", "Rare", "Epic", "Legendary" };
        public static readonly Color[] RarityColour = { new Color(0.75f, 0.78f, 0.85f), UiKit.Sky, UiKit.Grape, UiKit.SunYellow };
        private static readonly string[] Labels = { "", "◆ program-settled", "● server-recorded", "● Classic fallback (refund)", "aborted (refund)", "no winner (refund)" };

        public static EconomyHud Ensure()
        {
            if (Instance != null)
                return Instance;
            var go = new GameObject("EconomyHud");
            DontDestroyOnLoad(go);
            return go.AddComponent<EconomyHud>();
        }

        private void Awake()
        {
            Instance = this;
            _canvas = UiKit.CreateCanvas("EconomyCanvas", 40);
            DontDestroyOnLoad(_canvas.gameObject); // the on-chain session loads the map scene after this HUD exists
            _chipsPill = Pill(_canvas, new Vector2(1f, 1f), new Vector2(-150f, -10f), new Vector2(230f, 40f), UiKit.Ink, out _chips, 18, UiKit.SunYellow);
            _bannerPill = Pill(_canvas, new Vector2(0.5f, 1f), new Vector2(0f, -132f), new Vector2(720f, 54f), UiKit.Grape, out _banner, 24, Color.white);
            _bannerPill.gameObject.SetActive(false);
            BuildBet();
            BuildDropBar();
            BuildReceipt();
            FallRush.Client.MatchHud.DealOddsProvider = DealOdds;
            _debugPanel = Pill(_canvas, new Vector2(0f, 0.5f), new Vector2(14f, -40f), new Vector2(560f, 300f), new Color(0.08f, 0.04f, 0.16f, 0.88f), out _debug, 13, Color.white);
            _debug.alignment = TextAlignmentOptions.TopLeft;
            _debugPanel.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (_canvas != null)
                Destroy(_canvas.gameObject);
        }

        private static RectTransform Pill(Transform canvas, Vector2 anchor, Vector2 pos, Vector2 size, Color fill, out TMP_Text text, int fontSize, Color textColor)
        {
            var go = new GameObject("EconPill", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            UiKit.Pill(rt, fill);
            text = UiKit.Label(rt, string.Empty, fontSize, textColor, FontStyle.Bold);
            text.alignment = TextAlignmentOptions.Center;
            text.richText = true;
            var trt = (RectTransform)text.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            UiKit.Stretch(trt, 8f);
            return rt;
        }

        private static Button Small(Transform row, string text, Color c, float w, UnityEngine.Events.UnityAction a)
        {
            var b = UiKit.Button(row, text, c, a);
            foreach (var t in b.GetComponentsInChildren<TMP_Text>(true))
            {
                t.fontSize = w < 100f ? 13 : 16;
                t.enableAutoSizing = false;
                t.textWrappingMode = TextWrappingModes.NoWrap;
            }
            var le = b.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = w;
            le.preferredHeight = le.minHeight = 34;
            return b;
        }

        private void BuildBet()
        {
            _bet = UiKit.Panel(_canvas, new Vector2(460f, 640f), UiKit.CloudWhite);
            _bet.anchorMin = _bet.anchorMax = _bet.pivot = new Vector2(1f, 0.5f);
            _bet.anchoredPosition = new Vector2(-16f, -20f);
            var tabs = UiKit.Row(_bet, 36f, 6f);
            for (var k = 0; k < 5; k++)
            {
                var kk = k;
                Small(tabs, new[] { "Champ", "Final", "Q1", "Q2", "Q3" }[k], UiKit.SkyLight, 70f, () => _betMarket = kk);
            }

            _betTitle = UiKit.Label(_bet, string.Empty, 20, UiKit.Grape, FontStyle.Bold, 30);
            _betTitle.richText = true;
            _betBody = UiKit.Label(_bet, string.Empty, 15, UiKit.Ink, FontStyle.Normal, 380);
            _betBody.richText = true;
            _betBody.alignment = TextAlignmentOptions.TopLeft;
            var r1 = UiKit.Row(_bet, 36f, 6f);
            for (var i = 0; i < 6; i++)
            {
                var ii = i;
                Small(r1, $"#{i + 1} +10", UiKit.SunYellow, 58f, () => PlaceOn(ii, 10));
            }

            var r2 = UiKit.Row(_bet, 36f, 6f);
            for (var i = 0; i < 6; i++)
            {
                var ii = i;
                Small(r2, $"#{i + 1} +50", UiKit.Amber, 58f, () => PlaceOn(ii, 50));
            }

            UiKit.Label(_bet, "B: bet panel · devnet test chips only, no real-money value", 12, UiKit.Grey);
            _bet.gameObject.SetActive(false);
        }

        private void BuildDropBar()
        {
            _dropBar = UiKit.Panel(_canvas, new Vector2(980f, 112f), new Color(1f, 1f, 1f, 0.94f));
            _dropBar.anchorMin = _dropBar.anchorMax = _dropBar.pivot = new Vector2(0.5f, 0f);
            _dropBar.anchoredPosition = new Vector2(0f, 168f);
            _dropBar.localScale = Vector3.one * 0.8f;
            var row = UiKit.Row(_dropBar, 40f, 10f, TextAnchor.MiddleCenter);
            for (var k = 1; k <= 4; k++)
            {
                var kk = k;
                _dropButtons[k] = Small(row, ChaosDropFx.Names[k], k == 1 ? UiKit.SunYellow : k == 2 ? UiKit.Magenta : k == 3 ? UiKit.Sky : UiKit.Amber, 210f, () => ChainEconomy.Instance?.Drop(kk));
            }

            _dropInfo = UiKit.Label(_dropBar, string.Empty, 15, UiKit.Ink, FontStyle.Bold, 22);
            _dropInfo.alignment = TextAlignmentOptions.Center;
            _dropBar.gameObject.SetActive(false);
        }

        private void BuildReceipt()
        {
            _receipt = UiKit.Panel(_canvas, new Vector2(440f, 360f), UiKit.CloudWhite);
            _receipt.anchorMin = _receipt.anchorMax = _receipt.pivot = new Vector2(0f, 0.5f);
            _receipt.anchoredPosition = new Vector2(8f, -40f);
            _receipt.localScale = Vector3.one * 0.85f;
            _receiptText = UiKit.Label(_receipt, string.Empty, 14, UiKit.Ink, FontStyle.Normal, 260);
            _receiptText.richText = true;
            _receiptText.alignment = TextAlignmentOptions.TopLeft;
            _claim = UiKit.Button(_receipt, "Claim with Phantom", UiKit.Grape, Claim);
            // P5 0.4 burner safety: a guest wallet that holds a Crown / capsule lives only in this browser's storage;
            // "Back up your key" opens /me (same origin, same burner) with the export + copy warning
            _backup = UiKit.Button(_receipt, "Back up your key", UiKit.Magenta, BackupKey);
            _backup.gameObject.SetActive(false);
            _receipt.gameObject.SetActive(false);
        }

        private static string SlotName(int s)
        {
            var m = ClientSim.Instance != null ? ClientSim.Instance.Match : null;
            if (m != null && m.Slots != null)
                foreach (var x in m.Slots)
                    if (x.Slot == s && !string.IsNullOrEmpty(x.Name))
                        return x.Name;
            return $"Rushling {s + 1}";
        }

        private static string Short(string pk) => string.IsNullOrEmpty(pk) || pk.Length < 8 ? pk : pk.Substring(0, 4) + "…" + pk.Substring(pk.Length - 4);

        /// <summary>The outcomes of the current market, in panel order (slots).</summary>
        private static int[] Outcomes(EState s, int k)
        {
            if (s?.match == null)
                return System.Array.Empty<int>();
            var list = new System.Collections.Generic.List<int>();
            for (var i = 0; i < s.match.players && i < s.match.slots.Length; i++)
                if (s.match.slots[i] == 1 || s.match.slots[i] == 2)
                    list.Add(i);
            return list.ToArray();
        }

        private void PlaceOn(int row, int amount)
        {
            var e = ChainEconomy.Instance;
            var o = Outcomes(e?.State, _betMarket);
            if (e == null || row >= o.Length)
                return;
            e.Bet(_betMarket, o[row], amount);
        }

        private static void BackupKey() => Application.OpenURL("/me?backup=1");

        /// <summary>P5 0.6: the Deal screen's market line per capsule (Qualify R1–R3 for rounds 1–3, Champion for the Final).</summary>
        private static string DealOdds(int r, int rounds)
        {
            var st = ChainEconomy.Instance != null ? ChainEconomy.Instance.State : null;
            if (st?.markets == null)
                return string.Empty;
            var k = r == rounds - 1 ? 0 : 2 + r;
            if (k > 4 || k >= st.markets.Length || st.markets[k] == null || st.markets[k].status < 1)
                return string.Empty;
            var mk = st.markets[k];
            var pool = ChainEconomy.Parse(mk.pool);
            long top = 0;
            foreach (var x in mk.stake)
                top = System.Math.Max(top, ChainEconomy.Parse(x));
            var odds = top > 0 ? $" · fav x{pool / (double)top:0.0#}" : string.Empty;
            return $"\n<size=12><color=#7B2FF7>{(k == 0 ? "Champion" : "Qualify")} market: pool {ChainEconomy.Rush(pool)} RUSH{odds}</color></size>";
        }

        private void Claim()
        {
            var e = ChainEconomy.Instance;
            var r = e?.Receipt;
            if (r?.receipt == null)
                return;
            var phantom = ChainWallet.PhantomPubkey;
            if (string.IsNullOrEmpty(phantom))
            {
                ChainWallet.Connect();
                return;
            }

            for (var s = 0; s < 12 && s < r.receipt.wallets.Length; s++)
                if (r.receipt.wallets[s] == r.me)
                {
                    if (r.champion == s && (r.receipt.flags & 4) != 0)
                        e.Claim(r.crownAsset, phantom);
                    if (s < r.capsuleAssets.Length && !string.IsNullOrEmpty(r.capsuleAssets[s]) && (r.receipt.mintedMask >> s & 1) == 1)
                        e.Claim(r.capsuleAssets[s], phantom);
                }
        }

        private void Update()
        {
            if (_canvas == null)
                return;
            var e = ChainEconomy.Instance;
            var session = OnChainSession.Instance;
            var on = e != null && session != null && session.isActiveAndEnabled && e.Available && session.Cfg.target != "emu";
            _chipsPill.gameObject.SetActive(on);
            if (!on)
            {
                _bet.gameObject.SetActive(false);
                _dropBar.gameObject.SetActive(false);
                _receipt.gameObject.SetActive(false);
                _bannerPill.gameObject.SetActive(false);
                _debugPanel.gameObject.SetActive(false);
                return;
            }

            var kb = Keyboard.current;
            if (kb != null && kb.bKey.wasPressedThisFrame)
                _betOpen = !_betOpen;
            if (kb != null && kb.f3Key.wasPressedThisFrame)
                _showDebug = !_showDebug;
            var s = e.State;
            _chips.text = e.Balance >= 0 ? $"⬡ {ChainEconomy.Rush(e.Balance)} RUSH" : "⬡ chips…";

            // spectating (eliminated / watching) → bet panel + drop bar during the on-chain Final
            var spectating = session.Spectate || (session.OwnSlot >= 12) || session.OwnOut;
            var finalRush = s?.world != null && s.match != null && (s.match.flags & 0x40) != 0 && s.world.round == 4 && s.world.roundPhase == 3;
            var anyOpen = false;
            for (var k = 0; k < 5; k++)
                anyOpen |= e.MarketOpen(k);
            _bet.gameObject.SetActive(_betOpen || (anyOpen && spectating));
            if (_bet.gameObject.activeSelf)
                DrawBet(e, s);
            _dropBar.gameObject.SetActive(spectating && finalRush);
            if (_dropBar.gameObject.activeSelf)
                DrawDrops(e, s);
            DrawBanner(e, s);
            DrawReceipt(e, s);
            _debugPanel.gameObject.SetActive(_showDebug);
            if (_showDebug)
                DrawDebug(e, s);
        }

        private void DrawBet(ChainEconomy e, EState s)
        {
            var m = s?.markets != null && _betMarket < s.markets.Length ? s.markets[_betMarket] : null;
            if (m == null || m.status < 0)
            {
                _betTitle.text = "Rush Markets";
                _betBody.text = "loading…";
                return;
            }

            var open = e.MarketOpen(_betMarket);
            var settledBy = _betMarket <= 1 ? "◆ program-settled" : "● server-recorded";
            var pool = ChainEconomy.Parse(m.pool);
            _betTitle.text = $"{m.name}  <size=14><color=#7B2FF7>{(m.status >= 2 ? Labels[Mathf.Clamp(m.label, 0, 5)] : settledBy)}</color></size>";
            _sb.Clear();
            _sb.Append("Pool <b>").Append(ChainEconomy.Rush(pool)).Append(" RUSH</b> · ").Append(open ? "<color=#1FA36A>OPEN</color>" : m.status >= 2 ? "SETTLED" : "closed").Append('\n');
            var outs = Outcomes(s, _betMarket);
            for (var i = 0; i < outs.Length && i < 12; i++)
            {
                var o = outs[i];
                var st = o < m.stake.Length ? ChainEconomy.Parse(m.stake[o]) : 0;
                var mine = o < m.mine.Length ? ChainEconomy.Parse(m.mine[o]) : 0;
                var odds = st > 0 ? $"x{pool / (double)st:F2}" : "—";
                var win = m.status >= 2 && (m.winners >> o & 1) == 1;
                _sb.Append(i < 6 ? $"#{i + 1} " : "    ").Append(win ? "<color=#1FA36A>★ " : "").Append(SlotName(o)).Append(win ? "</color>" : "")
                    .Append("  <color=#8C88A6>").Append(odds).Append("</color>  stake ").Append(ChainEconomy.Rush(st));
                if (mine > 0)
                    _sb.Append("  <b>you ").Append(ChainEconomy.Rush(mine)).Append("</b>");
                _sb.Append('\n');
            }

            if (_betMarket == 0 && e.Sealed?.escrow != null && e.Sealed.escrow.n + (e.Sealed.open ? 1 : 0) > 0)
            {
                var es = e.Sealed.escrow;
                _sb.Append("<size=13><color=#7B2FF7><b>SEALED</b> — picks are hidden in MagicBlock's private rollup (TEE) until the market closes · ")
                    .Append(es.n).Append(" sealed bet(s), ").Append(ChainEconomy.Rush(ChainEconomy.Parse(es.pool))).Append(" RUSH");
                if (es.state >= 2)
                    _sb.Append(" · revealed ").Append(es.nRevealed).Append('/').Append(es.n).Append(es.nForfeit > 0 ? $", {es.nForfeit} forfeited" : string.Empty);
                foreach (var mine in e.Sealed.mine)
                    _sb.Append(" · yours: ").Append(ChainEconomy.Rush(ChainEconomy.Parse(mine.amount))).Append(" on ").Append(mine.saved != null && !string.IsNullOrEmpty(mine.saved.amount) ? SlotName(mine.saved.outcome) : "?")
                        .Append(mine.state == 1 ? " ✓" : mine.state == 2 ? " (forfeited)" : string.Empty);
                _sb.Append("</color></size>\n");
            }

            if (_betMarket >= 2)
                _sb.Append("<size=12>Pick who qualifies: every backer of a qualifier shares the pool.</size>\n");
            if (!string.IsNullOrEmpty(e.LastError) && e.LastAction != null && e.LastAction.StartsWith("bet"))
                _sb.Append("<color=#FF3EA5>").Append(e.LastError.Length > 90 ? e.LastError.Substring(0, 90) : e.LastError).Append("</color>\n");
            else if (!string.IsNullOrEmpty(e.LastSig) && e.LastAction != null && e.LastAction.StartsWith("bet"))
                _sb.Append("<color=#1FA36A>bet on-chain ✓ ").Append(e.LastSession ? "(session key, no popup)" : "").Append("</color>\n");
            _sb.Append("<size=12>mm = the market maker bot (seeds every market, labelled)</size>");
            _betBody.text = _sb.ToString();
        }

        private void DrawDrops(ChainEconomy e, EState s)
        {
            var ok = e.DropOpen(out var why);
            for (var k = 1; k <= 4; k++)
            {
                var price = s.prices != null && k < s.prices.Length ? s.prices[k] : 0;
                UiKit.SetButtonText(_dropButtons[k], $"{ChaosDropFx.Names[k]}  {price}");
                _dropButtons[k].interactable = ok && e.KindOnSale(k) && e.Balance >= price * ChainEconomy.One;
            }

            var left = 3 - (s.drops?.count ?? 0);
            _dropInfo.text = ok ? $"Chaos Drops: {left} left this round · map-wide, telegraphed 0.77 s · RUSH chips" : $"Chaos Drops: {why}";
        }

        private void DrawBanner(ChainEconomy e, EState s)
        {
            var n = ChaosDropFx.Any ? 0 : -1;
            if (ChaosDropFx.Any)
                foreach (var q in ChaosDropFx.Queue)
                    if (q.Kind != 0)
                        n++;
            var now = Time.realtimeSinceStartupAsDouble;
            if (n > _seenDrops)
            {
                _seenDrops = n;
                var q = ChaosDropFx.Queue[n - 1];
                var by = s?.drops?.buys != null && n - 1 < s.drops.buys.Length ? s.drops.buys[n - 1].by : null;
                var who = by == null ? "a spectator" : by == s.wallet ? "you" : Short(by);
                _banner.text = $"🌀 {ChaosDropFx.Names[q.Kind]} by {who} ◆  <size=16>rollup ✓</size>";
                _bannerUntil = now + 4.0;
            }
            else if (n < _seenDrops)
                _seenDrops = System.Math.Max(0, n);

            _bannerPill.gameObject.SetActive(now < _bannerUntil);
        }

        private void DrawReceipt(ChainEconomy e, EState s)
        {
            var done = s?.match != null && s.match.status == 5 && (s.match.flags & 0x40) != 0;
            if (!done && e.Receipt == null)
            {
                _receipt.gameObject.SetActive(false);
                return;
            }

            var mpda = s?.match?.pda ?? e.MatchPda;
            if (mpda != null && _receiptFor != mpda && Time.frameCount % 120 == 0)
                e.FetchReceipt(mpda);
            if (e.Receipt != null && e.Receipt.match == mpda && e.Receipt.onSolana)
                _receiptFor = mpda;
            else if (Time.frameCount % 120 == 0 && mpda != null)
                e.FetchReceipt(mpda);
            var r = e.Receipt;
            _receipt.gameObject.SetActive(true);
            _sb.Clear();
            _sb.Append("<size=24><b>MATCH RECEIPT</b></size>\n");
            if (r?.receipt == null)
            {
                _claim.gameObject.SetActive(false);
                _backup.gameObject.SetActive(false);
                _sb.Append("writing the receipt on the rollup…");
                _receiptText.text = _sb.ToString();
                return;
            }

            var me = -1;
            for (var i = 0; i < r.receipt.wallets.Length; i++)
                if (r.receipt.wallets[i] == r.me)
                    me = i;
            var champ = r.champion;
            _sb.Append("Champion: <b>").Append(champ < 12 ? SlotName(champ) : "-").Append("</b>");
            if ((r.receipt.flags & 4) != 0)
                _sb.Append("  👑 <color=#FFA51F>Crown Capsule minted</color>");
            else if ((r.receipt.flags & 16) != 0)
                _sb.Append("  (a bot: no Crown)");
            _sb.Append('\n');
            if (me >= 0 && me < r.receipt.rarity.Length && r.receipt.rarity[me] < 4)
            {
                var rr = r.receipt.rarity[me];
                _sb.Append("Your capsule: <b><color=#").Append(UiKit.HexOf(RarityColour[rr])).Append('>').Append(Rarity[rr]).Append("</color></b>")
                    .Append((r.receipt.mintedMask >> me & 1) == 1 ? "  (minted)" : "  (minting…)").Append('\n');
            }

            if (e.MatchStartBalance >= 0 && e.Balance >= 0)
            {
                var d = e.Balance - e.MatchStartBalance;
                _sb.Append("Chips this match: <b>").Append(d >= 0 ? "+" : "−").Append(ChainEconomy.Rush(System.Math.Abs(d))).Append(" RUSH</b>\n");
            }

            for (var k = 0; k < r.receipt.markets.Length; k++)
            {
                var mk = r.receipt.markets[k];
                if (mk.status == 0)
                    continue;
                _sb.Append("<size=14>").Append(new[] { "Champion", "Final winner", "Qualify R1", "Qualify R2", "Qualify R3" }[k]).Append(": pool ").Append(ChainEconomy.Rush(ChainEconomy.Parse(mk.pool)))
                    .Append(" · ").Append(Labels[Mathf.Clamp(mk.label, 0, 5)]).Append("</size>\n");
            }

            if (r.receipt.sealedN > 0)
                _sb.Append("<size=14>Sealed Champion bets: ").Append(r.receipt.sealedN).Append(" · revealed ").Append(r.receipt.sealedRevealed)
                    .Append(r.receipt.sealedForfeits > 0 ? $" · {r.receipt.sealedForfeits} forfeited" : string.Empty).Append(" ◆</size>\n");
            // P6: the season line (UI only; the numbers come from the on-chain PlayerSeason via the indexer)
            if (r.onSolana && me >= 0 && e.SeasonFor != r.match && Time.frameCount % 600 == 0)
                e.FetchSeason(r.match);
            var sp = e.Season?.player;
            if (me >= 0 && sp != null && !string.IsNullOrEmpty(sp.wallet) && e.SeasonFor == r.match)
            {
                _sb.Append("<size=15><color=#7B2FF7>");
                if (champ == me)
                    _sb.Append("+1 crown this season (").Append(sp.crowns).Append(" total) · ");
                _sb.Append(sp.finals).Append(" Final(s) this season");
                var a = sp.achievements;
                if ((a & 1) != 0) _sb.Append(" · FIRST CROWN");
                if ((a & 2) != 0) _sb.Append(" · 3-ROUND SURVIVOR");
                if ((a & 4) != 0) _sb.Append(" · PERFECT PREDICTOR");
                if ((a & 8) != 0) _sb.Append(" · CHAOS AGENT");
                _sb.Append("</color></size>\n");
            }

            _sb.Append("<size=14>Drops: ").Append(r.receipt.drops).Append(" · rounds 1–3 ● server-recorded · Final ◆ program-computed · core v").Append(r.receipt.coreVersion).Append("</size>\n");
            _sb.Append(r.onSolana ? "<color=#1FA36A>Rollup ✓ → Solana ✓</color>" : "Rollup ✓ → Solana …").Append("  <size=12>").Append(Short(r.match)).Append("</size>");
            var holds = me >= 0 && r.onSolana && (((r.receipt.flags & 4) != 0 && champ == me) || (r.receipt.mintedMask >> me & 1) == 1);
            if (holds && string.IsNullOrEmpty(ChainWallet.PhantomPubkey))
                _sb.Append("\n<size=14><color=#FF3EA5><b>⚠ Guest wallet:</b> your Crown / capsule lives only in this browser. Back up your key or claim it to Phantom before clearing site data.</color></size>");
            _receiptText.text = _sb.ToString();
            _claim.gameObject.SetActive(holds);
            _backup.gameObject.SetActive(holds && Application.platform == RuntimePlatform.WebGLPlayer);
            UiKit.SetButtonText(_claim, string.IsNullOrEmpty(ChainWallet.PhantomPubkey) ? "Connect Phantom to claim" : "Claim with Phantom → " + Short(ChainWallet.PhantomPubkey));
        }

        private void DrawDebug(ChainEconomy e, EState s)
        {
            _sb.Clear();
            _sb.Append("<b>ECONOMY (F3)</b>  calls ").Append(e.Calls).Append(" errors ").Append(e.Errors).Append('\n');
            _sb.Append("wallet ").Append(Short(s?.wallet)).Append("  chips ").Append(e.Balance >= 0 ? ChainEconomy.Rush(e.Balance) : "-").Append('\n');
            if (s?.world != null)
                _sb.Append("world tick ").Append(s.world.tick).Append(" round ").Append(s.world.round).Append(" rphase ").Append(s.world.roundPhase).Append(" rush@").Append(s.world.roundStart).Append('\n');
            if (s?.match != null)
                _sb.Append("match ").Append(Short(s.match.pda)).Append(" status ").Append(s.match.status).Append(" round ").Append(s.match.round).Append(" flags 0x").Append(s.match.flags.ToString("x")).Append('\n');
            if (s?.markets != null)
                foreach (var m in s.markets)
                    if (m != null && m.status >= 0)
                        _sb.Append("  k").Append(m.k).Append(' ').Append(m.name).Append(" st ").Append(m.status).Append(" pool ").Append(ChainEconomy.Rush(ChainEconomy.Parse(m.pool))).Append(" bets ").Append(m.nBets).Append(e.MarketOpen(m.k) ? " OPEN" : "").Append('\n');
            if (s?.drops != null)
                _sb.Append("drops ").Append(s.drops.count).Append(" last@").Append(s.drops.lastTick).Append(" pot ").Append(ChainEconomy.Rush(ChainEconomy.Parse(s.drops.pot))).Append('\n');
            for (var i = 0; i < 8; i++)
                if (ChaosDropFx.Queue[i].Kind != 0)
                    _sb.Append("  queued ").Append(ChaosDropFx.Names[ChaosDropFx.Queue[i].Kind]).Append(" @").Append(ChaosDropFx.Queue[i].Tick).Append(" arg ").Append(ChaosDropFx.Queue[i].Arg).Append('\n');
            foreach (var l in e.Log)
                _sb.Append("<size=11>").Append(l).Append("</size>\n");
            _debug.text = _sb.ToString();
        }
    }
}
