using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-B/D (WebGL): chain I/O through the browser bundle (<c>StreamingAssets/fallrush-chain.js</c>, packages/chain) via
    /// <c>FallRushChain.jslib</c>. The bundle talks to the emulator (wire-v0) or the real rollup per its config
    /// (<c>StreamingAssets/chain/&lt;target&gt;.json</c>) and hands Worlds over in its normalised WorldView layout
    /// (<see cref="Layout"/>) through one shared buffer per frame (<c>drainWorlds</c>).
    /// </summary>
    public sealed class BundleChainTransport : IChainTransport
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void fr_boot(string bundleUrl, string configUrl, string emulatorUrl);
        [DllImport("__Internal")] private static extern int fr_state();
        [DllImport("__Internal")] private static extern double fr_now();
        [DllImport("__Internal")] private static extern string fr_error();
        [DllImport("__Internal")] private static extern void fr_join(string args);
        [DllImport("__Internal")] private static extern string fr_join_result();
        [DllImport("__Internal")] private static extern void fr_subscribe(string addr);
        [DllImport("__Internal")] private static extern uint fr_send_inputs(byte[] ptr, int len);
        [DllImport("__Internal")] private static extern int fr_drain_worlds(byte[] ptr, int cap, int maxItems);
        [DllImport("__Internal")] private static extern string fr_drain_events();
        [DllImport("__Internal")] private static extern string fr_stats();
        [DllImport("__Internal")] private static extern void fr_stop();
#else
        private static void fr_boot(string a, string b, string c) => throw new PlatformNotSupportedException("the chain bundle runs in WebGL players only");
        private static int fr_state() => -1;
        private static double fr_now() => 0;
        private static string fr_error() => "WebGL only";
        private static void fr_join(string a) { }
        private static string fr_join_result() => null;
        private static void fr_subscribe(string a) { }
        private static uint fr_send_inputs(byte[] p, int l) => 0;
        private static int fr_drain_worlds(byte[] p, int c, int m) => 0;
        private static string fr_drain_events() => "[]";
        private static string fr_stats() => "{}";
        private static void fr_stop() { }
#endif

        /// <summary>The bundle's normalised WorldView: tick 0, core 4, seq 8, flags 12, len 14, applied 16, World at 64.</summary>
        public static readonly WorldAccLayout Layout = new WorldAccLayout
        {
            offset = 64, lenOffset = 14, tickOffset = 0, appliedTickOffset = 16, coreVersionOffset = 4, arenaSeqOffset = 8, flagsOffset = 12,
        };

        private readonly byte[] _buf = new byte[64 * 2200];
        private string _pendingJoin;
        private bool _joinSent;
        private double _statsAt;

        public string Name => "bundle";
        public string Error { get; private set; }
        public bool Connected => fr_state() == 1;
        public JoinInfo Joined { get; private set; }
        public float RttMs { get; private set; } = float.NaN;
        public long Gaps { get; private set; }
        public long Polls { get; private set; }
        public long Reconnects { get; private set; }

        [Serializable] private sealed class Ev { public string type; public uint txid; public string status; public float ms; public uint chainTick; public string why; public string err; }
        [Serializable] private sealed class Evs { public Ev[] e; }
        [Serializable] private sealed class Stats { public float rttMs = float.NaN; public long gaps, polls, reconnects; }
        [Serializable] private sealed class JoinReply { public bool ok; public string err; }

        public void Connect(ChainConfig cfg)
        {
            var sa = Application.streamingAssetsPath;
            // emulator: the session's URL (config default, ?onchainurl=, or the game server's fonc); rollup: the config's RPCs
            var url = cfg.target == "emu" ? cfg.url : "";
            fr_boot(sa + "/fallrush-chain.js", sa + "/chain/" + cfg.bundleConfig, url);
        }

        public void Join(string name, int slot, bool spectate, string match = null)
        {
            var n = (name ?? "Rushling").Replace("\"", "").Replace("\\", "");
            _pendingJoin = $"{{\"name\":\"{n}\",\"spectate\":{(spectate ? "true" : "false")}{(slot >= 0 ? ",\"slot\":" + slot : "")}{(string.IsNullOrEmpty(match) ? "" : ",\"match\":\"" + match + "\"")}}}";
            _joinSent = false;
        }

        public uint SendInputs(byte[] batch, int len) => Connected ? fr_send_inputs(batch, len) : 0;

        private readonly List<InputConfirm> _confirms = new List<InputConfirm>();

        public void Pump(double now)
        {
            var st = fr_state();
            if (st < 0)
            {
                Error = "chain bundle: " + fr_error();
                return;
            }

            if (st != 1)
                return;
            if (_pendingJoin != null && !_joinSent)
            {
                _joinSent = true;
                fr_join(_pendingJoin);
            }

            var jr = fr_join_result();
            if (!string.IsNullOrEmpty(jr))
            {
                var r = JsonUtility.FromJson<JoinReply>(jr);
                if (r.ok)
                {
                    Joined = JsonUtility.FromJson<JoinInfo>(jr);
                    fr_subscribe("$world"); // the bundle queues World notifications only while subscribed
                }
                else
                    Error = "join failed: " + r.err;
            }

            var evs = fr_drain_events();
            if (evs != null && evs.Length > 2)
            {
                var e = JsonUtility.FromJson<Evs>("{\"e\":" + evs + "}");
                foreach (var x in e.e ?? Array.Empty<Ev>())
                {
                    if (x.type == "confirm")
                        _confirms.Add(new InputConfirm { TxId = x.txid, Status = (byte)(x.status == "ok" ? 0 : x.status == "late" ? 1 : x.status == "window" ? 2 : x.status == "badSlot" ? 3 : x.status == "timeout" ? 4 : x.err != null && x.err.Contains("6027") ? 5 : x.err != null && (x.err.Contains("6020")) ? 2 : x.err != null && (x.err.Contains("6000") || x.err.Contains("6005")) ? 3 : 255), ChainTick = x.chainTick, LatencyMs = x.ms });
                    else if (x.type == "error")
                        Debug.LogWarning("[frc] " + x.why);
                }
            }

            if (now - _statsAt >= 1.0)
            {
                _statsAt = now;
                var s = JsonUtility.FromJson<Stats>(fr_stats());
                RttMs = s.rttMs;
                Gaps = s.gaps;
                Polls = s.polls;
                Reconnects = s.reconnects;
            }
        }

        public int DrainWorlds(List<WorldMsg> into)
        {
            if (!Connected)
                return 0;
            var n = fr_drain_worlds(_buf, _buf.Length, 64);
            var perfNow = fr_now();
            var tNow = Time.realtimeSinceStartupAsDouble;
            var o = 0;
            var c = 0;
            while (o + 16 <= n)
            {
                var len = BitConverter.ToInt32(_buf, o);
                var recvMs = BitConverter.ToDouble(_buf, o + 4);
                var seq = BitConverter.ToUInt32(_buf, o + 12);
                o += 16;
                if (len <= 0 || o + len > n)
                    break;
                var acc = new byte[len];
                Buffer.BlockCopy(_buf, o, acc, 0, len);
                o += len;
                into.Add(new WorldMsg { Acc = acc, Len = len, Seq = seq, RecvAt = tNow - (perfNow - recvMs) / 1000.0 });
                c++;
            }

            return c;
        }

        public int DrainConfirms(List<InputConfirm> into)
        {
            into.AddRange(_confirms);
            var n = _confirms.Count;
            _confirms.Clear();
            return n;
        }

        public void Dispose() => fr_stop();
    }
}
