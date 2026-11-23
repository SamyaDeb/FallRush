using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-D: the emulator WebSocket protocol (docs/proof/p3/wire-v0.md §4) over <see cref="ClientWebSocket"/>, for the
    /// Editor and the Mac player. Also talks to tools/p3/chain-relay.mjs, which speaks the same protocol in front of the
    /// real rollup (the browser bundle in Node). Receive and send run on background tasks; the game drains queues on
    /// the main thread. Reconnects with backoff and re-joins the same slot.
    /// </summary>
    public sealed class WsChainTransport : IChainTransport
    {
        private ChainConfig _cfg;
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private readonly ConcurrentQueue<WorldMsg> _worlds = new ConcurrentQueue<WorldMsg>();
        private readonly ConcurrentQueue<InputConfirm> _confirms = new ConcurrentQueue<InputConfirm>();
        private readonly ConcurrentQueue<(string text, double at)> _texts = new ConcurrentQueue<(string text, double at)>();
        private readonly float[] _rtt = new float[16];
        private int _rttN;
        private readonly ConcurrentQueue<(byte[] data, bool text)> _out = new ConcurrentQueue<(byte[] data, bool text)>();
        private readonly SemaphoreSlim _outSignal = new SemaphoreSlim(0);
        private readonly Dictionary<uint, double> _sentAt = new Dictionary<uint, double>();
        private readonly object _sentLock = new object();
        private volatile bool _connected;
        private uint _txid;
        private uint _lastSeq;
        private double _lastWorldAt, _lastPollAt, _lastPingAt;
        private string _joinJson;
        private volatile int _reconnecting;
        private bool _disposed;
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        public string Name => "emulator-ws";
        public string Error { get; private set; }
        public bool Connected => _connected;
        public JoinInfo Joined { get; private set; }
        public float RttMs { get; private set; } = float.NaN;
        public long Gaps { get; private set; }
        public long Polls { get; private set; }
        public long Reconnects { get; private set; }

        private static double NowS => Clock.Elapsed.TotalSeconds;

        public void Connect(ChainConfig cfg)
        {
            _cfg = cfg;
            _cts = new CancellationTokenSource();
            _ = Run(_cts.Token);
        }

        private async Task Run(CancellationToken ct)
        {
            var backoff = 250;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _ws = new ClientWebSocket();
                    await _ws.ConnectAsync(new Uri(_cfg.url), ct);
                    _connected = true;
                    Error = null;
                    backoff = 250;
                    if (_joinJson != null)
                        Enqueue(Encoding.UTF8.GetBytes(_joinJson), true); // re-join after a reconnect
                    var send = SendLoop(_ws, ct);
                    await ReceiveLoop(_ws, ct);
                    await send;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    Error = $"chain link {_cfg.url}: {e.Message}";
                }

                _connected = false;
                if (ct.IsCancellationRequested)
                    break;
                Reconnects++;
                try { await Task.Delay(backoff, ct); } catch (TaskCanceledException) { break; }
                backoff = Math.Min(backoff * 2, 4000);
            }
        }

        private async Task SendLoop(ClientWebSocket ws, CancellationToken ct)
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                try { await _outSignal.WaitAsync(250, ct); } catch (OperationCanceledException) { return; }
                while (_out.TryDequeue(out var m))
                {
                    if (ws.State != WebSocketState.Open)
                        return;
                    await ws.SendAsync(new ArraySegment<byte>(m.data), m.text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ct);
                }
            }
        }

        private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var n = 0;
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, n, buf.Length - n), ct);
                    n += r.Count;
                } while (!r.EndOfMessage && n < buf.Length);

                if (r.MessageType == WebSocketMessageType.Close)
                    return;
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    _texts.Enqueue((Encoding.UTF8.GetString(buf, 0, n), NowS)); // arrival time on the receive thread (not the main thread's next frame)
                    continue;
                }

                if (n < 1)
                    continue;
                var at = NowS;
                switch (buf[0])
                {
                    case 0x90 when n > 5:
                    case 0x91 when n > 5:
                        var acc = new byte[n - 5];
                        Buffer.BlockCopy(buf, 5, acc, 0, n - 5);
                        _worlds.Enqueue(new WorldMsg { Acc = acc, Len = acc.Length, Seq = BitConverter.ToUInt32(buf, 1), RecvAt = at, FromPoll = buf[0] == 0x91 });
                        break;
                    case 0x83 when n >= 10:
                        var id = BitConverter.ToUInt32(buf, 1);
                        double sent;
                        lock (_sentLock)
                        {
                            if (!_sentAt.TryGetValue(id, out sent))
                                sent = at;
                            _sentAt.Remove(id);
                        }

                        _confirms.Enqueue(new InputConfirm { TxId = id, Status = buf[5], ChainTick = BitConverter.ToUInt32(buf, 6), LatencyMs = (float)((at - sent) * 1000.0) });
                        break;
                }
            }
        }

        private void Enqueue(byte[] data, bool text)
        {
            _out.Enqueue((data, text));
            _outSignal.Release();
        }

        public void Join(string name, int slot, bool spectate, string match = null)
        {
            var n = (name ?? "Rushling").Replace("\"", "").Replace("\\", "");
            _joinJson = $"{{\"t\":\"join\",\"name\":\"{n}\",\"spectate\":{(spectate ? "true" : "false")}{(slot >= 0 ? ",\"slot\":" + slot : "")}{(string.IsNullOrEmpty(match) ? "" : ",\"match\":\"" + match + "\"")}}}";
            Enqueue(Encoding.UTF8.GetBytes(_joinJson), true);
        }

        private readonly Dictionary<int, string> _econ = new Dictionary<int, string>();

        public void Econ(int id, string action, string argsJson)
        {
            var a = (action ?? "").Replace("\"", "");
            Enqueue(Encoding.UTF8.GetBytes($"{{\"t\":\"econ\",\"id\":{id},\"a\":\"{a}\",\"args\":{(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson)}}}"), true);
        }

        public string EconResult(int id)
        {
            if (!_econ.TryGetValue(id, out var r))
                return null;
            _econ.Remove(id);
            return r;
        }

        public uint SendInputs(byte[] batch, int len)
        {
            var id = ++_txid;
            var f = new byte[5 + len];
            f[0] = 0x02;
            BitConverter.GetBytes(id).CopyTo(f, 1);
            Buffer.BlockCopy(batch, 0, f, 5, len);
            lock (_sentLock)
            {
                _sentAt[id] = NowS;
                if (_sentAt.Count > 512)
                    _sentAt.Clear();
            }

            Enqueue(f, false);
            return id;
        }

        public void Pump(double now)
        {
            var t = NowS;
            while (_texts.TryDequeue(out var s))
                OnText(s.text, s.at);
            if (!_connected)
                return;
            if (t - _lastPingAt >= 1.0)
            {
                _lastPingAt = t;
                Enqueue(Encoding.UTF8.GetBytes($"{{\"t\":\"ping\",\"c\":{(long)(t * 1000.0)}}}"), true);
            }

            // Backup poll: no notification for a second → ask for the account.
            if (Joined != null && t - _lastWorldAt >= _cfg.pollSeconds && t - _lastPollAt >= _cfg.pollSeconds)
            {
                _lastPollAt = t;
                Polls++;
                Enqueue(Encoding.UTF8.GetBytes("{\"t\":\"get\"}"), true);
            }
        }

        private void OnText(string s, double t)
        {
            if (s.Contains("\"t\":\"pong\""))
            {
                // {"t":"pong","c":<client ms>[,"er":<the relay's rollup RTT ms>]}
                var c = Num(s, "\"c\":");
                if (!double.IsNaN(c))
                {
                    var er = Num(s, "\"er\":");
                    _rtt[_rttN++ % _rtt.Length] = (float)(t * 1000.0 - c + (double.IsNaN(er) ? 0 : er));
                    var n = Math.Min(_rttN, _rtt.Length);
                    var a = new float[n];
                    Array.Copy(_rtt, a, n);
                    Array.Sort(a);
                    RttMs = a[n / 2]; // median of the last 16 probes: robust to one slow frame or a GC pause
                }

                return;
            }

            if (s.StartsWith("{\"t\":\"econ_r\"", StringComparison.Ordinal))
            {
                // {"t":"econ_r","id":N,"r":{…}}
                var id = (int)Num(s, "\"id\":");
                var k = s.IndexOf("\"r\":", StringComparison.Ordinal);
                if (k > 0)
                    _econ[id] = s.Substring(k + 4, s.Length - k - 5);
                return;
            }

            if (s.Contains("\"t\":\"joined\""))
            {
                Joined = JsonUtility.FromJson<JoinInfo>(s);
                return;
            }

            if (s.Contains("\"t\":\"full\"") || s.Contains("\"t\":\"err\""))
            {
                var j = JsonUtility.FromJson<JoinInfo>(s);
                Error = "arena refused the join: " + (j.why ?? j.t);
            }
        }

        /// <summary>The number after <paramref name="key"/> in a flat JSON text, or NaN (null / missing).</summary>
        public static double Num(string s, string key)
        {
            var i = s.IndexOf(key, StringComparison.Ordinal);
            if (i < 0)
                return double.NaN;
            i += key.Length;
            var j = i;
            while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.' || s[j] == '-' || s[j] == 'e' || s[j] == 'E' || s[j] == '+'))
                j++;
            return double.TryParse(s.Substring(i, j - i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        }

        public int DrainWorlds(List<WorldMsg> into)
        {
            var n = 0;
            while (_worlds.TryDequeue(out var w))
            {
                // Convert the background stopwatch time to the game clock (both monotonic; same origin shift).
                w.RecvAt = Time.realtimeSinceStartupAsDouble - (NowS - w.RecvAt);
                if (!w.FromPoll)
                {
                    if (_lastSeq != 0 && w.Seq > _lastSeq + 1)
                        Gaps += w.Seq - _lastSeq - 1;
                    if (w.Seq > _lastSeq)
                        _lastSeq = w.Seq;
                }

                _lastWorldAt = NowS;
                into.Add(w);
                n++;
            }

            return n;
        }

        public int DrainConfirms(List<InputConfirm> into)
        {
            var n = 0;
            while (_confirms.TryDequeue(out var c))
            {
                into.Add(c);
                n++;
            }

            return n;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts?.Cancel();
            try { _ws?.Abort(); } catch { /* closing */ }
            _ws?.Dispose();
        }
    }
}
