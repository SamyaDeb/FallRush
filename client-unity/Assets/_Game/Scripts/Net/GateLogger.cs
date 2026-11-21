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
        public RaceProbeBot Race;
        /// <summary>When true this component logs on its own every 10 s; the probe calls <see cref="Log"/> itself.</summary>
        public bool AutoLog = true;

        private long _lastHazCorr;
        private ClientPrediction _pred;
        private long _lastCorr, _lastPushCorr, _lastSnapsRec, _lastMatches, _lastSilent, _lastHazMis, _lastHazExp, _lastRespawns, _lastReplayTicks, _lastReplayCount;
        private double _lastSumCorr;
        private long _lastFrames, _lastExtrap, _lastFrozen;
        // remote stutter: |rendered speed − interpolated server speed| for one moving remote player, per frame
        private readonly RollingSamples _stutter = new RollingSamples(4096);
        private readonly RollingSamples _frameMs = new RollingSamples(4096);
        private readonly RollingSamples _dcFrame = new RollingSamples(4096);
        private long _trisMax;
        private float _texAt;
        private float _texMb = -1f;

        /// <summary>G2.4: estimated texture memory of every loaded texture (width × height × format bytes, × 4/3 with mips).</summary>
        private static float TextureMb()
        {
            double bytes = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture>())
            {
                if (t == null || t.width <= 0)
                    continue;
                var bpp = 4.0;
                if (t is Texture2D t2)
                    bpp = t2.format switch
                    {
                        TextureFormat.Alpha8 or TextureFormat.R8 => 1.0,
                        TextureFormat.RGB24 => 3.0,
                        TextureFormat.DXT1 or TextureFormat.ETC_RGB4 or TextureFormat.ETC2_RGB => 0.5,
                        TextureFormat.DXT5 or TextureFormat.BC7 or TextureFormat.ETC2_RGBA8 or TextureFormat.ASTC_4x4 => 1.0,
                        TextureFormat.RGBAHalf => 8.0,
                        _ => 4.0,
                    };
                else if (t is RenderTexture rt)
                    bpp = rt.format == RenderTextureFormat.ARGBHalf || rt.format == RenderTextureFormat.DefaultHDR ? 8.0 : 4.0;
                var mips = t.mipmapCount > 1 ? 4.0 / 3.0 : 1.0;
                bytes += t.width * (double)t.height * bpp * mips;
            }

            return (float)(bytes / (1024.0 * 1024.0));
        }
        private Vec3 _lastRemotePos;
        private int _remoteSlot = -1;
        private float _next;

        private Unity.Profiling.ProfilerRecorder _drawCalls, _batches, _tris, _setPass;

        private void Start()
        {
            _next = Time.realtimeSinceStartup + 10f;
            // Render counters (Unity docs: available in release players too); 0 = not available on this platform.
            _drawCalls = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Draw Calls Count");
            _batches = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Batches Count");
            _tris = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Triangles Count");
            _setPass = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "SetPass Calls Count");
        }

        private void OnDestroy()
        {
            _drawCalls.Dispose();
            _batches.Dispose();
            _tris.Dispose();
            _setPass.Dispose();
        }

        private void LateUpdate()
        {
            var cs = ClientSim.Instance;
            SampleRemoteStutter(cs);
            _frameMs.Add(Time.unscaledDeltaTime * 1000f);
            if (_drawCalls.Valid)
                _dcFrame.Add(_drawCalls.LastValue);
            if (_tris.Valid && _tris.LastValue > _trisMax)
                _trisMax = _tris.LastValue;
            if (Time.realtimeSinceStartup >= _texAt)
            {
                _texAt = Time.realtimeSinceStartup + 10f;
                _texMb = TextureMb();
            }
            if (AutoLog && Time.realtimeSinceStartup >= _next)
            {
                _next = Time.realtimeSinceStartup + 10f;
                Log(10f);
            }
        }

        /// <summary>Graphics level label for the budget line (set by the client, which owns the setting).</summary>
        public static string Gfx = "-";

        public void Log(float span)
        {
            var cs = ClientSim.Instance;
            if (cs == null)
                return;
            var p = cs.Prediction;
            var r = cs.Remote;
            if (p == null)
                return;
            if (!ReferenceEquals(p, _pred))
            {
                // A new round rebuilt the prediction: its counters start from zero.
                _pred = p;
                _lastCorr = _lastPushCorr = _lastSnapsRec = _lastMatches = _lastSilent = _lastHazMis = _lastHazExp = _lastRespawns = _lastReplayTicks = _lastReplayCount = _lastHazCorr = 0;
                _lastSumCorr = 0;
            }
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
                      $"snaps={snaps} match={p.Matches - _lastMatches} silent={p.SilentResyncs - _lastSilent} hazMismatch={p.HazardMismatches - _lastHazMis} hazExplained={p.ExplainedHazardMismatches - _lastHazExp} respawns={p.Respawns - _lastRespawns} " +
                      $"replay mean={(replayCount > 0 ? (double)replayTicks / replayCount : 0):F1} last={p.LastReplay} max={p.MaxReplay} " +
                      $"drift meanMm={(p.DriftSamples > 0 ? p.SumDriftMm / p.DriftSamples : 0):F2} maxMm={p.MaxDriftMm:F1} subMm={p.SubMmMismatches} predHits={p.PredictedHits} " +
                      $"interp delay={r.Delay * 1000:F0}ms target={r.TargetDelay * 1000:F0}ms lateP95={r.LatenessP95Ms:F0}ms extrap={(frames > 0 ? 100.0 * (r.ExtrapolatedFrames - _lastExtrap) / frames : 0):F1}% frozen={(frames > 0 ? 100.0 * (r.FrozenFrames - _lastFrozen) / frames : 0):F1}% lagEvents={r.LagEvents} " +
                      $"remoteStutter p50={stP50:F2} p95={stP95:F2}m/s course={(Course != null ? $"{Course.Current} laps={Course.Laps} falls={Course.Falls}" : "-")} " +
                      $"phase={cs.Phase} cp={cs.OwnRace & PlayerState.RaceCheckpointMask} finished={(cs.OwnRace & PlayerState.RaceFinished) != 0} race={(Race != null && Race.Bot != null ? $"{Race.Bot.CurrentRouteName}#{Race.Bot.Waypoint} rounds={Race.Laps}" : "-")} hazardCorr={p.HazardCorrections - _lastHazCorr} " +
                      $"pos=({st.PosX * 0.001f:F1},{st.PosY * 0.001f:F1},{st.PosZ * 0.001f:F1}) " +
                      $"m4 match={(cs.Match != null ? cs.Match.Phase.ToString() : "-")} round={(cs.Match != null ? cs.Match.Round : 0)} map={(cs.Round.HasValue ? MapPoolV1.KeyOf(cs.Round.Value.MapId) : "-")} slot={cs.MatchSlot} out={cs.OwnOutOfSim} " +
                      $"rxIn={cs.RxReceived} rxLate={cs.RxLate} rxLateMax={cs.RxLateMaxTicks} rxGaps={cs.RxGaps} matchBytes={cs.MatchBytesIn} rxBytes={cs.RxBytesIn} bytesIn={cs.BytesIn} events={cs.MatchEvents.Count}");
            _lastCorr = p.Corrections;
            _lastPushCorr = p.PushCorrections;
            _lastSnapsRec = p.Snapshots;
            _lastMatches = p.Matches;
            _lastSilent = p.SilentResyncs;
            _lastHazMis = p.HazardMismatches;
            _lastHazExp = p.ExplainedHazardMismatches;
            _lastHazCorr = p.HazardCorrections;
            _lastRespawns = p.Respawns;
            _lastReplayTicks = p.ReplayTicksTotal;
            _lastReplayCount = p.ReplayCount;
            _lastSumCorr = p.SumCorrection;
            _lastFrames = r.Frames;
            _lastExtrap = r.ExtrapolatedFrames;
            _lastFrozen = r.FrozenFrames;
            _stutter.Clear();
            _frameMs.Percentiles(0.5f, 0.95f, out var f50, out var f95);
            _frameMs.Percentiles(0.99f, 0.999f, out var f99, out _);
            float dc50 = -1f, dcMax = -1f;
            if (_dcFrame.Count > 0)
                _dcFrame.Percentiles(0.5f, 1f, out dc50, out dcMax);
            Debug.Log($"[{Tag}] budget map={(cs.Round.HasValue ? MapPoolV1.KeyOf(cs.Round.Value.MapId) : "-")} gfx={Gfx} fpsMedian={1000f / Mathf.Max(0.1f, f50):F0} fps1pcLow={1000f / Mathf.Max(0.1f, f99):F0} drawCallsMedian={dc50:F0} drawCallsMax={dcMax:F0} trisMax={_trisMax} texMB={_texMb:F1} screen={Screen.width}x{Screen.height}");
            _dcFrame.Clear();
            _trisMax = 0;
            Debug.Log($"[{Tag}] render map={(cs.Round.HasValue ? MapPoolV1.KeyOf(cs.Round.Value.MapId) : "-")} drawCalls={(_drawCalls.Valid ? _drawCalls.LastValue : -1)} batches={(_batches.Valid ? _batches.LastValue : -1)} setPass={(_setPass.Valid ? _setPass.LastValue : -1)} tris={(_tris.Valid ? _tris.LastValue : -1)} visible={Rushlings()}");
            Debug.Log($"[{Tag}] frame p50={f50:F1}ms p95={f95:F1}ms fps={1000f / Mathf.Max(0.1f, f50):F0} ping p50={(PingService.Instance != null ? PingService.Instance.P50Ms : float.NaN):F0} p95={(PingService.Instance != null ? PingService.Instance.P95Ms : float.NaN):F0}ms lead={cs.Clock.LastReportedLead}/{cs.Clock.TargetLead} resyncs={cs.Clock.Resyncs}");
            _frameMs.Clear();
        }

        /// <summary>
        /// Remote-player smoothness: for one remote player that is running on the ground, the difference between the
        /// rendered speed this frame and the interpolated server velocity. 0 = perfectly smooth; spikes = stutter.
        /// </summary>
        private static int Rushlings()
        {
            var cs = ClientSim.Instance;
            return cs != null && cs.HasSnapshots ? cs.Newest.Count : 0;
        }

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
