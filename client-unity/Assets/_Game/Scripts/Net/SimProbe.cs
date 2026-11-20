using System;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FallRush.Net
{
    /// <summary>
    /// MEASUREMENT TOOL (headless client, desktop builds only): connects, enters the sandbox, plays a scripted input
    /// pattern through the real client path (<see cref="ClientSim"/>, same messages as a browser), logs <c>[probe]</c>
    /// stats every 10 s, then quits.
    /// <code>FallRushDevServer -batchmode -nographics -fallrushProbe ws://127.0.0.1:7777 -fallrushProbeSecs 60 -fallrushProbeScript run</code>
    /// Scripts: <c>run</c> (run back and forth along Z with hops), <c>course</c> (<see cref="CourseBot"/>: the M2b hazard
    /// course), <c>idle</c>, <c>flood</c>.
    /// <para><c>-fallrushProbeHide 20:30</c> blocks the main thread for 30 s at t = 20 s (what a hidden browser tab does to
    /// a Unity WebGL page: no frames, no transport updates) — the R12 test.</para>
    /// </summary>
    public sealed class SimProbe : MonoBehaviour, IInputSource
    {
        public const string Arg = "-fallrushProbe";

        private float _endAt;
        private float _nextLog;
        private string _script = "run";
        private long _lastIn, _lastOut, _lastSnaps;
        private int _phase;
        private CourseBot _course;
        private RaceProbeBot _race;
        private MatchProbeBot _match;
        private GateLogger _gate;
        private float _hideAt = float.MaxValue;
        private int _hideSecs;
        private float _startedAt;

        public static string TargetFromArgs() => GetArg(Arg);

        /// <summary><c>-fallrushProbeView</c>: run windowed with the normal client view (for screenshots).</summary>
        public static bool ViewEnabled => HasArg("-fallrushProbeView");

        private string _shotPath;
        private readonly System.Collections.Generic.List<int> _shotTimes = new System.Collections.Generic.List<int>();
        private int _shotIndex;
        private bool _shotMulti;
        private float _shotAt = float.MaxValue;

        private static bool HasArg(string name)
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private void Start()
        {
            _script = GetArg("-fallrushProbeScript") ?? "run";
            var secs = int.TryParse(GetArg("-fallrushProbeSecs"), out var s) ? s : 60;
            _endAt = Time.realtimeSinceStartup + secs;
            _nextLog = Time.realtimeSinceStartup + 10f;
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            _startedAt = Time.realtimeSinceStartup;
            var hide = GetArg("-fallrushProbeHide");
            if (hide != null)
            {
                var parts = hide.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var hAt) && int.TryParse(parts[1], out var hSecs))
                {
                    _hideAt = _startedAt + hAt;
                    _hideSecs = hSecs;
                }
            }

            _shotPath = GetArg("-fallrushScreenshot");
            // -fallrushScreenshotAt 25 or 25,31,37 (several shots: <path>-<seconds>.png)
            var atArg = GetArg("-fallrushScreenshotAt");
            if (_shotPath != null && atArg != null)
            {
                foreach (var part in atArg.Split(','))
                    if (int.TryParse(part, out var at))
                        _shotTimes.Add(at);
                _shotMulti = _shotTimes.Count > 1;
                _shotTimes.Sort();
                if (_shotTimes.Count > 0)
                    _shotAt = Time.realtimeSinceStartup + _shotTimes[0];
            }
            Debug.Log($"[probe] start target={NetBootstrap.Instance.Target.Url} script={_script} secs={secs}");
            NetBootstrap.Instance.StateChanged += OnState;
            NetBootstrap.Instance.Connect();
        }

        private void OnDestroy()
        {
            if (NetBootstrap.Instance != null)
                NetBootstrap.Instance.StateChanged -= OnState;
        }

        private void OnState()
        {
            var boot = NetBootstrap.Instance;
            if (boot.State == ClientState.Connected)
            {
                if (_script == "course")
                    _course = new CourseBot(ClientSim.Instance);
                if (_script == "race")
                    _race = new RaceProbeBot(ClientSim.Instance, (uint)Environment.TickCount);
                if (_script == "match")
                    _match ??= new MatchProbeBot(ClientSim.Instance, (uint)Environment.TickCount, GetArg("-fallrushProbeTier") == "reference" ? BotTier.Reference : BotTier.Normal);
                _gate = gameObject.AddComponent<GateLogger>();
                _gate.Course = _course;
                _gate.Race = _race;
                _gate.AutoLog = false; // logged together with the [probe] line
                ClientSim.Instance.InputSource = this;
                ClientSim.Instance.FollowScenes = true; // the round message says which map to load (M4: every round)
            }
            else if (boot.State == ClientState.Disconnected)
            {
                Debug.Log($"[probe] disconnected: {boot.LastDisconnectReason}");
                Application.Quit(2);
            }

            // Keep the probe in its scene flow: the lobby room has no input script (it stands), rounds use the script.
        }

        public void Sample(ref InputCmd cmd)
        {
            if (_script == "idle" || _script == "flood")
                return;
            if (_course != null)
            {
                _course.Sample(ref cmd);
                return;
            }

            if (_race != null)
            {
                _race.Sample(ref cmd);
                return;
            }

            if (_match != null)
            {
                _match.Sample(ref cmd);
                return;
            }

            // 6 s forward, 6 s back; hop every 1.5 s.
            var t = (int)(cmd.Tick % 360);
            cmd.MoveX = 0;
            cmd.MoveY = (sbyte)(t < 180 ? 127 : -127);
            cmd.CamYaw = (ushort)(t < 180 ? 0 : 32768);
            if (cmd.Tick % 45 == 0)
                cmd.Buttons = InputCmd.JumpPressed | InputCmd.JumpHeld;
        }

        private void Update()
        {
            var now = Time.realtimeSinceStartup;
            var cs = ClientSim.Instance;

            if (cs != null && !cs.Active && MapInfo.Current != null)
                cs.Active = true;

            if (now >= _hideAt)
            {
                _hideAt = float.MaxValue;
                Debug.Log($"[probe] hide: main thread blocked for {_hideSecs} s (hidden-tab simulation)");
                System.Threading.Thread.Sleep(_hideSecs * 1000);
                Debug.Log($"[probe] unhide: connected={Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsConnectedClient}");
            }

            if (now >= _nextLog && cs != null)
            {
                var ping = PingService.Instance;
                var dIn = cs.BytesIn - _lastIn;
                var dOut = cs.BytesOut - _lastOut;
                var dSn = cs.SnapsIn - _lastSnaps;
                _lastIn = cs.BytesIn;
                _lastOut = cs.BytesOut;
                _lastSnaps = cs.SnapsIn;
                var span = 10f;
                Debug.Log($"[probe] snapHz={dSn / span:F1} payloadIn={dIn / span:F0}B/s payloadOut={dOut / span:F0}B/s rtt p50={ping?.P50Ms:F1} p95={ping?.P95Ms:F1}ms " +
                          $"serverTick={(cs.HasSnapshots ? cs.Newest.Tick : 0)} predTick={cs.Clock.PredictedTick} lead={cs.Clock.LastReportedLead} target={cs.Clock.TargetLead} rate={cs.Clock.Rate:F3} resyncs={cs.Clock.Resyncs} lateReports={cs.Clock.LateReports} slot={cs.OwnSlot} ack={cs.LastAckSeq} seq={cs.Seq} bad={cs.BadSnaps} old={cs.OldSnaps} fps={1f / Mathf.Max(1e-4f, Time.smoothDeltaTime):F0}");
                _gate?.Log(span);
                _nextLog = now + span;
                _phase++;
            }

            if (_script == "flood" && cs != null && cs.Active)
                Flood();

            PhaseShots(cs, now);

            if (now >= _shotAt)
            {
                var t = _shotTimes[_shotIndex];
                var path = _shotMulti ? _shotPath.Replace(".png", $"-{t}.png") : _shotPath;
                ScreenCapture.CaptureScreenshot(path);
                Debug.Log($"[probe] screenshot → {path}");
                _shotIndex++;
                _shotAt = _shotIndex < _shotTimes.Count ? _startedAt + _shotTimes[_shotIndex] : float.MaxValue;
            }

            if (now >= _endAt)
            {
                Debug.Log("[probe] done");
                Application.Quit(0);
            }
        }

        // ---- flood script (doc 11: size/rate caps, drop + count, kick after 50 violations/min) ----
        private readonly System.Random _rng = new System.Random(11);
        private readonly byte[] _junk = new byte[64];
        public long FloodSent;

        private void Flood()
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient)
                return;
            for (var i = 0; i < 200; i++)
            {
                var len = _rng.Next(0, _junk.Length);
                _rng.NextBytes(_junk);
                if ((i & 3) == 0 && len >= 14)
                {
                    // Well-formed but far-future command.
                    len = 14;
                    _junk[0] = 1;
                    _junk[5] = 0xFF;
                    _junk[6] = 0xFF;
                    _junk[7] = 0xFF;
                    _junk[8] = 0x7F;
                }

                using var w = new Unity.Netcode.FastBufferWriter(Math.Max(1, len), Unity.Collections.Allocator.Temp);
                w.WriteBytesSafe(_junk, len);
                nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Input, Unity.Netcode.NetworkManager.ServerClientId, w, Unity.Netcode.NetworkDelivery.Unreliable);
                FloodSent++;
            }
        }

        // ---- -fallrushShotDir dir: a screenshot of every match screen (proof), named by phase / round / map ----
        private string _shotDir;
        private string _lastShotKey;
        private float _shotDue = float.MaxValue;
        private string _shotName;
        private readonly System.Collections.Generic.HashSet<string> _shotTaken = new System.Collections.Generic.HashSet<string>();

        private void PhaseShots(ClientSim cs, float now)
        {
            _shotDir ??= GetArg("-fallrushShotDir") ?? string.Empty;
            if (_shotDir.Length == 0 || cs == null || cs.Match == null)
                return;
            var m = cs.Match;
            var map = cs.Round.HasValue ? MapPoolV1.KeyOf(cs.Round.Value.MapId) : "none";
            var key = $"{(int)m.Phase:00}-{m.Phase}-r{m.Round}-{map}";
            if (cs.Phase == RoundPhase.Rush && m.Phase == MatchPhase.RoundActive)
                key = (cs.OwnOutOfSim ? "spectate-" : "play-") + map;
            if (key != _lastShotKey)
            {
                _lastShotKey = key;
                if (!_shotTaken.Contains(key))
                {
                    _shotName = key;
                    _shotDue = now + (key.StartsWith("play-") ? 12f : key.StartsWith("spectate-") ? 4f : m.Phase == MatchPhase.Champion ? 3f : 2f);
                }
            }

            if (now >= _shotDue)
            {
                _shotDue = float.MaxValue;
                _shotTaken.Add(_shotName);
                var path = System.IO.Path.Combine(_shotDir, _shotName + ".png");
                ScreenCapture.CaptureScreenshot(path);
                Debug.Log($"[probe] screenshot → {path}");
            }
        }

                private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
