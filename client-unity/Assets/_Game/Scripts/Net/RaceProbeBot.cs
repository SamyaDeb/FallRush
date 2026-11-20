using FallRush.Shared;

namespace FallRush.Net
{
    /// <summary>
    /// MEASUREMENT TOOL: plays the race through the real client path with the same <see cref="WaypointBot"/> the server bots
    /// use, steering from the <b>predicted</b> local state, the hazard poses at the predicted tick and the checkpoint count
    /// from our snapshot race byte: what a human sees. Used by the probe (<c>-fallrushProbeScript race</c>) and WebGL
    /// (<c>?bot=race</c>) for the M3 gate.
    /// </summary>
    public sealed class RaceProbeBot : IInputSource
    {
        private readonly ClientSim _sim;
        private WaypointBot _bot;
        private ClientPrediction _for;
        private uint _roundStart;
        private readonly uint _seed;

        public RaceProbeBot(ClientSim sim, uint seed)
        {
            _sim = sim;
            _seed = seed;
        }

        public WaypointBot Bot => _bot;
        public int Laps;

        public void Sample(ref InputCmd cmd)
        {
            var pred = _sim.Prediction;
            var map = MapInfo.Current;
            if (pred == null || !pred.HasState || map == null || _sim.OwnOutOfSim)
                return;
            var start = _sim.Round.HasValue ? _sim.Round.Value.RoundStartTick : 0u;
            if (_bot == null || !ReferenceEquals(_for, pred) || start != _roundStart)
            {
                if (_bot != null && start != _roundStart)
                    Laps++;
                _for = pred;
                _roundStart = start;
                _bot = new WaypointBot(_sim.OwnSlot == SnapHeader.NoSlot ? 0 : _sim.OwnSlot, map.botRoutes, pred.Hazards, pred.Cfg, _seed);
            }

            var c = _bot.Next(cmd.Tick, pred.State, _sim.OwnRace & PlayerState.RaceCheckpointMask);
            cmd.MoveX = c.MoveX;
            cmd.MoveY = c.MoveY;
            cmd.Buttons = c.Buttons;
            cmd.CamYaw = c.CamYaw;
        }
    }
}
