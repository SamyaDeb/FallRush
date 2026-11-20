using System;
using FallRush.Shared;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// Server-side race round loop around <see cref="RaceRules"/> (M3b), owned by <see cref="ServerSim"/> on race maps.
    /// Waiting (start gate closed, free movement on the start deck) → as soon as a human has played for 5 s, or bots alone
    /// for 3 s → Intro → Countdown → Rush → Ended → results for 10 s → next round (seed + 1) → Intro.
    /// Domain events go out as reliable <c>evt</c> messages with a sequence number; <c>rstate</c> on connect and on
    /// every phase change; <c>round</c> (params + map hash) whenever the round's parameters change.
    /// </summary>
    public sealed class ServerRace
    {
        public const uint WaitingRoundStart = 0x7FFF0000u; // hazard time is far negative while waiting: the start gate stays closed
        public const int ResultsTicks = 300;

        private readonly ServerSim _sim;
        private readonly MapInfo _map;
        private readonly NetworkManager _nm;
        private readonly WaypointBot[] _bots = new WaypointBot[SimCore.MaxSlots];
        private readonly byte[] _buf = new byte[512];
        private uint _firstHumanInputTick;
        private uint _waitingSince;
        private uint _endedAt;
        private int _roundNo;
        private readonly RoundParams _baseParams;
        private readonly bool _fixedVariant, _fixedModifier;

        public RaceRules Rules { get; private set; }
        public RaceResult LastResult { get; private set; }
        public int RoundsCompleted { get; private set; }

        public ServerRace(ServerSim sim, MapInfo map, NetworkManager nm, RoundParams first, bool fixedVariant, bool fixedModifier)
        {
            _sim = sim;
            _map = map;
            _nm = nm;
            _baseParams = first;
            _fixedVariant = fixedVariant;
            _fixedModifier = fixedModifier;
            var def = map.definition;
            Rules = new RaceRules(map.CheckpointDefs(), map.CourseLineMm(), (uint)(def.timerCapSeconds * 30))
            {
                IntroTicks = Mathf.RoundToInt(def.introSeconds * 30f),
                CountdownTicks = Mathf.RoundToInt(def.countdownSeconds * 30f),
            };
            Rules.PhaseChanged += _ => BroadcastState();
            sim.Core.Rules = Rules;
            sim.Core.BotInput = BotInput;
        }

        private InputCmd BotInput(int slot, uint tick, MotorState s)
        {
            var b = _bots[slot];
            if (b == null || Rules.Phase == RoundPhase.Waiting)
                return new InputCmd { Tick = tick };
            return b.Next(tick, in s, Rules.Racers[slot].Checkpoint);
        }

        /// <summary>The params of round <paramref name="n"/> (1-based): the dev seed + n − 1; variant / modifier re-derived unless fixed by flags.</summary>
        private RoundParams ParamsFor(int n)
        {
            var seed = _baseParams.HazardSeed + (ulong)(n - 1);
            var p = RoundParams.FromDevSeed(_map.MapId, seed, _fixedVariant ? _baseParams.Variant : -1, _fixedModifier ? _baseParams.Modifier : -1);
            p.RoundIndex = 1; // M4: the match director numbers rounds
            return p;
        }

        public void OnHumanInput(uint tick)
        {
            if (_firstHumanInputTick == 0)
                _firstHumanInputTick = Math.Max(1, tick);
        }

        /// <summary>Before <see cref="SimCore.Step"/>: start rounds, loop after results.</summary>
        public void BeforeStep()
        {
            var core = _sim.Core;
            var tick = core.Tick;
            switch (Rules.Phase)
            {
                case RoundPhase.Waiting:
                {
                    if (_waitingSince == 0)
                        _waitingSince = Math.Max(1, tick);
                    var humansReady = core.HumanCount > 0 && _firstHumanInputTick != 0 && tick - _firstHumanInputTick >= 150;
                    var botsOnly = core.HumanCount == 0 && core.BotCount > 0 && tick - _waitingSince >= 90;
                    if (humansReady || botsOnly)
                        StartRound(ParamsFor(++_roundNo));
                    break;
                }
                case RoundPhase.Ended:
                    if (tick - _endedAt >= ResultsTicks)
                    {
                        if (core.HumanCount + core.BotCount > 0)
                            StartRound(ParamsFor(++_roundNo));
                    }

                    break;
            }
        }

        private void StartRound(RoundParams p)
        {
            var core = _sim.Core;
            var problems = _sim.ApplyRound(ref p, WaitingRoundStart);
            if (problems != null)
                return; // refused (allowlist): ServerSim logged it
            var rush = Rules.Begin(core, core.Tick);
            p.RoundStartTick = rush;
            p.QualifyCount = (byte)Rules.QualifyCount;
            var players = 0;
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (Rules.Racers[i].In)
                    players++;
            p.PlayerCount = (byte)players;
            p.IntroTicks = (ushort)Rules.IntroTicks;
            p.CountdownTicks = (ushort)Rules.CountdownTicks;
            p.TimerTicks = Rules.TimerTicks;
            core.Hazards.RoundStartTick = rush;
            _sim.SetRound(p);
            for (var i = 0; i < SimCore.MaxSlots; i++)
                _bots[i] = core.Slots[i].Active && core.Slots[i].Bot
                    ? new WaypointBot(i, _map.botRoutes, core.Hazards, core.Cfg, (uint)(p.HazardSeed * 2654435761UL) + (uint)i)
                    : null;
            Debug.Log($"[gs] race round {_roundNo} start: {p} players={players} (humans={core.HumanCount}, bots={core.BotCount}) rush@{rush}");
            _sim.BroadcastRound();
            BroadcastState();
        }

        /// <summary>After <see cref="SimCore.Step"/>: drain events, log the result.</summary>
        public void AfterStep()
        {
            if (Rules.Pending.Count > 0)
            {
                foreach (var e in Rules.Pending)
                    SendEvent(e);
                Rules.Pending.Clear();
            }

            if (Rules.Phase == RoundPhase.Ended && Rules.Result != null && !ReferenceEquals(LastResult, Rules.Result))
            {
                LastResult = Rules.Result;
                _endedAt = _sim.Core.Tick;
                RoundsCompleted++;
                var rejected = 0;
                for (var i = 0; i < SimCore.MaxSlots; i++)
                    rejected += Rules.Racers[i].RejectedCheckpoints;
                Debug.Log($"[gs] race round {_roundNo} result: {LastResult} rejectedCheckpoints={rejected}");
            }
        }

        public void SendEvent(in RaceEvent e)
        {
            var n = e.Encode(_buf);
            foreach (var id in _nm.ConnectedClientsIds)
            {
                if (id == NetworkManager.ServerClientId)
                    continue;
                using var w = new FastBufferWriter(n, Allocator.Temp);
                w.WriteBytesSafe(_buf, n);
                _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Event, id, w, NetworkDelivery.ReliableSequenced);
            }
        }

        public void SendState(ulong clientId)
        {
            var m = Rules.StateMessage();
            var n = m.Encode(_buf);
            using var w = new FastBufferWriter(n, Allocator.Temp);
            w.WriteBytesSafe(_buf, n);
            _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.RaceState, clientId, w, NetworkDelivery.ReliableSequenced);
        }

        public void BroadcastState()
        {
            foreach (var id in _nm.ConnectedClientsIds)
                if (id != NetworkManager.ServerClientId)
                    SendState(id);
        }
    }
}
