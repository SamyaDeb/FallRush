using System;
using FallRush.Shared;

namespace FallRush.Net
{
    /// <summary>
    /// The server's bots for one round: one brain per bot slot on the loaded map, built when the round begins, fed the same
    /// information a player has (own state, pure hazard poses, replicated reactive state). Labelled BOT everywhere.
    /// </summary>
    public sealed class BotBrainSet
    {
        private readonly WaypointBot[] _wp = new WaypointBot[SimCore.MaxSlots];
        private readonly IRoundRules _rules;
        private readonly RaceRules _race;

        public BotBrainSet(MapInfo map, SimCore core, IRoundRules rules, RosterSlot[] roster, in RoundParams p)
        {
            _rules = rules;
            _race = rules as RaceRules;
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!core.Slots[i].Active || !core.Slots[i].Bot)
                    continue;
                if (_race != null)
                    _wp[i] = new WaypointBot(i, map.botRoutes, core.Hazards, core.Cfg, (uint)(p.HazardSeed * 2654435761UL) + (uint)i);
            }
        }

        public InputCmd Input(int slot, uint tick, MotorState s)
        {
            var b = _wp[slot];
            if (b == null || _rules.Phase == RoundPhase.Waiting)
                return new InputCmd { Tick = tick };
            return b.Next(tick, in s, _race != null ? _race.Racers[slot].Checkpoint : 0);
        }

        public void AfterStep(SimCore core)
        {
        }
    }
}
