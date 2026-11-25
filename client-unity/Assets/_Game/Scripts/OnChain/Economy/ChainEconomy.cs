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
    [Serializable] public sealed class EReceiptBody { public int flags, trust, coreVersion, humanMask, mintedMask, drops, sealedN, sealedRevealed, sealedForfeits; public string dropSpent, mapHash, preFinal, finalTranscript, dropLog, capsuleSeed, crown; public string[] wallets = Array.Empty<string>(); public int[] placements = Array.Empty<int>(), rarity = Array.Empty<int>(); public EMarketResult[] markets = Array.Empty<EMarketResult>(); }
    [Serializable] public sealed class EReceipt { public bool ok, onSolana; public string error, match, crownAsset, me; public int status, flags, champion, round; public EReceiptBody receipt; public string[] capsuleAssets = Array.Empty<string>(); }
    [Serializable] public sealed class EReply { public bool ok, session; public string error, sig; public int ms; }
    // P6: sealed Champion bets (pick in MagicBlock's private rollup, stake + commitment on devnet-as) and the season
    [Serializable] public sealed class ESealedEscrow { public string pool, forfeit; public int n, nRevealed, state, nForfeit; public long closedAt; }
    [Serializable] public sealed class ESealedSaved { public int outcome; public string amount; }
    [Serializable] public sealed class ESealedMine { public int i, state; public string amount; public ESealedSaved saved; }
    [Serializable] public sealed class ESealed { public bool ok, open; public string match, error; public ESealedEscrow escrow; public ESealedMine[] mine = Array.Empty<ESealedMine>(); }
    [Serializable] public sealed class ESeasonPlayer { public int crowns, finals, matches, achievements, betsWon, betsLost, drops, roundsSurvived; public string net, wallet; }
    [Serializable] public sealed class ESeason { public bool ok; public int id; public ESeasonPlayer player; }
    // P7: /api/status (the arena keepers) → the current match's VRF deal request
    [Serializable] public sealed class SKeeper { public string match, dealSig; }
    [Serializable] public sealed class SServer { public string id; public SKeeper keeper; }
    [Serializable] public sealed class SStatus { public bool ok; public SServer[] servers = Array.Empty<SServer>(); }

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
        /// <summary>P6: the arena's sealed Champion escrow (+ my sealed entries) and my season stats.</summary>
        public ESealed Sealed;
        public ESeason Season;
        public string SeasonFor;
        private double _selfRevealAt;
        /// <summary>P6 test hook (dev / verification): <c>?autobet=&lt;RUSH&gt;</c> places one sealed Champion bet through
        /// <see cref="Bet"/> (the bet panel's path) as soon as the sealed market opens, on the first alive slot.</summary>
        private int _autobet = -1;
        private string _autobetFor;
        public string LastError, LastSig, LastAction;
        public bool LastSession;
        public long Balance => State?.chips != null ? Parse(State.chips.balance) : -1;
        /// <summary>The balance when this match's markets opened (the receipt shows the difference).</summary>
        public long MatchStartBalance = -1;
        public string MatchPda;
        /// <summary>P7: the request_deal tx of the current match (from the arena keeper via /api/status; WebGL only).</summary>
        public string DealSig;
        private string _dealFor;
        private double _dealAt;
        public bool Available { get; private set; } = true;
        /// <summary>P7: the chips account is set up on the rollup (bets before that fail with 3007).</summary>
        public bool ChipsReady => _chipsDone;
        /// <summary>P7: the <c>?autobet=</c> test hook owns the sealed Champion bet (the auto-play must not bet it too:
        /// P6's "autobet fires twice" was the two hooks each placing one).</summary>
        public bool AutobetOwnsChampion => _autobet > 0;
        public int Calls, Errors;
        public readonly List<string> Log = new List<string>();

        private int _id;
        private readonly Dictionary<int, Action<string>> _pending = new Dictionary<int, Action<string>>();
        private readonly List<int> _ids = new List<int>(8);
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

        private void Awake()
        {
            Instance = this;
            _autobet = int.TryParse(FallRush.Shared.NetTargets.GetQueryParam(Application.absoluteURL, "autobet"), out var ab) ? ab : 0;
        }
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
                // P7 fix: a callback may queue the next call (state → sealedState), so iterate a snapshot of the ids and
                // remove each one before its callback runs (the live build threw "Collection was modified" here, which
                // left finished calls pending and could run a callback twice).
                _ids.Clear();
                _ids.AddRange(_pending.Keys);
                foreach (var id in _ids)
                {
                    var r = tx.EconResult(id);
                    if (r == null || !_pending.TryGetValue(id, out var cb))
                        continue;
                    _pending.Remove(id);
                    try { cb(r); } catch (Exception e) { Debug.LogWarning("[econ] " + e.Message); }
                }
            }

            DealPoll();
            // economy reads / writes need the chain link only, not a joined slot (a hybrid pre-joined client bets
            // through rounds 1–3, before the Final starts)
            if (!Rollup || !tx.Connected)
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
                    // P6: the sealed escrow (and the commit-reveal fallback: reveal my own pick if the private book's
                    // reveal has not happened 6 s into the reveal window)
                    Call("sealedState", "{}", r2 =>
                    {
                        var x = JsonUtility.FromJson<ESealed>(r2);
                        if (x == null || !x.ok)
                            return;
                        Sealed = x;
                        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (x.escrow != null && x.escrow.state == 2 && nowUnix > x.escrow.closedAt + 6 && Time.realtimeSinceStartupAsDouble > _selfRevealAt + 5)
                            foreach (var mine in x.mine)
                                if (mine.state == 0 && mine.saved != null && !string.IsNullOrEmpty(mine.saved.amount))
                                {
                                    _selfRevealAt = Time.realtimeSinceStartupAsDouble;
                                    Call("sealedReveal", "{}", r3 => Note("self-reveal: " + r3));
                                    break;
                                }
                    });
                    if (_autobet < 0)
                        _autobet = int.TryParse(FallRush.Shared.NetTargets.GetQueryParam(Application.absoluteURL, "autobet"), out var ab) ? ab : 0;
                    // P7: only once the chips account is on the rollup (a fresh guest's first bet failed with 3007 before)
                    if (_autobet > 0 && _chipsDone && Sealed != null && Sealed.open && s.match != null && _autobetFor != s.match.pda && MarketOpen(0))
                    {
                        _autobetFor = s.match.pda;
                        var slot = 0;
                        for (var i = 0; i < s.match.slots.Length && i < s.match.players; i++)
                            if (s.match.slots[i] == 1 || s.match.slots[i] == 2) { slot = i; break; }
                        Debug.Log($"[econ] autobet: sealed Champion bet {_autobet} RUSH on slot {slot + 1}");
                        Bet(0, slot, _autobet, ok => Debug.Log($"[econ] autobet sealed {(ok ? "ok" : "failed: " + LastError)} sig {LastSig}"));
                    }

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

        /// <summary>P7: polls /api/status (same origin) every 3 s until the match's VRF deal request is known.</summary>
        private void DealPoll()
        {
            var m = State?.match?.pda;
            if (string.IsNullOrEmpty(m) || Application.platform != RuntimePlatform.WebGLPlayer || State.match.round > 1)
                return; // the Deal screen only (the keeper's request_deal lands during round 1 at the latest)
            if (_dealFor != m)
            {
                _dealFor = m;
                DealSig = null;
            }

            var now = Time.realtimeSinceStartupAsDouble;
            if (DealSig != null || now < _dealAt)
                return;
            _dealAt = now + 3.0;
            var req = UnityEngine.Networking.UnityWebRequest.Get("/api/status");
            req.SendWebRequest().completed += _ =>
            {
                try
                {
                    if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                        return;
                    var st = JsonUtility.FromJson<SStatus>(req.downloadHandler.text);
                    if (st?.servers == null)
                        return;
                    foreach (var sv in st.servers)
                        if (sv?.keeper != null && sv.keeper.match == m && !string.IsNullOrEmpty(sv.keeper.dealSig))
                            DealSig = sv.keeper.dealSig;
                }
                catch (Exception e) { Debug.LogWarning("[econ] status: " + e.Message); }
                finally { req.Dispose(); }
            };
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
        public void Bet(int k, int outcome, int amountRush, Action<bool> after = null)
        {
            // P6: the Champion market is sealed (the pick is hidden until close; the program reveals it from the book)
            if (k == 0 && Sealed != null && Sealed.open)
            {
                Call("sealedBet", $"{{\"outcome\":{outcome},\"amount\":{amountRush}}}", r => Reply($"bet sealed {outcome} {amountRush}", r, after));
                return;
            }

            Call("bet", $"{{\"k\":{k},\"outcome\":{outcome},\"amount\":{amountRush}}}", r => Reply($"bet {k}/{outcome} {amountRush}", r, after));
        }

        /// <summary>P6: my season stats (crowns, achievements) from the indexer's /api/season.</summary>
        public void FetchSeason(string forMatch) =>
            Call("season", "{}", r =>
            {
                var x = JsonUtility.FromJson<ESeason>(r);
                if (x != null && x.ok)
                {
                    Season = x;
                    SeasonFor = forMatch;
                }
            });

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

        /// <summary><c>Match.flags</c>: 0x40 = Final on-chain after Classic rounds (<c>start_final</c>), 0x20 = the program
        /// simulates (<c>start_sim</c>, fullchain: every round; also set with 0x40). Either = the arena runs this match's Final.</summary>
        public const int FlagFinalMode = 0x40, FlagSimStarted = 0x20;

        public static bool ChainRuns(int matchFlags) => (matchFlags & (FlagFinalMode | FlagSimStarted)) != 0;

        /// <summary>fullchain: every round computed by the program (start_sim without the Final-mode bit).</summary>
        public static bool ChainAll(int matchFlags) => (matchFlags & FlagSimStarted) != 0 && (matchFlags & FlagFinalMode) == 0;

        public bool DropOpen(out string why)
        {
            why = null;
            var s = State;
            if (s?.world == null || s.match == null || !ChainRuns(s.match.flags) || s.world.round != 4 || s.world.roundPhase != 3)
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
            var live = ChainRuns(s.match.flags) && s.world != null && s.world.round == 4 && s.world.roundPhase < 3; // fullchain: FinalWinner at R4 intro
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
