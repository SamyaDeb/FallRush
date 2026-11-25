using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// P3-F (server, additive, OFF by default): the Classic → on-chain switch at the Final. With
    /// <c>-fallrushOnchainFinal</c> (env <c>GAMESERVER_ONCHAIN_FINAL=1</c>) the game server hands the Final to an arena:
    /// <list type="number">
    /// <item>when the director loads the last round, <see cref="Begin"/> asks the arena for a Final with the alive roster
    /// and the round's hazard seed (Part 1: the chain emulator's <c>POST /final</c>; Part 2: P2's <c>start_final</c>
    /// through its chain worker) and waits until the arena steps (≤ <see cref="LiveTimeoutSeconds"/>);</item>
    /// <item>live: <see cref="ServerMatch"/> wraps the Final's rules in <see cref="ChainFinalRules"/> (the server does not
    /// simulate the Final; the result comes from the arena) and sends every client <c>fonc</c> (<see cref="Message"/>);
    /// clients switch to the arena;</item>
    /// <item>not live in time (or any error): <see cref="Failed"/>, and the server runs the Final itself (Classic).</item>
    /// </list>
    /// </summary>
    public sealed class ChainFinalHandoff
    {
        public const string Msg = "fonc";
        public const float LiveTimeoutSeconds = 8f;
        /// <summary>Submission (judge mode): rounds 1–3 take seconds, so the keeper is still recording them when the Final
        /// loads (~18 s measured from roster lock to start_final): a judge match waits this long instead.</summary>
        public const float JudgeTimeoutSeconds = 45f;
        /// <summary>This handoff's wait (rollup mode); <see cref="LiveTimeoutSeconds"/> unless the server sets it.</summary>
        public float TimeoutSeconds = LiveTimeoutSeconds;

        public static bool Enabled =>
            Array.IndexOf(Environment.GetCommandLineArgs(), "-fallrushOnchainFinal") >= 0 || Environment.GetEnvironmentVariable("GAMESERVER_ONCHAIN_FINAL") == "1";

        /// <summary>
        /// fullchain (docs/proof/fullchain/CONTRACT.md §1, OFF by default): <c>-fallrushOnchainAll</c> / env
        /// <c>GAMESERVER_ONCHAIN_ALL=1</c>, on top of a rollup handoff (<see cref="Enabled"/> + net devnet/local): the whole
        /// match (rounds 1–4) runs on the arena (<c>start_sim</c>); per-match fallback = the Classic match.
        /// </summary>
        public static bool AllEnabled =>
            Enabled && (Array.IndexOf(Environment.GetCommandLineArgs(), "-fallrushOnchainAll") >= 0 || Environment.GetEnvironmentVariable("GAMESERVER_ONCHAIN_ALL") == "1");

        /// <summary>fullchain: how long the server waits (from roster lock) for the arena to run this match. The keeper
        /// creates + delegates the Match, waits for the VRF deal and the humans' joins (≤ 10 s / judge 20 s), then
        /// start_sim: more than the Final's 8 s. Env <c>GAMESERVER_ONCHAIN_ALL_TIMEOUT</c> (seconds) overrides both.</summary>
        public const float AllTimeoutSeconds = 60f, AllJudgeTimeoutSeconds = 90f;

        public static float AllTimeout(bool judge)
        {
            var v = ServerSim.ArgStr("-fallrushOnchainAllTimeout", "GAMESERVER_ONCHAIN_ALL_TIMEOUT");
            if (!string.IsNullOrEmpty(v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0)
                return f;
            return judge ? AllJudgeTimeoutSeconds : AllTimeoutSeconds;
        }

        /// <summary>fullchain: this handoff is a whole match (<see cref="BeginAll"/>), not the Final.</summary>
        public bool All { get; private set; }
        /// <summary>fullchain, while live: the arena's director phase / round (−1 before the first read).</summary>
        public int ChainPhase { get; private set; } = -1;
        public int ChainRound { get; private set; }
        /// <summary>fullchain: the arena's match ended without a champion (ABORTED, left the arena, or stalled).</summary>
        public bool ChainFailed { get; private set; }
        public string ChainFailWhy { get; private set; }
        /// <summary>fullchain: no World tick progress for this long while live = the match is dead (keeper / tasks gone).</summary>
        public const float AllStallSeconds = 120f;

        /// <summary>The arena's control URL (emulator HTTP) and the URL clients connect to.</summary>
        public readonly string ControlUrl, ClientUrl;

        public enum State { Idle, Deciding, Live, Failed, Done }

        public State Phase { get; private set; } = State.Idle;
        public string Error { get; private set; }
        public string Arena { get; private set; }
        public string Message { get; private set; }
        public float StartedAt { get; private set; }
        /// <summary>The arena's Final result (slots in rank order, reasons by rank), once it ended.</summary>
        public int[] ResultOrder { get; private set; }
        public ElimReason[] ResultReasons { get; private set; }
        public uint ResultTicks { get; private set; }

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        private volatile string _status;
        private bool _polling;

        /// <summary>"emu" (Part 1: the chain emulator) or "devnet" / "local" (Part 2: P2's arena, driven by its keeper in
        /// hybrid mode, which records rounds 1–3 from this server's jsonl and calls <c>start_final</c>).</summary>
        public readonly string Net;
        /// <summary>Rollup mode: the ER JSON-RPC URL and the Final arena's <c>Arena</c> / <c>WorldAcc</c> accounts.</summary>
        public readonly string ErUrl, ArenaKey, WorldKey;
        public bool Rollup => Net != "emu";

        public ChainFinalHandoff()
        {
            Net = ServerSim.ArgStr("-fallrushOnchainFinalNet", "GAMESERVER_ONCHAIN_FINAL_NET") ?? "emu";
            ErUrl = ServerSim.ArgStr("-fallrushOnchainEr", "GAMESERVER_ONCHAIN_ER") ?? (Net == "local" ? "http://127.0.0.1:7799" : "https://devnet-as.magicblock.app");
            ArenaKey = ServerSim.ArgStr("-fallrushOnchainArena", "GAMESERVER_ONCHAIN_ARENA") ?? "HbinPthhN3g5BRQRL4aKQ75HyQXMofhsUbg2c28bJXHt";
            WorldKey = ServerSim.ArgStr("-fallrushOnchainWorld", "GAMESERVER_ONCHAIN_WORLD");
            ControlUrl = (ServerSim.ArgStr("-fallrushOnchainFinalUrl", "GAMESERVER_ONCHAIN_FINAL_URL") ?? "http://127.0.0.1:8797").TrimEnd('/');
            ClientUrl = ServerSim.ArgStr("-fallrushOnchainClientUrl", "GAMESERVER_ONCHAIN_CLIENT_URL") ?? ControlUrl.Replace("http", "ws") + "/";
        }

        /// <summary>Starts the handoff for <paramref name="roster"/> (the alive slots: slot, name, bot).</summary>
        public void Begin(string matchHex, ulong seed, (int slot, string name, bool bot)[] roster)
        {
            All = false;
            Phase = State.Deciding;
            StartedAt = Time.realtimeSinceStartup;
            ResultOrder = null;
            var sb = new StringBuilder();
            sb.Append("{\"match\":\"").Append(matchHex).Append("\",\"seed\":\"").Append(seed.ToString("x16")).Append("\",\"roster\":[");
            var slots = new StringBuilder();
            for (var i = 0; i < roster.Length; i++)
            {
                var r = roster[i];
                var name = (r.name ?? "").Replace("\"", "").Replace("\\", "");
                if (i > 0)
                {
                    sb.Append(',');
                    slots.Append(',');
                }

                sb.Append("{\"slot\":").Append(r.slot).Append(",\"name\":\"").Append(name).Append("\",\"kind\":\"").Append(r.bot ? "bot" : "human").Append("\"}");
                slots.Append("{\"slot\":").Append(r.slot).Append(",\"name\":\"").Append(name).Append("\",\"kind\":\"").Append(r.bot ? "bot" : "human").Append("\"}");
            }

            sb.Append("]}");
            var body = sb.ToString();
            if (Rollup)
                _ = RunRollup(matchHex, slots.ToString());
            else
                _ = Run(body, matchHex, slots.ToString());
        }

        private async Task Run(string body, string matchHex, string slots)
        {
            try
            {
                var r = await Http.PostAsync(ControlUrl + "/final", new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
                var text = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!r.IsSuccessStatusCode || !text.Contains("\"ok\":true"))
                    throw new Exception($"/final {(int)r.StatusCode}: {text}");
                Arena = Field(text, "arena");
                // live = the arena steps: two /status reads with an advancing tick
                long first = -1;
                var t0 = DateTime.UtcNow;
                while ((DateTime.UtcNow - t0).TotalSeconds < LiveTimeoutSeconds)
                {
                    var s = await Http.GetStringAsync(ControlUrl + "/status").ConfigureAwait(false);
                    var tick = long.Parse(Field(s, "tick") ?? "-1", CultureInfo.InvariantCulture);
                    if (Field(s, "arena") == Arena && first >= 0 && tick > first)
                    {
                        Message = $"{{\"t\":\"final_onchain\",\"arena\":\"{Arena}\",\"match\":\"{matchHex}\",\"url\":\"{ClientUrl}\",\"slot_map\":[{slots}]}}";
                        Phase = State.Live;
                        Debug.Log($"[gs] on-chain Final live: {Arena} ({(DateTime.UtcNow - t0).TotalMilliseconds:F0} ms)");
                        return;
                    }

                    if (Field(s, "arena") == Arena && first < 0)
                        first = tick;
                    await Task.Delay(200).ConfigureAwait(false);
                }

                throw new Exception($"arena {Arena} not live within {LiveTimeoutSeconds} s");
            }
            catch (Exception e)
            {
                Error = e.Message;
                Phase = State.Failed;
                Debug.LogWarning($"[gs] on-chain Final handoff failed ({e.Message}): the server runs the Final (Classic)");
            }
        }

        /// <summary>
        /// Rollup mode, at roster lock: the message that lets clients pre-join the arena's lobby during rounds 1–3 (P2's
        /// keeper opens the lobby early and calls <c>start_final</c> right after round 3, before the Final would load).
        /// </summary>
        public string PrejoinMessage(string matchHex, (int slot, string name, bool bot)[] humans)
        {
            var sb = new StringBuilder();
            sb.Append("{\"t\":\"final_prejoin\",\"net\":\"").Append(Net).Append("\",\"arena\":\"").Append(ArenaKey).Append("\",\"match\":\"").Append(matchHex).Append("\",\"slot_map\":[");
            for (var i = 0; i < humans.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"slot\":").Append(humans[i].slot).Append(",\"name\":\"").Append((humans[i].name ?? "").Replace("\"", "")).Append("\",\"kind\":\"").Append(humans[i].bot ? "bot" : "human").Append("\"}");
            }

            return sb.Append("]}").ToString();
        }

        /// <summary>
        /// fullchain, at roster lock: waits (≤ <see cref="TimeoutSeconds"/>) until the arena runs THIS match (Arena status
        /// running, <c>Arena.match_account</c> = a Match whose <c>match_id</c> is <paramref name="matchHex"/>, the World's
        /// director past ROSTER_LOCKED and its tick advancing). Live → <see cref="Message"/> (<c>final_onchain</c> with
        /// <c>"all":true</c> and every roster slot); else <see cref="State.Failed"/> (the server plays the match on Classic).
        /// </summary>
        public void BeginAll(string matchHex, (int slot, string name, bool bot)[] roster)
        {
            All = true;
            Phase = State.Deciding;
            StartedAt = Time.realtimeSinceStartup;
            ResultOrder = null;
            ResultReasons = null;
            ChainPhase = -1;
            ChainRound = 0;
            ChainFailed = false;
            ChainFailWhy = null;
            _verifiedMatch = null;
            // fullchain Gate 2 fix: the previous match's last arena / World reads must not reach this match's first
            // result check (a stale "arena ended" read aborted 2 of 5 matches 0.6 s after they went live)
            _arenaNow = null;
            _world = null;
            _leftPolls = 0;
            _allPolls = 0;
            _lastTickAt = DateTime.UtcNow;
            var slots = new StringBuilder();
            for (var i = 0; i < roster.Length; i++)
            {
                var r = roster[i];
                if (i > 0)
                    slots.Append(',');
                slots.Append("{\"slot\":").Append(r.slot).Append(",\"name\":\"").Append((r.name ?? "").Replace("\"", "").Replace("\\", "")).Append("\",\"kind\":\"").Append(r.bot ? "bot" : "human").Append("\"}");
            }

            _ = RunRollupAll(matchHex, slots.ToString());
        }

        private string _verifiedMatch;

        /// <summary>fullchain: is the arena's current Match (<c>Arena.match_account</c>) the one with <paramref name="matchHex"/>?</summary>
        private async Task<bool> ArenaRunsMatch(byte[] arena, string matchHex)
        {
            if (arena == null || arena.Length < ArenaMatch + 32)
                return false;
            var key = Base58Encode(arena, ArenaMatch, 32);
            if (key == _verifiedMatch)
                return true;
            var m = await ReadAccount(ErUrl, key).ConfigureAwait(false);
            if (m == null || m.Length < MatchId + 16 || !SameId(m, MatchId, matchHex))
                return false;
            _verifiedMatch = key;
            return true;
        }

        public static bool SameId(byte[] b, int offset, string hex)
        {
            if (hex == null || hex.Length != 32 || b == null || b.Length < offset + 16)
                return false;
            for (var i = 0; i < 16; i++)
                if (b[offset + i] != byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                    return false;
            return true;
        }

        private async Task RunRollupAll(string matchHex, string slots)
        {
            try
            {
                if (string.IsNullOrEmpty(WorldKey))
                    throw new Exception("-fallrushOnchainWorld (the arena's WorldAcc address) is required in rollup mode");
                var t0 = DateTime.UtcNow;
                long first = -1;
                while ((DateTime.UtcNow - t0).TotalSeconds < TimeoutSeconds)
                {
                    byte[] a = null, w = null;
                    var ours = false;
                    try
                    {
                        a = await ReadAccount(ErUrl, ArenaKey).ConfigureAwait(false);
                        if (a != null && a.Length > ArenaStatus && a[ArenaStatus] == 2)
                        {
                            ours = await ArenaRunsMatch(a, matchHex).ConfigureAwait(false);
                            if (ours)
                                w = await ReadAccount(ErUrl, WorldKey).ConfigureAwait(false);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[gs] on-chain match liveness read: {e.Message} (retrying)");
                    }

                    var d = w != null ? new ChainDirector(w, WaWorld) : default;
                    if (ours && w != null && d.Valid && d.Running)
                    {
                        var tick = BitConverter.ToUInt32(w, WaTick);
                        ChainPhase = d.Phase;
                        ChainRound = d.Round;
                        if (first >= 0 && tick > first)
                        {
                            Arena = ArenaKey;
                            Message = $"{{\"t\":\"final_onchain\",\"net\":\"{Net}\",\"arena\":\"{ArenaKey}\",\"match\":\"{matchHex}\",\"url\":\"\",\"all\":true,\"slot_map\":[{slots}]}}";
                            _lastTick = tick;
                            _lastTickAt = DateTime.UtcNow;
                            Phase = State.Live;
                            Debug.Log($"[gs] on-chain match live on {Net}: arena {ArenaKey} director phase {d.Phase} round {d.Round} tick {tick} ({(DateTime.UtcNow - t0).TotalSeconds:F1} s)");
                            return;
                        }

                        if (first < 0)
                            first = tick;
                    }

                    await Task.Delay(300).ConfigureAwait(false);
                }

                throw new Exception($"arena {ArenaKey} not running match {matchHex} within {TimeoutSeconds} s");
            }
            catch (Exception e)
            {
                Error = e.Message;
                Phase = State.Failed;
                Debug.LogWarning($"[gs] on-chain match handoff failed ({e.Message}): the server plays the match (Classic)");
            }
        }

        private uint _lastTick;
        private DateTime _lastTickAt;
        private int _leftPolls;
        /// <summary>Consecutive arena reads (one per ~2 s) that must show the arena off this match before it counts.</summary>
        public const int LeftPollsNeeded = 3;
        private volatile byte[] _arenaNow;
        private int _allPolls;

        /// <summary>fullchain, while live: follows the arena's director; the result at DONE (or CHAMPION once the
        /// arena has left this match), a failure at ABORTED / another match / no progress.</summary>
        private void TryAllResult(byte[] w, byte[] a)
        {
            if (w == null || w.Length < WaWorld + ChainDirector.InWorld + ChainDirector.Size)
                return;
            var d = new ChainDirector(w, WaWorld);
            var tick = BitConverter.ToUInt32(w, WaTick);
            if (tick != _lastTick)
            {
                _lastTick = tick;
                _lastTickAt = DateTime.UtcNow;
            }

            ChainPhase = d.Phase;
            ChainRound = d.Round;
            var leftNow = a != null && a.Length > ArenaStatus && (a[ArenaStatus] != 2 || _verifiedMatch == null || Base58Encode(a, ArenaMatch, 32) != _verifiedMatch);
            // an arena read only counts as "left this match" after LeftPollsNeeded consecutive reads say so
            if (a != null)
                _leftPolls = leftNow ? _leftPolls + 1 : 0;
            var left = _leftPolls >= LeftPollsNeeded;
            if (d.Phase == ChainDirector.Aborted)
            {
                Fail($"the arena aborted the match (reason {d.LastAbort})");
                return;
            }

            if (d.HasChampion && (d.Phase == ChainDirector.Done || left) && d.TryResult(out var order, out var reasons))
            {
                ResultOrder = order;
                ResultReasons = new ElimReason[reasons.Length];
                for (var k = 0; k < reasons.Length; k++)
                    ResultReasons[k] = (ElimReason)reasons[k];
                ResultTicks = tick;
                Phase = State.Done;
                Debug.Log($"[gs] on-chain match result from the arena director: champion {order[0]} order {string.Join(",", order)}");
                return;
            }

            if (left)
                Fail("the arena left this match without a champion");
            else if ((DateTime.UtcNow - _lastTickAt).TotalSeconds > AllStallSeconds)
                Fail($"no World progress for {AllStallSeconds} s (tick {tick})");
        }

        private void Fail(string why)
        {
            if (ChainFailed)
                return;
            ChainFailed = true;
            ChainFailWhy = why;
            Debug.LogWarning("[gs] on-chain match: " + why + "; aborting the server's match");
        }

        private async Task PollAll()
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                _world = await ReadAccount(ErUrl, WorldKey).ConfigureAwait(false);
                if (_allPolls++ % 4 == 0)
                    _arenaNow = await ReadAccount(ErUrl, ArenaKey).ConfigureAwait(false); // 0.5 Hz: still this match?
            }
            catch (Exception e)
            {
                Debug.LogWarning("[gs] on-chain match World: " + e.Message);
            }
            finally
            {
                _polling = false;
            }
        }

        private const string B58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        /// <summary>Base58 (Bitcoin alphabet) of <paramref name="len"/> bytes at <paramref name="offset"/>.</summary>
        public static string Base58Encode(byte[] b, int offset, int len)
        {
            var digits = new List<int>(48); // base 58, least significant first
            for (var i = 0; i < len; i++)
            {
                var carry = (int)b[offset + i];
                for (var j = 0; j < digits.Count; j++)
                {
                    carry += digits[j] << 8;
                    digits[j] = carry % 58;
                    carry /= 58;
                }

                while (carry > 0)
                {
                    digits.Add(carry % 58);
                    carry /= 58;
                }
            }

            var sb = new StringBuilder(48);
            for (var i = 0; i < len && b[offset + i] == 0; i++)
                sb.Append('1');
            for (var j = digits.Count - 1; j >= 0; j--)
                sb.Append(B58[digits[j]]);
            return sb.ToString();
        }

        private static async Task<byte[]> ReadAccount(string url, string key)
        {
            var req = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"getAccountInfo\",\"params\":[\"" + key + "\",{\"encoding\":\"base64\",\"commitment\":\"processed\"}]}";
            var r = await Http.PostAsync(url, new StringContent(req, Encoding.UTF8, "application/json")).ConfigureAwait(false);
            var t = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
            var i = t.IndexOf("\"data\":[\"", StringComparison.Ordinal);
            if (i < 0)
                return null;
            i += 9;
            var e = t.IndexOf('"', i);
            return Convert.FromBase64String(t.Substring(i, e - i));
        }

        // Arena (1,432 B, checked on devnet): status u8 @1409. WorldAcc: tick u32 @128, part_pending @136, round @139,
        // World @208 (rush_core::World: round state @1768: phase +1, result +52 {n, order[12], qualified u16, reasons[12]},
        // has_result +137).
        private const int ArenaStatus = 1409, WaTick = 128, WaPart = 136, WaRound = 139, WaWorld = 208, RoundState = 1768;
        // fullchain: Arena.match_account @40 (disc 8 + authority 32); Match.match_id @10 (disc 8, version, bump)
        private const int ArenaMatch = 40, MatchId = 10;

        private async Task RunRollup(string matchHex, string slots)
        {
            try
            {
                if (string.IsNullOrEmpty(WorldKey))
                    throw new Exception("-fallrushOnchainWorld (the arena's WorldAcc address) is required in rollup mode");
                var t0 = DateTime.UtcNow;
                long first = -1;
                // live = the arena is RUNNING the Final (start_final done: WorldAcc round 4) and its tick advances
                while ((DateTime.UtcNow - t0).TotalSeconds < TimeoutSeconds)
                {
                    byte[] a = null, w = null;
                    try
                    {
                        a = await ReadAccount(ErUrl, ArenaKey).ConfigureAwait(false);
                        w = await ReadAccount(ErUrl, WorldKey).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        // one slow / failed RPC must not decide the handoff (measured: a 3 s timeout did, and the arena
                        // and the server then both ran a Final): retry until the window closes
                        Debug.LogWarning($"[gs] on-chain Final liveness read: {e.Message} (retrying)");
                    }

                    if (a != null && w != null && a.Length > ArenaStatus && w.Length > WaWorld && a[ArenaStatus] == 2 && w[WaRound] == 4)
                    {
                        var tick = BitConverter.ToUInt32(w, WaTick);
                        if (first >= 0 && tick > first)
                        {
                            Arena = ArenaKey;
                            Message = $"{{\"t\":\"final_onchain\",\"net\":\"{Net}\",\"arena\":\"{ArenaKey}\",\"match\":\"{matchHex}\",\"url\":\"\",\"slot_map\":[{slots}]}}";
                            Phase = State.Live;
                            Debug.Log($"[gs] on-chain Final live on {Net}: arena {ArenaKey} tick {tick}");
                            return;
                        }

                        if (first < 0)
                            first = tick;
                    }

                    await Task.Delay(300).ConfigureAwait(false);
                }

                throw new Exception($"arena {ArenaKey} not running the Final within {TimeoutSeconds} s");
            }
            catch (Exception e)
            {
                Error = e.Message;
                Phase = State.Failed;
                Debug.LogWarning($"[gs] on-chain Final handoff failed ({e.Message}): the server runs the Final (Classic)");
            }
        }

        private volatile byte[] _world;

        private async Task PollWorld()
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                _world = await ReadAccount(ErUrl, WorldKey).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[gs] on-chain Final World: " + e.Message);
            }
            finally
            {
                _polling = false;
            }
        }

        private void TryWorldResult(byte[] w)
        {
            if (w == null || w.Length < WaWorld + 2037 || w[WaPart] != 0)
                return;
            var rs = WaWorld + RoundState;
            if (w[rs + 1] != 4 || w[rs + 137] == 0)
                return; // round phase ENDED with a result
            var n = w[rs + 52];
            if (n == 0 || n > 12)
                return;
            for (var k = 0; k < n; k++)
                if (w[rs + 53 + k] >= 12)
                    return; // beta audit: a slot out of range would throw in the director on every tick
            ResultOrder = new int[n];
            ResultReasons = new ElimReason[n];
            for (var k = 0; k < n; k++)
            {
                ResultOrder[k] = w[rs + 53 + k];
                ResultReasons[k] = (ElimReason)w[rs + 52 + 15 + k];
            }

            ResultTicks = BitConverter.ToUInt32(w, rs + 52 + 80) - BitConverter.ToUInt32(w, rs + 52 + 76);
            Phase = State.Done;
            Debug.Log($"[gs] on-chain Final result from the arena World: order {string.Join(",", ResultOrder)} (champion {ResultOrder[0]})");
        }

        /// <summary>While live: polls the arena (2 Hz) for the result. Main thread, every tick.</summary>
        public void Poll()
        {
            if (Phase != State.Live || ResultOrder != null)
                return;
            if (All)
            {
                var w = _world;
                if (w != null)
                {
                    _world = null;
                    var a = _arenaNow;
                    _arenaNow = null;
                    TryAllResult(w, a);
                }

                if (_polling || ChainFailed || ResultOrder != null || string.IsNullOrEmpty(WorldKey))
                    return;
                _polling = true;
                _ = PollAll();
                return;
            }

            if (Rollup)
            {
                var w = _world;
                if (w != null)
                {
                    _world = null;
                    TryWorldResult(w);
                }

                if (_polling)
                    return;
                _polling = true;
                _ = PollWorld();
                return;
            }

            var s = _status;
            if (s != null)
            {
                _status = null;
                TryResult(s);
            }

            if (_polling)
                return;
            _polling = true;
            _ = PollOnce();
        }

        private async Task PollOnce()
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                _status = await Http.GetStringAsync(ControlUrl + "/status").ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[gs] on-chain Final status: " + e.Message);
            }
            finally
            {
                _polling = false;
            }
        }

        [Serializable] private sealed class Status { public string arena; public string state; public Res result; }
        [Serializable] private sealed class Res { public int champion = 255; public int ticks; public Place[] placements; }
        [Serializable] private sealed class Place { public int place; public int slot; public int reason; }

        /// <summary>Tests / tools: a live handoff with a given result.</summary>
        public void ForceForTest(State phase, int[] order = null, ElimReason[] reasons = null)
        {
            Phase = phase;
            ResultOrder = order;
            ResultReasons = reasons;
        }

        /// <summary>Tests: a full-match handoff whose arena match failed (ABORTED / gone / stalled).</summary>
        public void ForceChainFailedForTest(string why)
        {
            All = true;
            Phase = State.Live;
            Fail(why);
        }

        /// <summary>Tests: feeds one WorldAcc / Arena read to the full-match result logic (as <see cref="Poll"/> does).</summary>
        public void FeedAllForTest(byte[] worldAcc, byte[] arena)
        {
            All = true;
            Phase = State.Live;
            TryAllResult(worldAcc, arena);
        }

        private void TryResult(string json)
        {
            Status st;
            try { st = JsonUtility.FromJson<Status>(json); }
            catch { return; }
            var p = st?.result?.placements;
            if (st == null || st.arena != Arena || st.state != "ended" || p == null || p.Length == 0)
                return;
            foreach (var x in p)
                if (x.slot < 0 || x.slot >= 12)
                    return;
            ResultOrder = new int[p.Length];
            ResultReasons = new ElimReason[p.Length];
            for (var k = 0; k < p.Length; k++)
            {
                ResultOrder[k] = p[k].slot;
                ResultReasons[k] = (ElimReason)p[k].reason;
            }

            ResultTicks = (uint)Math.Max(0, st.result.ticks);
            Phase = State.Done;
            Debug.Log($"[gs] on-chain Final result from {Arena}: order {string.Join(",", ResultOrder)} (champion {ResultOrder[0]})");
        }

        private static string Field(string json, string key)
        {
            var k = "\"" + key + "\":";
            var i = json.IndexOf(k, StringComparison.Ordinal);
            if (i < 0)
                return null;
            i += k.Length;
            if (json[i] == '"')
            {
                var e = json.IndexOf('"', i + 1);
                return json.Substring(i + 1, e - i - 1);
            }

            var j = i;
            while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-'))
                j++;
            return json.Substring(i, j - i);
        }
    }
}
