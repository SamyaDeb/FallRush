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
        /// <summary>Survival: the spiral side (route section) the bot steered by on its last tick (G4.3 route QA).</summary>
        public int Section;

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
                _wp = new WaypointBot(slot, routes, _hz, _cfg, seed + (uint)slot * 7u)
                {
                    TimingSlop = tier == BotTier.Easy ? 5 : tier == BotTier.Normal ? 4 : tier == BotTier.Good ? 2 : 0, // G4.2
                    StartAtFirstOnEntry = _mode == "Survival", // G4.3
                };
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
                    Section = SurvivalSection(_groundY);
                    plan = _wp.Next(tick, in s, Section);
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

        /// <summary>G1: the hop is moon-high (MoonBounce, apex 1.5 m): precise jumps are approached at 70 % stick.</summary>
        private bool MoonHop => _cfg != null && _cfg.JumpApex > 1.3f && _mode != "Skill"; // G4.4: a 70 % stick in the air brakes a spring launch (Star Fountain islands)

        private InputCmd Noise(uint tick, in MotorState s, InputCmd plan)
        {
            if (Tier == BotTier.Reference)
            {
                plan.Seq = ++_seq;
                plan.Tick = tick;
                if (MoonHop && ((_wp != null && _wp.Precise) || !s.Has(MotorState.Grounded)))
                {
                    // G1: MoonBounce hops reach ~4.3 m: ease off so the small S1 blocks are not overshot.
                    plan.MoveX = (sbyte)(plan.MoveX * 7 / 10);
                    plan.MoveY = (sbyte)(plan.MoveY * 7 / 10);
                }

                return plan;
            }

            var cmd = new InputCmd { Seq = ++_seq, Tick = tick, CamYaw = plan.CamYaw };
            // Hesitation: after a wait at a door, a bar or a lift ends, sometimes hold a little longer (race / survival only:
            // in the final a held tile is about to drop).
            var waitAction = _wp != null && (_wp.CurrentAction == WaypointAction.Door || _wp.CurrentAction == WaypointAction.WaitClear || _wp.CurrentAction == WaypointAction.JumpToPlatform);
            var waiting = plan.MoveX == 0 && plan.MoveY == 0 && waitAction;
            // G4.2: or after a look-ahead clear check said "go" without a wait (the bot misjudges the timing this often).
            var goNow = _wp != null && _wp.LookAheadGoTick == tick && !_wasWaiting;
            if ((_wasWaiting && !waiting || goNow) && Next() % 100 < (uint)S.HesitatePct)
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
                var k = precise ? (MoonHop ? 0.7 : 1.0) : S.SpeedPermille / 1000.0; // full speed for gap jumps (G1: 70 % under MoonBounce, see MoonHop)
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

        /// <summary>
        /// G4.4: a spring pad you are not using throws you out toward its island: steer round any pad whose square (+0.9 m)
        /// lies on the way ahead (sideways detour), like <see cref="AvoidBumpers"/>.
        /// </summary>
        private void AvoidPads(in MotorState s, ref InputCmd cmd, int except)
        {
            float ax = cmd.MoveX, az = cmd.MoveY;
            var ml = Math.Sqrt(ax * ax + az * az);
            if (ml < 1)
                return;
            double ux = ax / ml, uz = az / ml;
            for (var i = 0; i < _hz.Pads.Length; i++)
            {
                if (i == except)
                    continue;
                ref readonly var p = ref _hz.Pads[i];
                double bx = (p.CenterXMm - s.PosX) * 0.001, bz = (p.CenterZMm - s.PosZ) * 0.001;
                var d = Math.Sqrt(bx * bx + bz * bz);
                var r = Math.Max(p.HalfXMm, p.HalfZMm) * 0.0014 + 0.9;
                if (d > 5 || d < 1e-3)
                    continue;
                var along = bx * ux + bz * uz;
                var side = bx * uz - bz * ux;
                if (along <= -0.3 || Math.Abs(side) >= r)
                    continue;
                var push = (r - Math.Abs(side)) / r * 200.0 * (side >= 0 ? -1 : 1);
                ax += (float)(uz * push);
                az += (float)(-ux * push);
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

        // ------------------------------------------------------------------ skill: stars (G4.4 Star Fountain)

        /// <summary>G4.4 QA camper probe: hold this world spot (mm) all round, or (<see cref="CampIsland"/> ≥ 0) ride that island.</summary>
        public bool Camp;
        public int CampXMm, CampZMm;
        public int CampIsland = -1;
        private int _flightIsland = -1;

        private readonly List<int> _live = new List<int>();

        /// <summary>The island (platform index) a star anchor rides, or −1.</summary>
        private static int IslandOf(in StarFieldDef f, int anchor) =>
            f.Attach != null && anchor < f.Attach.Length && f.Attach[anchor] >= 0 && f.Attach[anchor] < 1000 ? f.Attach[anchor] : -1;

        private InputCmd SkillPlan(uint tick, in MotorState s)
        {
            if (_hz.StarFields.Length == 0 || _hz.Reactive == null)
                return new InputCmd { Tick = tick };
            var f = _hz.StarFields[0];
            var t = _hz.Time(tick);
            InputCmd cmd;
            if (Camp)
            {
                cmd = CampPlan(tick, in s, t);
                ReactToBars(tick, in s, ref cmd);
                return cmd;
            }

            _live.Clear();
            SkillRules.LiveStars(_hz, t, tick, _live);
            // Dropped stars count as targets (id StarDropBase + index).
            for (var d = 0; d < _hz.Reactive.Drops.Count; d++)
                if (_hz.Reactive.DropAvailable(d, tick, out _) || tick - _hz.Reactive.Drops[d].Tick < (uint)f.DropPopTicks)
                    _live.Add(HazardMath.StarDropBase + d);
            // G4.4 burst: once the arcs fly (the last 0.5 s, what a player sees), the next wave's stars are targets at their
            // spawn-tick pose.
            var nextWave = StarBurst.NextWave(in f, t, out var nextSpawn);
            var before = nextSpawn - t;
            if (nextWave >= 0 && before <= StarBurst.ArcTicks)
                for (var k = 0; k < f.WaveSize; k++)
                {
                    var id = nextWave * f.WaveSize + k;
                    if (id < HazardMath.StarGoldenBase)
                        _live.Add(id);
                }

            var best = -1;
            var bestScore = double.MinValue;
            int bx = 0, bz = 0, bIsland = -1;
            var range = S.StarPickRange * 1000.0;
            foreach (var id in _live)
            {
                int x, y, z, value;
                var island = -1;
                if (id >= HazardMath.StarDropBase)
                {
                    var e = _hz.Reactive.Drops[id - HazardMath.StarDropBase];
                    HazardMath.DropPosMm(in f, e.Xcm * 10, e.Ycm * 10, e.Zcm * 10, (uint)(id - HazardMath.StarDropBase + 1), f.DropPopTicks, out x, out y, out z);
                    value = 1;
                }
                else
                {
                    HazardMath.StarSlot(in f, _hz.Seed, id, out var a, out var sp, out var ex, out value);
                    island = IslandOf(in f, a);
                    // Aim where it will be when we get there (a turntable star moves): pose at t + travel time (≤ 1 s).
                    HazardMath.StarAnchorMm(in f, _hz, a, Math.Max(t, sp), out x, out y, out z);
                    double ddx = x - s.PosX, ddz = z - s.PosZ;
                    var lead = (int)Math.Min(30, Math.Sqrt(ddx * ddx + ddz * ddz) / 5000.0 * 30.0);
                    if (island < 0 && lead > 0)
                        HazardMath.StarAnchorMm(in f, _hz, a, Math.Max(t, sp) + lead, out x, out y, out z);
                    // An island star needs the spring trip (≈ 3 s) before it expires.
                    if (island >= 0 && ex - t < 90)
                        continue;
                }

                long dx = x - s.PosX, dz = z - s.PosZ;
                var dist = Math.Sqrt((double)dx * dx + (double)dz * dz);
                if (dist > range)
                    continue;
                if (island >= 0)
                    dist += 4000; // the detour via the spring
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
                    bz = z;
                    bIsland = island;
                }
            }

            if (best != _target)
            {
                _target = best;
                _targetSince = tick;
                Retargets++;
            }

            var rr = Math.Sqrt((double)s.PosX * s.PosX + (double)s.PosZ * s.PosZ);
            var onIsland = OnIsland(in s, t) >= 0;
            // Committed to a spring flight: keep steering at that island until we land (changing your mind mid-air
            // drops you into the gap).
            if (s.Has(MotorState.Grounded))
                _flightIsland = -1;
            else if (_flightIsland < 0 && s.VelY > 600)
                _flightIsland = LaunchedToward(in s);
            if (_flightIsland >= 0 && !s.Has(MotorState.Grounded))
                return FlyTo(tick, in s, _flightIsland, t);

            if (bIsland >= 0)
            {
                cmd = SteerToIsland(tick, in s, bIsland, t);
            }
            else if (onIsland)
            {
                cmd = LeaveIsland(tick, in s, t);
            }
            else if (best < 0 || before <= 45 && Dist(s, bx, bz) > 4000)
            {
                // G4.4: nothing worth it, or the crown glows (a wave within 1.5 s) and our star is far: head for the
                // fountain ring (r 5 m on our side), where most of the burst lands.
                var k = rr < 1 ? 0.0 : 5000.0 / rr;
                cmd = SteerTo(tick, in s, (int)(s.PosX * k), (int)(s.PosZ * k));
            }
            else
            {
                cmd = SteerTo(tick, in s, bx, bz);
            }

            // Stay off the rim (r 18 m) unless heading for an island.
            if (rr > 16800 && bIsland < 0 && !onIsland)
                cmd = SteerTo(tick, in s, 0, 0);
            AvoidBumpers(in s, ref cmd);
            if (bIsland < 0 && s.Has(MotorState.Grounded))
                AvoidPads(in s, ref cmd, -1);
            ReactToBars(tick, in s, ref cmd); // G4.4: jump the low sweeper bars
            if (s.Has(MotorState.Grounded) && tick % 47 == (uint)Slot && bIsland < 0 && !onIsland && rr < 14000)
                cmd.Buttons |= InputCmd.JumpPressed | InputCmd.JumpHeld; // a hop now and then (players do)
            return cmd;
        }

        private static double Dist(in MotorState s, int x, int z)
        {
            double dx = x - s.PosX, dz = z - s.PosZ;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>The island (platform) we stand on at hazard time t, or −1.</summary>
        private int OnIsland(in MotorState s, int t)
        {
            if (s.PosY < 500)
                return -1;
            for (var i = 0; i < _hz.Platforms.Length; i++)
            {
                ref readonly var p = ref _hz.Platforms[i];
                HazardMath.PlatformCenterMm(in p, t, out var cx, out var cy, out var cz);
                if (Math.Abs(s.PosX - cx) <= p.HalfXMm + 100 && Math.Abs(s.PosZ - cz) <= p.HalfZMm + 100 && s.PosY >= cy && s.PosY <= cy + p.HalfYMm + 1500)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Back to the arena from an island: run in at 30° off the island's line (so the landing and the run-in miss its
        /// spring, which would throw you straight back out) and hop at the island's inner edge (over the gap and the lip).
        /// </summary>
        private InputCmd LeaveIsland(uint tick, in MotorState s, int t)
        {
            var rr0 = Math.Sqrt((double)s.PosX * s.PosX + (double)s.PosZ * s.PosZ);
            var ang = (Slot & 1) == 0 ? 0.52 : -0.52;
            double ux = rr0 < 1 ? 0 : s.PosX / rr0, uz = rr0 < 1 ? 0 : s.PosZ / rr0;
            var tx = (ux * Math.Cos(ang) - uz * Math.Sin(ang)) * 10000;
            var tz = (ux * Math.Sin(ang) + uz * Math.Cos(ang)) * 10000;
            var cmd = SteerTo(tick, in s, (int)tx, (int)tz);
            var i = OnIsland(in s, t);
            if (i < 0 || !s.Has(MotorState.Grounded))
                return cmd;
            ref readonly var p = ref _hz.Platforms[i];
            HazardMath.PlatformCenterMm(in p, t, out var cx, out _, out var cz);
            var rr = Math.Sqrt((double)s.PosX * s.PosX + (double)s.PosZ * s.PosZ);
            if (rr < 1)
                return cmd;
            // 0.45 m further in: off the island → jump now.
            var ax = s.PosX - s.PosX / rr * 450;
            var az = s.PosZ - s.PosZ / rr * 450;
            if (Math.Abs(ax - cx) > p.HalfXMm || Math.Abs(az - cz) > p.HalfZMm)
                cmd.Buttons |= InputCmd.JumpPressed | InputCmd.JumpHeld;
            return cmd;
        }

        /// <summary>
        /// In the air toward an island: steer at it only within 30° of our flight direction. Air control keeps the speed
        /// only along the stick; steering hard sideways bleeds a spring launch (MoonBounce flights landed short).
        /// </summary>
        private InputCmd FlyTo(uint tick, in MotorState s, int island, int t)
        {
            HazardMath.PlatformCenterMm(in _hz.Platforms[island], t + 8, out var ix, out _, out var iz);
            double vx = s.VelX, vz = s.VelZ, tx = ix - s.PosX, tz = iz - s.PosZ;
            var vl = Math.Sqrt(vx * vx + vz * vz);
            var tl = Math.Sqrt(tx * tx + tz * tz);
            if (vl < 50 || tl < 1 || (vx * tx + vz * tz) / (vl * tl) >= 0.866)
                return SteerTo(tick, in s, ix, iz);
            return SteerTo(tick, in s, (int)(s.PosX + vx / vl * 3000), (int)(s.PosZ + vz / vl * 3000));
        }

        /// <summary>Just thrown by a spring pad (rising, within 2.5 m of one): the island it launches toward, or −1.</summary>
        private int LaunchedToward(in MotorState s)
        {
            for (var i = 0; i < _hz.Pads.Length; i++)
            {
                ref readonly var p = ref _hz.Pads[i];
                double dx = s.PosX - p.CenterXMm, dz = s.PosZ - p.CenterZMm;
                if (dx * dx + dz * dz > 2500.0 * 2500.0)
                    continue;
                for (var k = 0; k < _hz.Platforms.Length; k++)
                    if (PadFor(k) == i)
                        return k;
            }

            return -1;
        }

        /// <summary>The spring pad whose launch points at island <paramref name="island"/>'s middle position, or −1.</summary>
        private int PadFor(int island)
        {
            if ((uint)island >= (uint)_hz.Platforms.Length)
                return -1;
            ref readonly var pl = ref _hz.Platforms[island];
            var best = -1;
            var bestCos = 0.8;
            for (var i = 0; i < _hz.Pads.Length; i++)
            {
                ref readonly var p = ref _hz.Pads[i];
                double lx = p.LaunchXCms, lz = p.LaunchZCms;
                double tx = pl.CenterXMm - p.CenterXMm, tz = pl.CenterZMm - p.CenterZMm;
                var ll = Math.Sqrt(lx * lx + lz * lz) * Math.Sqrt(tx * tx + tz * tz);
                if (ll < 1)
                    continue;
                var c = (lx * tx + lz * tz) / ll;
                if (c > bestCos)
                {
                    bestCos = c;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>Spring flight (ticks) from a pad top to an island top <paramref name="dyMm"/> higher, and the horizontal distance (mm).</summary>
        private void Flight(in SpringPadDef p, int dyMm, out int ticks, out int distMm)
        {
            var vy = p.LaunchYCms * 0.01;
            var vh = Math.Sqrt((double)p.LaunchXCms * p.LaunchXCms + (double)p.LaunchZCms * p.LaunchZCms) * 0.01;
            var g = Math.Max(1.0, _cfg.Gravity);
            var up = vy / g;
            var apex = vy * vy / (2 * g);
            var fall = Math.Max(0.05, apex - dyMm * 0.001);
            var down = Math.Sqrt(2 * fall / (g * Math.Max(1.0, _cfg.FallMultiplier)));
            ticks = (int)Math.Round((up + down) * 30);
            distMm = (int)(vh * (up + down) * 1000);
        }

        /// <summary>
        /// G4.4: to an island by its spring. Wait 2.5 m short of the pad until the island will be under the landing point
        /// (pure platform pose at arrival + flight), then run over the pad; in the air steer at the island.
        /// </summary>
        private InputCmd SteerToIsland(uint tick, in MotorState s, int island, int t)
        {
            var pi = PadFor(island);
            ref readonly var pl = ref _hz.Platforms[island];
            if (OnIsland(in s, t) == island)
            {
                HazardMath.PlatformCenterMm(in pl, t + 3, out var ix, out _, out var iz);
                return SteerTo(tick, in s, ix, iz);
            }

            if (pi < 0)
            {
                HazardMath.PlatformCenterMm(in pl, t, out var ix, out _, out var iz);
                return SteerTo(tick, in s, ix, iz);
            }

            ref readonly var p = ref _hz.Pads[pi];
            double dlx = p.LaunchXCms, dlz = p.LaunchZCms;
            var dl = Math.Sqrt(dlx * dlx + dlz * dlz);
            dlx /= dl;
            dlz /= dl;
            var along = (s.PosX - p.CenterXMm) * dlx + (s.PosZ - p.CenterZMm) * dlz; // < 0: short of the pad
            if (!s.Has(MotorState.Grounded) && (along > -400 || s.VelY > 300))
            {
                if (s.VelY > 600 && along > -1200)
                    _flightIsland = island; // launched by the pad
                return FlyTo(tick, in s, island, t);
            }

            Flight(in p, pl.CenterYMm + pl.HalfYMm - p.CenterYMm, out var fly, out var reach);
            var wx = (int)(p.CenterXMm - dlx * 2500);
            var wz = (int)(p.CenterZMm - dlz * 2500);
            var lateral = Math.Abs((s.PosX - p.CenterXMm) * dlz - (s.PosZ - p.CenterZMm) * dlx);
            var committed = along > -2700 && lateral < 700;
            var toPad = Math.Max(0.0, -along) / 5000.0 * 30.0 + 2;
            // Landing point (launched at the pad's near edge, ballistic: air control never brakes a launch) and the island box then.
            var from = -Math.Max(p.HalfXMm, p.HalfZMm) * 0.7;
            var lx = p.CenterXMm + dlx * (from + reach);
            var lz = p.CenterZMm + dlz * (from + reach);
            HazardMath.PlatformCenterMm(in pl, t + (int)toPad + fly, out var cx, out _, out var cz);
            var margin = committed ? 200 : 450;
            var good = Math.Abs(lx - cx) <= pl.HalfXMm - margin && Math.Abs(lz - cz) <= pl.HalfZMm - margin;
            if (committed && good)
                return SteerTo(tick, in s, (int)(p.CenterXMm + dlx * 3000), (int)(p.CenterZMm + dlz * 3000));
            if (committed)
            {
                // Bad timing on the way in: back off to the wait point.
                return SteerTo(tick, in s, wx, wz);
            }

            var dw = Dist(s, wx, wz);
            if (dw > 500)
                return SteerTo(tick, in s, wx, wz);
            if (good)
                return SteerTo(tick, in s, (int)(p.CenterXMm + dlx * 3000), (int)(p.CenterZMm + dlz * 3000));
            var hold = new InputCmd { Tick = tick };
            hold.CamYaw = (ushort)(Yaw256.FromVector((int)(dlx * 1000), (int)(dlz * 1000), s.Yaw) << 8);
            return hold;
        }

        /// <summary>G4.4 QA camper: hold a fixed spot (or ride an island) all round; no star chasing.</summary>
        private InputCmd CampPlan(uint tick, in MotorState s, int t)
        {
            if (CampIsland >= 0 && CampIsland < _hz.Platforms.Length)
                return SteerToIsland(tick, in s, CampIsland, t);
            var cmd = SteerTo(tick, in s, CampXMm, CampZMm);
            if (Dist(s, CampXMm, CampZMm) < 250)
                cmd.MoveX = cmd.MoveY = 0;
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
