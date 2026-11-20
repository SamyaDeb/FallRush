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
    /// Scripts: <c>run</c> (run back and forth along Z with hops), <c>idle</c>.
    /// </summary>
    public sealed class SimProbe : MonoBehaviour, IInputSource
    {
        public const string Arg = "-fallrushProbe";

        private float _endAt;
        private float _nextLog;
        private string _script = "run";
        private long _lastIn, _lastOut, _lastSnaps;
        private int _phase;

        public static string TargetFromArgs() => GetArg(Arg);

        private void Start()
        {
            _script = GetArg("-fallrushProbeScript") ?? "run";
            var secs = int.TryParse(GetArg("-fallrushProbeSecs"), out var s) ? s : 60;
            _endAt = Time.realtimeSinceStartup + secs;
            _nextLog = Time.realtimeSinceStartup + 10f;
            Application.targetFrameRate = 60;
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
            if (_script == "idle")
                return;
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
                _nextLog = now + span;
                _phase++;
            }

            if (now >= _endAt)
            {
                Debug.Log("[probe] done");
                Application.Quit(0);
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
