using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>
    /// The match bots (M4 §3.5, doc 04 §9): inputs through the same sanitiser and motor as humans, labelled BOT everywhere.
    /// They know only what a player knows: their own state, the pure hazard poses, the replicated reactive state (tiles,
    /// stars) and where the other Rushlings are.
    /// <list type="bullet">
    /// <item><b>Race / Survival:</b> the M3 <see cref="WaypointBot"/> on the map's routes (race sections by checkpoint;
    /// survival sections by height on the tower's spiral), wrapped in the tier's noise.</item>
    /// <item><b>Skill:</b> chases the best reachable star (value over distance; dropped stars too), uses a spring pad for an
    /// island's golden star, steers clear of bumpers and the rim.</item>
    /// <item><b>Final:</b> keeps moving onto untouched tiles away from other players, drops to the shelf below when its
    /// shelf is nearly gone, jumps / dives the spinner bars.</item>
    /// </list>
    /// <b>Tiers</b> (from the match seed): reaction delay on steering, a slow steering wobble, late jump presses (imperfect
    /// timing at gaps), a run-speed scale, and a chance to hesitate after waiting at doors / bars / lifts. Reference = none
    /// (QA). Deterministic for a seed.
    /// </summary>
    public sealed class BotBrain
    {
        public struct Skill
        {
            public int ReactionTicks;  // steering follows the plan this many ticks late
            public int WobbleDeg;      // amplitude of a slow steering wobble
            public int LateJumpTicks;  // a jump press is late by 0…this many ticks
            public int SpeedPermille;  // stick magnitude (1000 = full)
            public int HesitatePct;    // after a wait (door, bar, lift) hold still a bit longer, this often
            public int HesitateTicks;
            public int StarPickRange;  // skill: how far (m) the bot looks for stars
        }

        public static Skill For(BotTier t) => t switch
        {
            BotTier.Easy => new Skill { ReactionTicks = 8, WobbleDeg = 22, LateJumpTicks = 3, SpeedPermille = 820, HesitatePct = 35, HesitateTicks = 40, StarPickRange = 12 },
            BotTier.Normal => new Skill { ReactionTicks = 5, WobbleDeg = 12, LateJumpTicks = 2, SpeedPermille = 900, HesitatePct = 18, HesitateTicks = 25, StarPickRange = 18 },
            BotTier.Good => new Skill { ReactionTicks = 2, WobbleDeg = 5, LateJumpTicks = 1, SpeedPermille = 970, HesitatePct = 6, HesitateTicks = 12, StarPickRange = 30 },
            _ => new Skill { SpeedPermille = 1000, StarPickRange = 40 },
        };

        public readonly int Slot;
        public readonly BotTier Tier;
        public readonly Skill S;
        private readonly string _mode;
        private readonly HazardSet _hz;
        private readonly MovementConfig _cfg;
        private readonly WaypointBot _wp;
        /// <summary>Where another Rushling is (false = not in the round): a player sees this in the snapshots.</summary>
        public delegate bool OthersFn(int slot, out MotorState state);

        private readonly Func<int> _checkpoints;
        private readonly OthersFn _others;
        private readonly float[] _sectionY;
        private uint _rng;
        private readonly double _wobblePhase;
        private readonly (sbyte x, sbyte y)[] _moveLine = new (sbyte, sbyte)[16];
        private uint _pendingJumpAt;
        private bool _pendingJump;
        private uint _hesitateUntil;
        private bool _wasWaiting;
        private uint _seq;
        private int _target = -1;
        private uint _targetSince;
        private uint _jumpHeldUntil;
        private uint _diveCooldownUntil;
        private int _groundY = int.MinValue;
        private int _blockedTicks;

        // stats
        public int Jumps, Hesitations, Retargets;

        /// <summary>Server bot: rules + the sim it runs in.</summary>
        public BotBrain(int slot, BotTier tier, uint seed, IRoundRules rules, SimCore sim, BotRoute[] routes)
            : this(slot, tier, seed, rules.Mode, sim.Hazards, sim.Cfg, routes,
                () => rules is RaceRules race ? race.Racers[slot].Checkpoint : 0,
                (int i, out MotorState st) =>
                {
                    st = sim.Slots[i].State;
                    return sim.Slots[i].Active && rules.InRound(i);
                })
        {
        }

        /// <summary>Any driver (the client probe): mode, hazards, motor config, routes, our checkpoint count, the others.</summary>
        public BotBrain(int slot, BotTier tier, uint seed, string mode, HazardSet hz, MovementConfig cfg, BotRoute[] routes, Func<int> checkpoints, OthersFn others)
        {
            Slot = slot;
            Tier = tier;
            S = For(tier);
            _mode = mode;
            _checkpoints = checkpoints;
            _others = others;
            _hz = hz;
            _cfg = cfg;
            _rng = seed * 0x9E3779B1u + (uint)slot * 0x85EBCA6Bu + 7u;
            if (_rng == 0)
                _rng = 1;
            _wobblePhase = Next() % 6283 / 1000.0;
            if (_mode == "Race" || _mode == "Survival")
                _wp = new WaypointBot(slot, routes, _hz, _cfg, seed + (uint)slot * 7u);
            if (_mode == "Survival")
                _sectionY = SectionHeights(routes);
        }

        private uint Next()
        {
            _rng ^= _rng << 13;
            _rng ^= _rng >> 17;
            _rng ^= _rng << 5;
            return _rng;
        }

        public WaypointBot Waypoints => _wp;

        /// <summary>Survival sections from the routes: section k starts at its routes' lowest first waypoint.</summary>
        private static float[] SectionHeights(BotRoute[] routes)
        {
            var max = -1;
            foreach (var r in routes)
                max = Math.Max(max, r.Section);
            var y = new float[max + 1];
            for (var i = 0; i <= max; i++)
                y[i] = float.MaxValue;
            foreach (var r in routes)
                if (r.Points != null && r.Points.Length > 0)
                    y[r.Section] = Math.Min(y[r.Section], r.Points[0].YMm * 0.001f);
            return y;
        }

        /// <summary>The survival section (spiral side) for feet height y: the last section whose start is at or below y + 0.3 m.</summary>
        public int SurvivalSection(int yMm)
        {
            var y = yMm * 0.001f + 0.3f;
            var k = 0;
            for (var i = 0; i < _sectionY.Length; i++)
                if (_sectionY[i] <= y)
                    k = i;
            return k;
        }

        public InputCmd Next(uint tick, in MotorState s)
        {
            InputCmd plan;
            switch (_mode)
            {
                case "Race":
                    plan = _wp.Next(tick, in s, _checkpoints != null ? _checkpoints() : 0);
                    break;
                case "Survival":
                    // Section by the height of the last ground contact (a jump must not switch to the next side's route).
                    if (s.Has(MotorState.Grounded) || _groundY == int.MinValue)
                        _groundY = s.PosY;
                    plan = _wp.Next(tick, in s, SurvivalSection(_groundY));
                    break;
                case "Skill":
                    plan = SkillPlan(tick, in s);
                    break;
                case "Final":
                    plan = FinalPlan(tick, in s);
                    break;
                default:
                    plan = new InputCmd { Tick = tick };
                    break;
            }

            Unstick(in s, ref plan);
            return Noise(tick, in s, plan);
        }

        /// <summary>Pushing a stick but not moving while grounded for 2/3 s (a ledge lip, a wall): hop.</summary>
        private void Unstick(in MotorState s, ref InputCmd plan)
        {
            var pushing = plan.MoveX != 0 || plan.MoveY != 0;
            var still = Math.Abs(s.VelX) + Math.Abs(s.VelZ) < 60;
            if (pushing && still && s.Has(MotorState.Grounded) && s.StunTicks == 0)
                _blockedTicks++;
            else
                _blockedTicks = 0;
            if (_blockedTicks < 20)
                return;
            _blockedTicks = 0;
            plan.Buttons |= InputCmd.JumpPressed | InputCmd.JumpHeld;
        }

        // ------------------------------------------------------------------ noise (tiers)

        private InputCmd Noise(uint tick, in MotorState s, InputCmd plan)
        {
            if (Tier == BotTier.Reference)
            {
                plan.Seq = ++_seq;
                plan.Tick = tick;
                return plan;
            }

            var cmd = new InputCmd { Seq = ++_seq, Tick = tick, CamYaw = plan.CamYaw };
            // Hesitation: after a wait at a door, a bar or a lift ends, sometimes hold a little longer (race / survival only:
            // in the final a held tile is about to drop).
            var waitAction = _wp != null && (_wp.CurrentAction == WaypointAction.Door || _wp.CurrentAction == WaypointAction.WaitClear || _wp.CurrentAction == WaypointAction.JumpToPlatform);
            var waiting = plan.MoveX == 0 && plan.MoveY == 0 && waitAction;
            if (_wasWaiting && !waiting && Next() % 100 < (uint)S.HesitatePct)
            {
                _hesitateUntil = tick + (uint)(S.HesitateTicks / 2 + Next() % (uint)Math.Max(1, S.HesitateTicks));
                Hesitations++;
            }

            _wasWaiting = waiting;
            if (tick < _hesitateUntil)
                return cmd;

            // Precise moves (gap jumps, landings, platforms, bars, standing on a moving surface): no wobble or reaction delay,
            // at most a 1-tick late jump. Elsewhere the full tier noise. (QA: noise on every jump made normal bots fall ~15×
            // per race on Block Party Dash.)
            var precise = (_wp != null && _wp.Precise) || s.Surface != 0 || !s.Has(MotorState.Grounded) || _mode == "Final";
            _moveLine[tick % (uint)_moveLine.Length] = (plan.MoveX, plan.MoveY);
            var reaction = precise ? 0 : S.ReactionTicks;
            var delayed = reaction > 0 ? _moveLine[(tick + (uint)_moveLine.Length - (uint)Math.Min(reaction, _moveLine.Length - 1)) % (uint)_moveLine.Length] : (plan.MoveX, plan.MoveY);
            // Steer toward the plan while it is fresh in either axis (a delayed zero would stall at the start).
            // Stopping is immediate (a wait point); only steering lags.
            var stop = plan.MoveX == 0 && plan.MoveY == 0;
            float mx = stop ? 0 : delayed.Item1 == 0 && delayed.Item2 == 0 ? plan.MoveX : delayed.Item1;
            float my = stop ? 0 : delayed.Item1 == 0 && delayed.Item2 == 0 ? plan.MoveY : delayed.Item2;
            if (mx != 0 || my != 0)
            {
                var wob = (precise ? 0 : S.WobbleDeg) * Math.PI / 180.0 * Math.Sin(tick * 0.07 + _wobblePhase);
                double c = Math.Cos(wob), sn = Math.Sin(wob);
                var rx = mx * c - my * sn;
                var ry = mx * sn + my * c;
                var k = precise ? 1.0 : S.SpeedPermille / 1000.0; // full speed for gap jumps (the jump points assume it)
                var l = Math.Sqrt(rx * rx + ry * ry);
                if (l > 127)
                    k *= 127 / l;
                cmd.MoveX = (sbyte)Math.Round(rx * k);
                cmd.MoveY = (sbyte)Math.Round(ry * k);
            }

            // Late jump presses (imperfect timing).
            if ((plan.Buttons & InputCmd.JumpPressed) != 0 && !_pendingJump)
            {
                _pendingJump = true;
                var late = precise ? Math.Min(1, S.LateJumpTicks) : S.LateJumpTicks;
                _pendingJumpAt = tick + (late > 0 ? Next() % (uint)(late + 1) : 0u);
            }

            if (_pendingJump && tick >= _pendingJumpAt)
            {
                _pendingJump = false;
                cmd.Buttons |= InputCmd.JumpPressed | InputCmd.JumpHeld;
                _jumpHeldUntil = tick + 8;
                Jumps++;
            }
            else if (tick < _jumpHeldUntil || (plan.Buttons & InputCmd.JumpHeld) != 0 && !_pendingJump)
            {
                cmd.Buttons |= InputCmd.JumpHeld;
            }

            if ((plan.Buttons & InputCmd.DivePressed) != 0)
                cmd.Buttons |= InputCmd.DivePressed | InputCmd.DiveHeld;
            InputSanitizer.Sanitize(ref cmd);
            return cmd;
        }

        // ------------------------------------------------------------------ steering helpers

        private static InputCmd SteerTo(uint tick, in MotorState s, int tx, int tz)
        {
            var cmd = new InputCmd { Tick = tick };
            long dx = tx - s.PosX, dz = tz - s.PosZ;
            var l = Math.Sqrt((double)dx * dx + (double)dz * dz);
            if (l > 60)
            {
                cmd.MoveX = (sbyte)Math.Round(dx / l * 127);
                cmd.MoveY = (sbyte)Math.Round(dz / l * 127);
                cmd.CamYaw = (ushort)(Yaw256.FromVector((int)dx, (int)dz, s.Yaw) << 8);
            }

            return cmd;
        }

        /// <summary>
        /// Bumpers knock a star out of you: steer around any bumper whose disc (+1.2 m) lies on the way ahead (sideways
        /// detour), and push away from one closer than 2.2 m.
        /// </summary>
        private void AvoidBumpers(in MotorState s, ref InputCmd cmd)
        {
            float ax = cmd.MoveX, az = cmd.MoveY;
            var ml = Math.Sqrt(ax * ax + az * az);
            foreach (var b in _hz.Bumpers)
            {
                double bx = (b.CenterXMm - s.PosX) * 0.001, bz = (b.CenterZMm - s.PosZ) * 0.001;
                var d = Math.Sqrt(bx * bx + bz * bz);
                var r = b.RadiusMm * 0.001;
                if (ml > 1 && d < 6)
                {
                    double ux = ax / ml, uz = az / ml;
                    var along = bx * ux + bz * uz;            // ahead of us?
                    var side = bx * uz - bz * ux;              // which side of our line
                    if (along > 0 && Math.Abs(side) < r + 1.2)
                    {
                        var push = (r + 1.2 - Math.Abs(side)) / (r + 1.2) * 160.0 * (side >= 0 ? -1 : 1);
                        ax += (float)(uz * push);
                        az += (float)(-ux * push);
                    }
                }

                var reach = r + 2.2;
                if (d >= reach || d < 1e-3)
                    continue;
                var w = (reach - d) / reach * 260.0;
                ax += (float)(-bx / d * w);
                az += (float)(-bz / d * w);
            }

            var l = Math.Sqrt(ax * ax + az * az);
            if (l < 1)
                return;
            cmd.MoveX = (sbyte)Math.Round(ax / l * 127);
            cmd.MoveY = (sbyte)Math.Round(az / l * 127);
        }

        /// <summary>Jump a low spinner bar / dive under a high one if it hits us within 3–6 ticks (what a player sees).</summary>
        private void ReactToBars(uint tick, in MotorState s, ref InputCmd cmd)
        {
            if (_hz.Sweepers.Length == 0 || s.ImmunityTicks > 0 || !s.Has(MotorState.Grounded) || _hz.Time(tick) + 6 < _hz.SweeperStartT)
                return;
            var r = MotorState.QuantizeMm(_cfg.Radius);
            var h = MotorState.QuantizeMm(_cfg.Height);
            for (var k = 3; k <= 6; k++)
            {
                var x = s.PosX + s.VelX * 10 * k / 30;
                var z = s.PosZ + s.VelZ * 10 * k / 30;
                var t = _hz.Time(tick + (uint)k);
                if (t < _hz.SweeperStartT)
                    continue;
                foreach (var d in _hz.Sweepers)
                {
                    if (!HazardMath.SweeperContactMm(in d, t, x, s.PosY, z, r, h, out _, out _))
                        continue;
                    var bottom = d.PivotYMm + d.BarHeightMm - d.BarRadiusMm - s.PosY;
                    if (bottom >= 650)
                    {
                        if (k >= 4 && s.DiveCooldownTicks == 0 && tick >= _diveCooldownUntil)
                        {
                            cmd.Buttons |= InputCmd.DivePressed | InputCmd.DiveHeld;
                            _diveCooldownUntil = tick + 40;
                        }
                    }
                    else if (k <= 5)
                    {
                        cmd.Buttons |= InputCmd.JumpPressed | InputCmd.JumpHeld;
                    }

                    return;
                }
            }
        }

        // ------------------------------------------------------------------ skill: stars

        private readonly List<int> _live = new List<int>();

        private InputCmd SkillPlan(uint tick, in MotorState s)
        {
            if (_hz.StarFields.Length == 0 || _hz.Reactive == null)
                return new InputCmd { Tick = tick };
            var f = _hz.StarFields[0];
            var t = _hz.Time(tick);
            _live.Clear();
            SkillRules.LiveStars(_hz, t, tick, _live);
            // Dropped stars count as targets (id StarDropBase + index).
            for (var d = 0; d < _hz.Reactive.Drops.Count; d++)
                if (_hz.Reactive.DropAvailable(d, tick, out _) || tick - _hz.Reactive.Drops[d].Tick < (uint)f.DropPopTicks)
                    _live.Add(HazardMath.StarDropBase + d);

            var best = -1;
            var bestScore = double.MinValue;
            int bx = 0, by = 0, bz = 0;
            var range = S.StarPickRange * 1000.0;
            foreach (var id in _live)
            {
                int x, y, z, value;
                if (id >= HazardMath.StarDropBase)
                {
                    var e = _hz.Reactive.Drops[id - HazardMath.StarDropBase];
                    HazardMath.DropPosMm(in f, e.Xcm * 10, e.Ycm * 10, e.Zcm * 10, (uint)(id - HazardMath.StarDropBase + 1), f.DropPopTicks, out x, out y, out z);
                    value = 1;
                }
                else
                {
                    HazardMath.StarSlot(in f, _hz.Seed, id, out var a, out _, out _, out value);
                    HazardMath.StarAnchorMm(in f, _hz, a, t, out x, out y, out z);
                }

                long dx = x - s.PosX, dz = z - s.PosZ;
                var dist = Math.Sqrt((double)dx * dx + (double)dz * dz);
                if (dist > range)
                    continue;
                var up = y - s.PosY;
                if (up > 1800 && Tier != BotTier.Good && Tier != BotTier.Reference)
                    continue; // islands: only the good bots go for them
                var score = value * 4000.0 / (dist + 3000.0) - (id == _target ? -0.15 : 0); // a little stickiness
                // Others closer to it make it less attractive.
                for (var i = 0; i < SimCore.MaxSlots; i++)
                {
                    if (i == Slot || _others == null || !_others(i, out var o))
                        continue;
                    long ox = x - o.PosX, oz = z - o.PosZ;
                    if (ox * ox + oz * oz < dx * dx + dz * dz)
                        score -= 0.25;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = id;
                    bx = x;
                    by = y;
                    bz = z;
                }
            }

            if (best != _target)
            {
                _target = best;
                _targetSince = tick;
                Retargets++;
            }

            InputCmd cmd;
            if (best < 0)
            {
                // Nothing in reach: drift toward the middle (where the next wave will be).
                cmd = SteerTo(tick, in s, 0, 0);
            }
            else if (by - s.PosY > 1800)
            {
                // A golden star on an island: the nearest spring pad that points roughly at it, else jump from the rim.
                cmd = SteerToIsland(tick, in s, bx, bz);
            }
            else
            {
                cmd = SteerTo(tick, in s, bx, bz);
            }

            // Stay off the rim (Ø 36 m arena) unless heading for an island.
            var rr = Math.Sqrt((double)s.PosX * s.PosX + (double)s.PosZ * s.PosZ);
            if (rr > 16800 && (best < 0 || by - s.PosY <= 1800))
                cmd = SteerTo(tick, in s, 0, 0);
            AvoidBumpers(in s, ref cmd);
            if (s.Has(MotorState.Grounded) && tick % 47 == (uint)Slot)
                cmd.Buttons |= InputCmd.JumpPressed | InputCmd.JumpHeld; // a hop now and then (players do)
            return cmd;
        }

        private InputCmd SteerToIsland(uint tick, in MotorState s, int ix, int iz)
        {
            var best = -1;
            var bestD = double.MaxValue;
            for (var i = 0; i < _hz.Pads.Length; i++)
            {
                ref readonly var p = ref _hz.Pads[i];
                // Pad launch direction vs the island direction.
                double lx = p.LaunchXCms, lz = p.LaunchZCms;
                double tx = ix - p.CenterXMm, tz = iz - p.CenterZMm;
                var ll = Math.Sqrt(lx * lx + lz * lz) * Math.Sqrt(tx * tx + tz * tz);
                if (ll < 1 || (lx * tx + lz * tz) / ll < 0.8)
                    continue;
                double dx = p.CenterXMm - s.PosX, dz = p.CenterZMm - s.PosZ;
                var d = dx * dx + dz * dz;
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            if (best >= 0)
                return SteerTo(tick, in s, _hz.Pads[best].CenterXMm, _hz.Pads[best].CenterZMm);
            // No pad: run at it and jump at the rim.
            var cmd = SteerTo(tick, in s, ix, iz);
            var rr = Math.Sqrt((double)s.PosX * s.PosX + (double)s.PosZ * s.PosZ);
            if (rr > 17300 && s.Has(MotorState.Grounded))
                cmd.Buttons |= InputCmd.JumpPressed | InputCmd.JumpHeld;
            return cmd;
        }

        // ------------------------------------------------------------------ final: tiles

        private InputCmd FinalPlan(uint tick, in MotorState s)
        {
            if (_hz.Grids.Length == 0 || _hz.Reactive == null)
                return new InputCmd { Tick = tick };
            var g = _hz.Grids[0];
            var rx = _hz.Reactive;
            // Current shelf: the highest shelf top at or below the feet.
            var shelf = g.ShelfTopsMm.Length - 1;
            for (var i = 0; i < g.ShelfTopsMm.Length; i++)
                if (s.PosY >= g.ShelfTopsMm[i] - 300)
                {
                    shelf = i;
                    break;
                }

            var per = g.PerShelf;
            var intact = 0;
            for (var i = shelf * per; i < (shelf + 1) * per; i++)
                if (rx.TileContactAt(i, tick) == 0)
                    intact++;

            // Frugal (a careful player): stay on the tile under you until it is about to drop, then step to a fresh one.
            var under = g.TileUnder(s.PosX, s.PosY, s.PosZ);
            if (s.Has(MotorState.Grounded) && under >= 0 && _target >= 0 && rx.TileContactAt(_target, tick) != 0)
            {
                var c = rx.TileContactAt(under, tick);
                var safeTicks = c == 0 ? g.WobbleTicks : (int)(c + (uint)g.WobbleTicks) - (int)tick;
                var margin = Tier == BotTier.Good || Tier == BotTier.Reference ? 7 : Tier == BotTier.Normal ? 9 : 12;
                if (under == _target && safeTicks > margin && intact > 3)
                {
                    g.Center(under, out var ux, out _, out var uz);
                    var hold = SteerTo(tick, in s, ux, uz); // hold the centre of the tile
                    if (Math.Abs(ux - s.PosX) < 250 && Math.Abs(uz - s.PosZ) < 250)
                        hold.MoveX = hold.MoveY = 0;
                    ReactToBars(tick, in s, ref hold);
                    return hold;
                }
            }

            var needNew = _target < 0 || _target / per != shelf || rx.TileContactAt(_target, tick) != 0 || tick - _targetSince > 75;
            if (!needNew)
            {
                g.Center(_target, out var cx, out _, out var cz);
                long ddx = cx - s.PosX, ddz = cz - s.PosZ;
                needNew = ddx * ddx + ddz * ddz < 500L * 500L;
            }

            if (needNew)
            {
                var best = -1;
                var bestScore = double.MaxValue;
                // Nearly gone: aim at a touched (wobbling / gone) tile to drop to the shelf below on purpose.
                var dropDown = intact < 6 && shelf < g.ShelfTopsMm.Length - 1;
                for (var i = shelf * per; i < (shelf + 1) * per; i++)
                {
                    var touched = rx.TileContactAt(i, tick) != 0;
                    if (dropDown ? !touched : touched)
                        continue;
                    g.Center(i, out var x, out _, out var z);
                    long dx = x - s.PosX, dz = z - s.PosZ;
                    var d = Math.Sqrt((double)dx * dx + (double)dz * dz);
                    if (d < 400 && !dropDown)
                        d += 2500; // not the one we are on
                    else if (!dropDown && d > 2400)
                        d += (d - 2400) * 2; // prefer a neighbouring tile (short hops)
                    // Away from other players, and slightly forward-biased (keep moving).
                    var crowd = 0.0;
                    for (var o = 0; o < SimCore.MaxSlots; o++)
                    {
                        if (o == Slot || _others == null || !_others(o, out var os))
                            continue;
                        long ox = x - os.PosX, oz = z - os.PosZ;
                        var od = Math.Sqrt((double)ox * ox + (double)oz * oz);
                        if (od < 3000)
                            crowd += (3000 - od) * 0.8;
                    }

                    var score = d + crowd + (Next() % 600);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }

                _target = best;
                _targetSince = tick;
                Retargets++;
            }

            if (_target < 0)
                return new InputCmd { Tick = tick };
            g.Center(_target, out var tx, out _, out var tz);
            var cmd = SteerTo(tick, in s, tx, tz);
            ReactToBars(tick, in s, ref cmd);
            return cmd;
        }
    }
}
