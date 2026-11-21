using System.Diagnostics;
using FallRush.Net;
using FallRush.Shared;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.LowLevel;
using Debug = UnityEngine.Debug;

namespace FallRush.Server
{
    /// <summary>
    /// Server-only health line every 10 s: measured NGO tick rate, frame interval p50/p95 and
    /// frame work time p50/p95 (PlayerLoop Initialization start → PostLateUpdate end, i.e. excluding the
    /// target-frame-rate wait). Allocation-free per frame; one log string per 10 s.
    /// </summary>
    public sealed class ServerStats : MonoBehaviour
    {
        private const float ReportSeconds = 10f;
        private const int MaxSamples = 1024; // > 10 s × 30 fps (with headroom)

        private static long s_FrameStart;
        private static ServerStats s_Instance;

        private readonly RollingSamples _intervalMs = new RollingSamples(MaxSamples);
        private readonly RollingSamples _workMs = new RollingSamples(MaxSamples);
        private NetworkManager _nm;
        private int _ticks;
        private float _windowStart;
        private float _maxWorkMs;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var boot = NetBootstrap.Instance;
            if (boot == null || !boot.IsServerRole)
                return;
            s_Instance = boot.gameObject.AddComponent<ServerStats>();
        }

        private void Start()
        {
            _nm = GetComponent<NetworkManager>();
            InsertPlayerLoopMarkers();
            _windowStart = Time.realtimeSinceStartup;
        }

        private void OnDisable()
        {
            if (_tickHooked && _nm != null && _nm.NetworkTickSystem != null)
                _nm.NetworkTickSystem.Tick -= OnTick;
            _tickHooked = false;
        }

        private bool _tickHooked;

        private void OnTick() => _ticks++;

        private void Update()
        {
            // NetworkTickSystem is created on StartServer; hook once it exists.
            if (!_tickHooked && _nm != null && _nm.NetworkTickSystem != null)
            {
                _nm.NetworkTickSystem.Tick += OnTick;
                _tickHooked = true;
            }

            _intervalMs.Add(Time.unscaledDeltaTime * 1000f);

            var now = Time.realtimeSinceStartup;
            var elapsed = now - _windowStart;
            if (elapsed < ReportSeconds)
                return;

            _intervalMs.Percentiles(0.5f, 0.95f, out var dt50, out var dt95);
            _workMs.Percentiles(0.5f, 0.95f, out var w50, out var w95);
            var tickHz = _ticks / elapsed;
            var clients = NetBootstrap.Instance != null ? NetBootstrap.Instance.ServerConnectedCount : 0;

            Debug.Log($"[gs] stats tickHz={tickHz:F1} frameDt p50={dt50:F2}ms p95={dt95:F2}ms work p50={w50:F3}ms p95={w95:F3}ms max={_maxWorkMs:F3}ms clients={clients} frames={_intervalMs.Count}");
            LogSim(elapsed);

            _ticks = 0;
            _maxWorkMs = 0f;
            _intervalMs.Clear();
            _workMs.Clear();
            _windowStart = now;
        }

        // ---- M2 sim line: occupants, sim step time, snapshot/input traffic, input validation counters ----
        private long _snaps, _snapBytes, _inMsgs, _inBytes;
        private InputCounters _inputs;

        private void LogSim(float elapsed)
        {
            var sim = ServerSim.Instance;
            if (sim == null || sim.Core == null)
                return;
            var core = sim.Core;
            var totals = sim.Retired;
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!core.Slots[i].Active || core.Slots[i].Bot)
                    continue;
                var c = core.Input(i).Counters;
                totals.Messages += c.Messages;
                totals.Accepted += c.Accepted;
                totals.Redundant += c.Redundant;
                totals.Late += c.Late;
                totals.FarFuture += c.FarFuture;
                totals.Flood += c.Flood;
                totals.Burst += c.Burst;
                totals.Malformed += c.Malformed;
                totals.Repeated += c.Repeated;
                totals.Neutral += c.Neutral;
                totals.Early += c.Early;
                totals.Backlog += c.Backlog;
            }

            sim.StepMs.Percentiles(0.5f, 0.95f, out var s50, out var s95);
            var where = new System.Text.StringBuilder();
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (core.Slots[i].Active)
                    where.Append($"{core.Slots[i].State.PosX / 1000},{core.Slots[i].State.PosZ / 1000} ");
            var humans = core.HumanCount;
            var perClientBps = humans > 0 ? (sim.SnapBytesSent - _snapBytes) / elapsed / humans : 0f;
            Debug.Log($"[gs] sim tick={core.Tick} humans={humans} bots={core.BotCount} step p50={s50:F3}ms p95={s95:F3}ms " +
                      $"snapHz={30 / sim.SnapshotEveryTicks} snaps={sim.SnapshotsSent - _snaps} snapPayload/client={perClientBps:F0}B/s " +
                      $"inMsgs={sim.InputMessages - _inMsgs} inPayload={(sim.InputBytes - _inBytes) / elapsed:F0}B/s " +
                      $"accepted={totals.Accepted - _inputs.Accepted} late={totals.Late - _inputs.Late} repeated={totals.Repeated - _inputs.Repeated} neutral={totals.Neutral - _inputs.Neutral} " +
                      $"burst={totals.Burst - _inputs.Burst} early={totals.Early - _inputs.Early} backlog={totals.Backlog - _inputs.Backlog} violations(far/flood/malformed)={totals.FarFuture - _inputs.FarFuture}/{totals.Flood - _inputs.Flood}/{totals.Malformed - _inputs.Malformed} kicks={sim.Kicks} snapsPausedIdleTotal={sim.SnapshotsPausedIdle} pushesTotal={core.PushEvents} bumpsTotal={core.BumpEvents} bumpFlag={(core.Cfg.BumpEnabled ? 1 : 0)} hazardHitsTotal={core.HazardHits} launchesTotal={core.Hazards.Launches} doorPushesTotal={core.Hazards.DoorPushes} mapHash={MapContent.Short(sim.MapHash)} snapBytes12={SnapHeader.Size + 12 * PlayerState.Size} at={where}");
            _snaps = sim.SnapshotsSent;
            _snapBytes = sim.SnapBytesSent;
            _inMsgs = sim.InputMessages;
            _inBytes = sim.InputBytes;
            _inputs = totals;
            sim.StepMs.Clear();
        }

        private void RecordWork(float ms)
        {
            _workMs.Add(ms);
            if (ms > _maxWorkMs)
                _maxWorkMs = ms;
        }

        private struct FrameBeginMarker { }

        private struct FrameEndMarker { }

        private static void InsertPlayerLoopMarkers()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var systems = root.subSystemList;
            for (var i = 0; i < systems.Length; i++)
            {
                if (systems[i].type == typeof(UnityEngine.PlayerLoop.Initialization))
                    systems[i].subSystemList = Insert(systems[i].subSystemList, 0, new PlayerLoopSystem
                    {
                        type = typeof(FrameBeginMarker),
                        updateDelegate = () => s_FrameStart = Stopwatch.GetTimestamp(),
                    });
                else if (systems[i].type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                    systems[i].subSystemList = Insert(systems[i].subSystemList, systems[i].subSystemList.Length, new PlayerLoopSystem
                    {
                        type = typeof(FrameEndMarker),
                        updateDelegate = OnFrameEnd,
                    });
            }

            root.subSystemList = systems;
            PlayerLoop.SetPlayerLoop(root);
        }

        private static void OnFrameEnd()
        {
            if (s_FrameStart == 0 || s_Instance == null)
                return;
            var ms = (Stopwatch.GetTimestamp() - s_FrameStart) * 1000.0 / Stopwatch.Frequency;
            s_Instance.RecordWork((float)ms);
        }

        private static PlayerLoopSystem[] Insert(PlayerLoopSystem[] list, int index, PlayerLoopSystem item)
        {
            list ??= new PlayerLoopSystem[0];
            var result = new PlayerLoopSystem[list.Length + 1];
            for (int i = 0, j = 0; i < result.Length; i++)
                result[i] = i == index ? item : list[j++];
            return result;
        }
    }
}
