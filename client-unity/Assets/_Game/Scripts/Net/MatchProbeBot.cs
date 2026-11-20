using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// MEASUREMENT TOOL (M4): plays every round of a match through the real client path with a <see cref="BotBrain"/>
    /// steering from the <b>predicted</b> local state, the pure hazard poses at the predicted tick, the replicated reactive
    /// state and the other Rushlings from the snapshots (what a human sees). Used by the probe
    /// (<c>-fallrushProbeScript match</c>) and WebGL (<c>?bot=match</c>) for the M4 real-client runs.
    /// </summary>
    public sealed class MatchProbeBot : IInputSource
    {
        private readonly ClientSim _cs;
        private readonly BotTier _tier;
        private readonly uint _seed;
        private BotBrain _brain;
        private ClientPrediction _for;
        private uint _roundStart;
        public int Rounds;

        public MatchProbeBot(ClientSim cs, uint seed, BotTier tier = BotTier.Normal)
        {
            _cs = cs;
            _seed = seed;
            _tier = tier;
        }

        public BotBrain Brain => _brain;

        public void Sample(ref InputCmd cmd)
        {
            var pred = _cs.Prediction;
            var map = MapInfo.Current;
            if (pred == null || !pred.HasState || map == null || map.definition == null || _cs.OwnOutOfSim)
                return;
            var start = _cs.Round.HasValue ? _cs.Round.Value.RoundStartTick : 0u;
            if (_brain == null || !ReferenceEquals(_for, pred) || start != _roundStart)
            {
                _for = pred;
                _roundStart = start;
                Rounds++;
                var slot = _cs.OwnSlot == SnapHeader.NoSlot ? 0 : _cs.OwnSlot;
                var routes = BotBrainSet.UsableRoutes(map.botRoutes, pred.Hazards);
                _brain = new BotBrain(slot, _tier, _seed + (uint)Rounds * 977u, map.definition.mode, pred.Hazards, pred.Cfg, routes,
                    () => _cs.OwnRace & PlayerState.RaceCheckpointMask, Others);
            }

            var c = _brain.Next(cmd.Tick, pred.State);
            cmd.MoveX = c.MoveX;
            cmd.MoveY = c.MoveY;
            cmd.Buttons = c.Buttons;
            cmd.CamYaw = c.CamYaw;
        }

        private bool Others(int slot, out MotorState st)
        {
            st = default;
            if (!_cs.HasSnapshots || slot == _cs.OwnSlot)
                return false;
            ref readonly var n = ref _cs.Newest;
            for (var i = 0; i < n.Count; i++)
            {
                ref readonly var p = ref n.Players[i];
                if (p.Slot != slot || (p.Flags & MotorState.Respawning) != 0 || (p.Race & PlayerState.RaceFinished) != 0)
                    continue;
                st = new MotorState { PosX = p.PosX, PosY = p.PosY, PosZ = p.PosZ, VelX = p.VelX, VelY = p.VelY, VelZ = p.VelZ, Flags = p.Flags };
                return true;
            }

            return false;
        }
    }
}
