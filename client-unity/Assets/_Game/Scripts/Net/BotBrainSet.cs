using System.Collections.Generic;
using FallRush.Shared;

namespace FallRush.Net
{
    /// <summary>
    /// The server's bots for one round: one <see cref="BotBrain"/> per bot slot on the loaded map, built when the round
    /// begins, with the tier the roster gave it (from the match seed). Labelled BOT everywhere.
    /// </summary>
    public sealed class BotBrainSet
    {
        private readonly BotBrain[] _bots = new BotBrain[SimCore.MaxSlots];
        private readonly IRoundRules _rules;

        public BotBrainSet(MapInfo map, SimCore core, IRoundRules rules, RosterSlot[] roster, in RoundParams p, BotTier? forceTier = null)
        {
            _rules = rules;
            var routes = UsableRoutes(map != null ? map.botRoutes : null, core.Hazards);
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!core.Slots[i].Active || !core.Slots[i].Bot)
                    continue;
                var tier = forceTier ?? (roster != null && roster[i].Occupied ? roster[i].Tier : BotTier.Normal);
                if (tier == BotTier.Reference && forceTier == null)
                    tier = BotTier.Normal;
                _bots[i] = new BotBrain(i, tier, (uint)(p.HazardSeed * 2654435761UL) + (uint)i * 7919u, rules, core, routes);
            }
        }

        public BotBrain this[int slot] => _bots[slot];

        /// <summary>G4.4 QA: put a different brain in a slot (the camper probe's reference bot among normal ones).</summary>
        public void Replace(int slot, BotBrain brain) => _bots[slot] = brain;

        /// <summary>Routes that pass a gate the variant keeps (practically) closed are left out (bots see that door never opens).</summary>
        public static BotRoute[] UsableRoutes(BotRoute[] routes, HazardSet hz)
        {
            if (routes == null)
                return System.Array.Empty<BotRoute>();
            var r = new List<BotRoute>();
            foreach (var route in routes)
            {
                var ok = true;
                if (route.Points != null)
                    foreach (var w in route.Points)
                        if (w.Action == WaypointAction.Door && (uint)w.Arg < (uint)hz.Doors.Length)
                        {
                            ref readonly var d = ref hz.Doors[w.Arg];
                            if (d.Mode == 0 && d.OpenTicks < 30)
                                ok = false;
                        }

                if (ok)
                    r.Add(route);
            }

            return r.ToArray();
        }

        public InputCmd Input(int slot, uint tick, MotorState s)
        {
            var b = _bots[slot];
            if (b == null || _rules.Phase == RoundPhase.Waiting)
                return new InputCmd { Tick = tick };
            return b.Next(tick, in s);
        }

        public void AfterStep(SimCore core)
        {
        }
    }
}
