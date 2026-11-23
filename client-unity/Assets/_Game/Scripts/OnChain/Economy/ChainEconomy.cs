using System;
using System.Collections.Generic;
using UnityEngine;

namespace FallRush.OnChain
{
    // P4 economy JSON (packages/chain/src/rollup.ts econ): JsonUtility shapes; chip amounts are base-unit strings
    // (6 decimals) so nothing is lost to floats.
    [Serializable] public sealed class EChips { public string wallet, balance, faucetTotal, cashedOut, won, spent; public long faucetAt; }
    [Serializable] public sealed class EBet { public string wallet, amount; public int outcome, paid; }
    [Serializable] public sealed class EMarket { public int k = -1; public string name, match, pool, winningStake, paidTotal; public string[] stake = Array.Empty<string>(); public string[] mine = Array.Empty<string>(); public int winners, kind, round, status = -1, label, nBets, refund; public EBet[] bets = Array.Empty<EBet>(); }
    [Serializable] public sealed class EBuy { public string by; public long tick; public int kind; }
    [Serializable] public sealed class EDrops { public string match, pot; public long lastTick; public int count; public EBuy[] buys = Array.Empty<EBuy>(); }
    [Serializable] public sealed class EWorld { public long tick; public int phase, round, roundPhase; public long roundStart; }
    [Serializable] public sealed class EMatch { public string pda; public int status, flags, players, round, champion; public int[] slots = Array.Empty<int>(); }
    [Serializable] public sealed class EState { public bool ok; public string error, wallet; public EChips chips; public EMarket[] markets = Array.Empty<EMarket>(); public EDrops drops; public EWorld world; public EMatch match; public int[] prices = Array.Empty<int>(); }
    [Serializable] public sealed class EMarketResult { public string pool, winningStake; public int winners, label, status; }
    [Serializable] public sealed class EReceiptBody { public int flags, trust, coreVersion, humanMask, mintedMask, drops; public string dropSpent, mapHash, preFinal, finalTranscript, dropLog, capsuleSeed, crown; public string[] wallets = Array.Empty<string>(); public int[] placements = Array.Empty<int>(), rarity = Array.Empty<int>(); public EMarketResult[] markets = Array.Empty<EMarketResult>(); }
    [Serializable] public sealed class EReceipt { public bool ok, onSolana; public string error, match, crownAsset, me; public int status, flags, champion, round; public EReceiptBody receipt; public string[] capsuleAssets = Array.Empty<string>(); }
    [Serializable] public sealed class EReply { public bool ok, session; public string error, sig; public int ms; }

    /// <summary>
    /// P4: the player's side of the economy (RUSH chips, Rush Markets, Chaos Drops, receipt, claim), over the on-chain
    /// session's transport (WebGL bundle / Mac relay → <c>packages/chain</c> → the fall_rush program). Gets chips silently
    /// on first use (ChipBalance init + delegation by the burner, the faucet signed by the session key; no popup), polls
    /// the state once a second while an on-chain session runs. Devnet test chips only, no real-money value.
    /// </summary>
    public sealed class ChainEconomy : MonoBehaviour
    {
        public static ChainEconomy Instance { get; private set; }
        public const long One = 1_000_000;

        public EState State;
        public EReceipt Receipt;
        public string LastError, LastSig, LastAction;
        public bool LastSession;
        public long Balance => State?.chips != null ? Parse(State.chips.balance) : -1;
        /// <summary>The balance when this match's markets opened (the receipt shows the difference).</summary>
        public long MatchStartBalance = -1;
        public string MatchPda;
        public bool Available { get; private set; } = true;
        public int Calls, Errors;
        public readonly List<string> Log = new List<string>();

        private int _id;
        private readonly Dictionary<int, Action<string>> _pending = new Dictionary<int, Action<string>>();
        private double _nextPoll, _chipsAt = -1;
        private bool _stateInFlight, _chipsDone;

        public static ChainEconomy Ensure()
        {
            if (Instance != null)
                return Instance;
            var go = new GameObject("ChainEconomy");
            DontDestroyOnLoad(go);
            return go.AddComponent<ChainEconomy>();
        }

        private void Awake() => Instance = this;
        private void OnDestroy() { if (Instance == this) Instance = null; }

        public static long Parse(string s) => long.TryParse(s, out var v) ? v : 0;
        public static string Rush(long baseUnits) => (baseUnits / One).ToString("N0");

        private IChainTransport Tx
        {
            get
            {
                var s = OnChainSession.Instance;
                return s != null && s.isActiveAndEnabled ? s.Transport : null;
            }
        }

        private bool Rollup => OnChainSession.Instance != null && OnChainSession.Instance.Cfg.target != "emu";

        private void Call(string action, string args, Action<string> done)
        {
            var tx = Tx;
            if (tx == null)
                return;
            var id = ++_id;
            _pending[id] = done;
            Calls++;
            tx.Econ(id, action, args);
        }

        private void Update()
        {
            var tx = Tx;
            if (tx == null)
                return;
            if (_pending.Count > 0)
            {
                var done = new List<int>();
                foreach (var kv in _pending)
                {
                    var r = tx.EconResult(kv.Key);
                    if (r == null)
                        continue;
                    done.Add(kv.Key);
                    try { kv.Value(r); } catch (Exception e) { Debug.LogWarning("[econ] " + e.Message); }
                }

                foreach (var k in done)
                    _pending.Remove(k);
            }

            if (!Rollup || tx.Joined == null)
                return;
            var now = Time.realtimeSinceStartupAsDouble;
            // chips on first use: init + delegate (burner) + the day's faucet claim (session key), no popup
            if (!_chipsDone && (_chipsAt < 0 || now - _chipsAt > 20))
            {
                _chipsAt = now;
                Call("chips", "{}", r =>
                {
                    var j = JsonUtility.FromJson<EReply>(r);
                    _chipsDone = j.ok;
                    Note(j.ok ? "chips ready" : "chips: " + j.error);
                });
            }

            if (now >= _nextPoll && !_stateInFlight)
            {
                _nextPoll = now + 1.0;
                _stateInFlight = true;
                Call("state", "{}", r =>
                {
                    _stateInFlight = false;
                    var s = JsonUtility.FromJson<EState>(r);
                    if (s == null || !s.ok)
                    {
                        Available = !(r.Contains("no economy") || r.Contains("unknown economy"));
                        LastError = s?.error ?? r;
                        return;
                    }

                    State = s;
                    var m = s.match?.pda;
                    if (!string.IsNullOrEmpty(m) && m != MatchPda)
                    {
                        MatchPda = m;
                        MatchStartBalance = Balance;
                        Receipt = null;
                    }
                });
            }
        }

        private void Note(string s)
        {
            Log.Add($"{DateTime.Now:HH:mm:ss} {s}");
            if (Log.Count > 12)
                Log.RemoveAt(0);
        }

        private void Reply(string what, string r, Action<bool> after)
        {
            var j = JsonUtility.FromJson<EReply>(r);
            LastAction = what;
            LastSig = j.sig;
            LastSession = j.session;
            if (!j.ok)
            {
                Errors++;
                LastError = j.error;
            }

            Note($"{what}: {(j.ok ? "ok" : j.error)} {(j.session ? "(session key)" : "")}");
            after?.Invoke(j.ok);
            _nextPoll = 0; // refresh now
        }

        /// <summary>A bet of <paramref name="amountRush"/> whole RUSH on <paramref name="outcome"/> (slot) in market k.</summary>
        public void Bet(int k, int outcome, int amountRush, Action<bool> after = null) =>
            Call("bet", $"{{\"k\":{k},\"outcome\":{outcome},\"amount\":{amountRush}}}", r => Reply($"bet {k}/{outcome} {amountRush}", r, after));

        public void Drop(int kind, Action<bool> after = null) =>
            Call("drop", $"{{\"kind\":{kind}}}", r => Reply($"drop {kind}", r, after));

        public void FetchReceipt(string matchPda) =>
            Call("receipt", $"{{\"match\":\"{matchPda}\"}}", r =>
            {
                var x = JsonUtility.FromJson<EReceipt>(r);
                if (x != null && x.ok)
                    Receipt = x;
            });

        /// <summary>"Claim with Phantom": moves an asset the burner owns to <paramref name="toWallet"/> (Core TransferV1).</summary>
        public void Claim(string asset, string toWallet, Action<bool> after = null) =>
            Call("claim", $"{{\"asset\":\"{asset}\",\"to\":\"{toWallet}\"}}", r => Reply("claim", r, after));

        // ---- program rules mirrored for the UI (the program enforces them; this only greys buttons)
        public const int DropCap = 3, DropCooldownTicks = 150, SweeperFromT30 = 600;

        public bool DropOpen(out string why)
        {
            why = null;
            var s = State;
            if (s?.world == null || s.match == null || (s.match.flags & 0x40) == 0 || s.world.round != 4 || s.world.roundPhase != 3)
            {
                why = "during the on-chain Final's RUSH only";
                return false;
            }

            if (s.drops != null && s.drops.count >= DropCap)
            {
                why = "3 drops this round";
                return false;
            }

            if (s.drops != null && s.drops.count > 0 && s.world.tick < s.drops.lastTick + DropCooldownTicks)
            {
                why = $"cooldown {(s.drops.lastTick + DropCooldownTicks - s.world.tick) / 30f:F1} s";
                return false;
            }

            return true;
        }

        public bool KindOnSale(int kind) => kind != 4 || (State?.world != null && State.world.tick - State.world.roundStart >= SweeperFromT30);

        /// <summary>Is market <paramref name="k"/> taking bets (program rule, mirrored)?</summary>
        public bool MarketOpen(int k)
        {
            var s = State;
            if (s?.match == null || s.markets == null || k >= s.markets.Length || s.markets[k] == null || s.markets[k].status != 1 || s.markets[k].match != s.match.pda)
                return false;
            var final = (s.match.flags & 0x40) != 0;
            var live = final && s.world != null && s.world.round == 4 && s.world.roundPhase < 3;
            var over = s.match.status >= 5;
            if (over)
                return false;
            switch (k)
            {
                case 0: return !(s.match.round >= 4 || final) || live;
                case 1: return live;
                default:
                    var r = k - 1;
                    return r == 1 ? s.match.round == 0 && s.match.status <= 2 : s.match.round == r - 1 && s.match.status == 4;
            }
        }
    }
}
