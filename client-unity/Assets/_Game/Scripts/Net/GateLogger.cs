using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// MEASUREMENT TOOL: logs the M2 gate numbers every 10 s (prediction corrections, replay, drift, hazard mismatches,
    /// interpolation delay/starvation, remote-player stutter, client FPS). Used by the desktop probe (<see cref="SimProbe"/>)
    /// and by a WebGL client opened with <c>?gate=1</c> (lines go to the browser console).
    /// </summary>
    public sealed class GateLogger : MonoBehaviour
    {
        public string Tag = "probe";
        public CourseBot Course;
        /// <summary>When true this component logs on its own every 10 s; the probe calls <see cref="Log"/> itself.</summary>
        public bool AutoLog = true;

        private long _lastCorr, _lastPushCorr, _lastSnapsRec, _lastMatches, _lastSilent, _lastHazMis, _lastRespawns, _lastReplayTicks, _lastReplayCount;
        private double _lastSumCorr;
        private long _lastFrames, _lastExtrap, _lastFrozen;
        // remote stutter: |rendered speed − interpolated server speed| for one moving remote player, per frame
        private readonly RollingSamples _stutter = new RollingSamples(4096);
        private readonly RollingSamples _frameMs = new RollingSamples(4096);
        private Vec3 _lastRemotePos;
        private int _remoteSlot = -1;
        private float _next;

        private void Start() => _next = Time.realtimeSinceStartup + 10f;

        private void LateUpdate()
        {
            var cs = ClientSim.Instance;
            SampleRemoteStutter(cs);
            _frameMs.Add(Time.unscaledDeltaTime * 1000f);
            if (AutoLog && Time.realtimeSinceStartup >= _next)
            {
                _next = Time.realtimeSinceStartup + 10f;
                Log(10f);
            }
        }

        public void Log(float span)
        {
            var cs = ClientSim.Instance;
            if (cs == null)
                return;
            var p = cs.Prediction;
            var r = cs.Remote;
            if (p == null)
                return;
            var corr = p.Corrections - _lastCorr;
            var pushCorr = p.PushCorrections - _lastPushCorr;
            var snaps = p.Snapshots - _lastSnapsRec;
            var replayCount = p.ReplayCount - _lastReplayCount;
            var replayTicks = p.ReplayTicksTotal - _lastReplayTicks;
            var sumCorr = p.SumCorrection - _lastSumCorr;
            var frames = r.Frames - _lastFrames;
            float stP50 = float.NaN, stP95 = float.NaN;
            if (_stutter.Count > 0)
                _stutter.Percentiles(0.5f, 0.95f, out stP50, out stP95);
            var st = p.State;
            Debug.Log($"[{Tag}] pred corrections={corr} per5s={corr * 5f / span:F2} pushCorrections={pushCorr} meanCorr={(corr > 0 ? sumCorr / corr * 100 : 0):F1}cm maxCorr={p.MaxCorrection * 100:F1}cm " +
                      $"snaps={snaps} match={p.Matches - _lastMatches} silent={p.SilentResyncs - _lastSilent} hazMismatch={p.HazardMismatches - _lastHazMis} respawns={p.Respawns - _lastRespawns} " +
                      $"replay mean={(replayCount > 0 ? (double)replayTicks / replayCount : 0):F1} last={p.LastReplay} max={p.MaxReplay} " +
                      $"drift meanMm={(p.DriftSamples > 0 ? p.SumDriftMm / p.DriftSamples : 0):F2} maxMm={p.MaxDriftMm:F1} subMm={p.SubMmMismatches} predHits={p.PredictedHits} " +
                      $"interp delay={r.Delay * 1000:F0}ms target={r.TargetDelay * 1000:F0}ms lateP95={r.LatenessP95Ms:F0}ms extrap={(frames > 0 ? 100.0 * (r.ExtrapolatedFrames - _lastExtrap) / frames : 0):F1}% frozen={(frames > 0 ? 100.0 * (r.FrozenFrames - _lastFrozen) / frames : 0):F1}% lagEvents={r.LagEvents} " +
                      $"remoteStutter p50={stP50:F2} p95={stP95:F2}m/s course={(Course != null ? $"{Course.Current} laps={Course.Laps} falls={Course.Falls}" : "-")} " +
                      $"pos=({st.PosX * 0.001f:F1},{st.PosY * 0.001f:F1},{st.PosZ * 0.001f:F1})");
            _lastCorr = p.Corrections;
            _lastPushCorr = p.PushCorrections;
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
            _frameMs.Percentiles(0.5f, 0.95f, out var f50, out var f95);
            Debug.Log($"[{Tag}] frame p50={f50:F1}ms p95={f95:F1}ms fps={1000f / Mathf.Max(0.1f, f50):F0} ping p50={(PingService.Instance != null ? PingService.Instance.P50Ms : float.NaN):F0} p95={(PingService.Instance != null ? PingService.Instance.P95Ms : float.NaN):F0}ms lead={cs.Clock.LastReportedLead}/{cs.Clock.TargetLead} resyncs={cs.Clock.Resyncs}");
            _frameMs.Clear();
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

    }
}
