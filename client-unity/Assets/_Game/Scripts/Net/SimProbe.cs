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
        private float _hideAt = float.MaxValue;
        private int _hideSecs;
        private float _startedAt;
        private long _lastCorr, _lastSnapsRec, _lastMatches, _lastSilent, _lastHazMis, _lastRespawns, _lastReplayTicks, _lastReplayCount;
        private double _lastSumCorr;
        private long _lastFrames, _lastExtrap, _lastFrozen;
        // remote stutter: |rendered speed − interpolated server speed| for one moving remote player, per frame
        private readonly RollingSamples _stutter = new RollingSamples(4096);
        private Vec3 _lastRemotePos;
        private int _remoteSlot = -1;

        public static string TargetFromArgs() => GetArg(Arg);

        /// <summary><c>-fallrushProbeView</c>: run windowed with the normal client view (for screenshots).</summary>
        public static bool ViewEnabled => HasArg("-fallrushProbeView");

        private string _shotPath;
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
            if (_shotPath != null && int.TryParse(GetArg("-fallrushScreenshotAt"), out var at))
                _shotAt = Time.realtimeSinceStartup + at;
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
                ClientSim.Instance.InputSource = this;
                SceneManager.LoadScene(NetBootstrap.SandboxScene);
            }
            else if (boot.State == ClientState.Disconnected)
            {
                Debug.Log($"[probe] disconnected: {boot.LastDisconnectReason}");
                Application.Quit(2);
            }
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

            SampleRemoteStutter(cs);

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
                LogPrediction(cs, span);
                _nextLog = now + span;
                _phase++;
            }

            if (_script == "flood" && cs != null && cs.Active)
                Flood();

            if (now >= _shotAt)
            {
                _shotAt = float.MaxValue;
                ScreenCapture.CaptureScreenshot(_shotPath);
                Debug.Log($"[probe] screenshot → {_shotPath}");
            }

            if (now >= _endAt)
            {
                Debug.Log("[probe] done");
                Application.Quit(0);
            }
        }

        private void LogPrediction(ClientSim cs, float span)
        {
            var p = cs.Prediction;
            var r = cs.Remote;
            if (p == null)
                return;
            var corr = p.Corrections - _lastCorr;
            var snaps = p.Snapshots - _lastSnapsRec;
            var replayCount = p.ReplayCount - _lastReplayCount;
            var replayTicks = p.ReplayTicksTotal - _lastReplayTicks;
            var sumCorr = p.SumCorrection - _lastSumCorr;
            var frames = r.Frames - _lastFrames;
            float stP50 = float.NaN, stP95 = float.NaN;
            if (_stutter.Count > 0)
                _stutter.Percentiles(0.5f, 0.95f, out stP50, out stP95);
            var st = p.State;
            Debug.Log($"[probe] pred corrections={corr} per5s={corr * 5f / span:F2} meanCorr={(corr > 0 ? sumCorr / corr * 100 : 0):F1}cm maxCorr={p.MaxCorrection * 100:F1}cm " +
                      $"snaps={snaps} match={p.Matches - _lastMatches} silent={p.SilentResyncs - _lastSilent} hazMismatch={p.HazardMismatches - _lastHazMis} respawns={p.Respawns - _lastRespawns} " +
                      $"replay mean={(replayCount > 0 ? (double)replayTicks / replayCount : 0):F1} last={p.LastReplay} max={p.MaxReplay} " +
                      $"drift meanMm={(p.Snapshots > 0 ? p.SumDriftMm / p.Snapshots : 0):F2} maxMm={p.MaxDriftMm:F1} subMm={p.SubMmMismatches} predHits={p.PredictedHits} " +
                      $"interp delay={r.Delay * 1000:F0}ms target={r.TargetDelay * 1000:F0}ms lateP95={r.LatenessP95Ms:F0}ms extrap={(frames > 0 ? 100.0 * (r.ExtrapolatedFrames - _lastExtrap) / frames : 0):F1}% frozen={(frames > 0 ? 100.0 * (r.FrozenFrames - _lastFrozen) / frames : 0):F1}% lagEvents={r.LagEvents} " +
                      $"remoteStutter p50={stP50:F2} p95={stP95:F2}m/s course={(_course != null ? $"{_course.Current} laps={_course.Laps} falls={_course.Falls}" : "-")} " +
                      $"pos=({st.PosX * 0.001f:F1},{st.PosY * 0.001f:F1},{st.PosZ * 0.001f:F1})");
            _lastCorr = p.Corrections;
            _lastSnapsRec = p.Snapshots;
            _lastMatches = p.Matches;
            _lastSilent = p.SilentResyncs;
            _lastHazMis = p.HazardMismatches;
            _lastRespawns = p.Respawns;
            _lastReplayTicks = p.ReplayTicksTotal;
            _lastReplayCount = p.ReplayCount;
            _lastSumCorr = p.SumCorrection;
            _lastFrames = r.Frames;
            _lastExtrap = r.ExtrapolatedFrames;
            _lastFrozen = r.FrozenFrames;
            _stutter.Clear();
        }

        /// <summary>
        /// Remote-player smoothness: for one remote player that is running on the ground, the difference between the
        /// rendered speed this frame and the interpolated server velocity. 0 = perfectly smooth; spikes = stutter.
        /// </summary>
        private void SampleRemoteStutter(ClientSim cs)
        {
            if (cs == null || !cs.HasSnapshots || Time.unscaledDeltaTime <= 0f)
                return;
            if (_remoteSlot < 0 || !cs.Remote.IsPresent(_remoteSlot) || _remoteSlot == cs.OwnSlot)
            {
                _remoteSlot = -1;
                ref readonly var n = ref cs.Newest;
                for (var i = 0; i < n.Count; i++)
                {
                    if (n.Players[i].Slot != cs.OwnSlot)
                    {
                        _remoteSlot = n.Players[i].Slot;
                        break;
                    }
                }

                if (_remoteSlot < 0)
                    return;
                cs.Remote.Sample(_remoteSlot, out _lastRemotePos, out _, out _);
                return;
            }

            if (!cs.Remote.Sample(_remoteSlot, out var pos, out var vel, out var near))
                return;
            var d = pos - _lastRemotePos;
            _lastRemotePos = pos;
            if ((near.Flags & MotorState.Grounded) == 0 || d.SqrMagnitude > 1f)
                return;
            var renderedSpeed = new Vec3(d.X, 0f, d.Z).Magnitude / Time.unscaledDeltaTime;
            var serverSpeed = new Vec3(vel.X, 0f, vel.Z).Magnitude;
            if (serverSpeed < 1f)
                return;
            _stutter.Add(System.Math.Abs(renderedSpeed - serverSpeed));
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
