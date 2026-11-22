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

        public ChainFinalHandoff()
        {
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

        /// <summary>While live: polls the arena (2 Hz) for the result. Main thread, every tick.</summary>
        public void Poll()
        {
            if (Phase != State.Live || ResultOrder != null)
                return;
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
