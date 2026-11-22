using System;
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

        public static bool Enabled =>
            Array.IndexOf(Environment.GetCommandLineArgs(), "-fallrushOnchainFinal") >= 0 || Environment.GetEnvironmentVariable("GAMESERVER_ONCHAIN_FINAL") == "1";

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
            ControlUrl = (ServerSim.ArgStr("-fallrushOnchainFinalUrl", "GAMESERVER_ONCHAIN_FINAL_URL") ?? "http://127.0.0.1:8787").TrimEnd('/');
            ClientUrl = ServerSim.ArgStr("-fallrushOnchainClientUrl", "GAMESERVER_ONCHAIN_CLIENT_URL") ?? ControlUrl.Replace("http", "ws") + "/";
        }

        /// <summary>Starts the handoff for <paramref name="roster"/> (the alive slots: slot, name, bot).</summary>
        public void Begin(string matchHex, ulong seed, (int slot, string name, bool bot)[] roster)
        {
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

        private async Task RunRollup(string matchHex, string slots)
        {
            try
            {
                if (string.IsNullOrEmpty(WorldKey))
                    throw new Exception("-fallrushOnchainWorld (the arena's WorldAcc address) is required in rollup mode");
                var t0 = DateTime.UtcNow;
                long first = -1;
                // live = the arena is RUNNING the Final (start_final done: WorldAcc round 4) and its tick advances
                while ((DateTime.UtcNow - t0).TotalSeconds < LiveTimeoutSeconds)
                {
                    var a = await ReadAccount(ErUrl, ArenaKey).ConfigureAwait(false);
                    var w = await ReadAccount(ErUrl, WorldKey).ConfigureAwait(false);
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

                throw new Exception($"arena {ArenaKey} not running the Final within {LiveTimeoutSeconds} s");
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

        private void TryResult(string json)
        {
            Status st;
            try { st = JsonUtility.FromJson<Status>(json); }
            catch { return; }
            var p = st?.result?.placements;
            if (st == null || st.arena != Arena || st.state != "ended" || p == null || p.Length == 0)
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
