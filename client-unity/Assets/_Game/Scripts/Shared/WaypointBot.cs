using System;

namespace FallRush.Shared
{
    public enum WaypointAction : byte
    {
        Run = 0,          // steer to the point
        Jump = 1,         // on arrival press jump (the next point is across a gap)
        WaitClear = 2,    // on arrival wait until the straight path to the next point is clear of spinner bars and mallets
        Door = 3,         // on arrival wait until gate door Arg stays open long enough to pass
        JumpToPlatform = 4, // on arrival wait until platform Arg is within reach, then jump toward it
        Platform = 5,     // target = platform Arg's live centre; reached when standing on it
        Dive = 6,         // on arrival press dive (toward the next point)
        Land = 7,         // a block to land on: reached only when grounded within the radius (air control brakes over it)
    }

    /// <summary>One authored bot waypoint (mm). Server-side only: not part of the map content hash.</summary>
    [Serializable]
    public struct Waypoint
    {
        public int XMm, YMm, ZMm;
        public int RadiusMm;
        public WaypointAction Action;
        public int Arg;
    }

    /// <summary>One route through one section: <see cref="Section"/> = checkpoints reached when it starts (0 = from the start).</summary>
    [Serializable]
    public struct BotRoute
    {
        public int Section;
        public string Name;
        public Waypoint[] Points;
    }

    /// <summary>
    /// Waypoint bot (M3 §5.3; the base of M4's BotBrain). Produces <see cref="InputCmd"/>s from an authored route graph
    /// (two or more routes per section), through the same sanitiser and motor as humans. It knows only what a player can
    /// know: its own state, the hazard pose functions and the next waypoints. It times gap jumps, waits for spinner bars and
    /// mallets to pass, waits for doors, boards moving blocks, and recovers after a knockback or a fall. Reference-speed
    /// mode: no noise, no mistakes (M4 adds them). Deterministic for a given seed.
    /// </summary>
    public sealed class WaypointBot
    {
        public readonly int Slot;
        private readonly BotRoute[] _routes;
        private readonly HazardSet _hz;
        private readonly MovementConfig _cfg;
        private uint _rng;
        private int _section = -1;
        private int _route = -1;
        private int _wp;
        private uint _seq;
        private uint _jumpHeldUntil;
        private uint _progressTick;
        private long _bestDist2 = long.MaxValue;
        private bool _wasStunned;
        private bool _wasRespawning;

        // Statistics (QA).
        public int Stuck;           // re-acquisitions after no progress for 6 s
        public int Recoveries;      // re-acquisitions after a knockback / respawn
        public string LastStuckReason;
        public readonly int[] RouteTaken = new int[16]; // route index chosen per section (last time)

        /// <summary>Route choice per section: −1 = random from the seed, else this route index (QA: force a route).</summary>
        public int ForcedRoute = -1;

        public WaypointBot(int slot, BotRoute[] routes, HazardSet hazards, MovementConfig cfg, uint seed)
        {
            Slot = slot;
            _routes = routes ?? Array.Empty<BotRoute>();
            _hz = hazards;
            _cfg = cfg;
            _rng = seed * 0x9E3779B1u + (uint)slot * 0x85EBCA6Bu + 1u;
            if (_rng == 0)
                _rng = 1;
        }

        private uint Next()
        {
            _rng ^= _rng << 13;
            _rng ^= _rng >> 17;
            _rng ^= _rng << 5;
            return _rng;
        }

        public string CurrentRouteName => _route >= 0 && _route < _routes.Length ? _routes[_route].Name : "-";
        public int Waypoint => _wp;

        /// <summary>The current waypoint's action (BotBrain keeps its noise out of precise moves: jumps, landings, platforms).</summary>
        public WaypointAction CurrentAction => _route >= 0 && _route < _routes.Length && _wp < _routes[_route].Points.Length ? _routes[_route].Points[_wp].Action : WaypointAction.Run;

        /// <summary>Is the current or the next waypoint a precise move (jump / land / platform / dive)?</summary>
        public bool Precise
        {
            get
            {
                if (_route < 0 || _route >= _routes.Length)
                    return false;
                var pts = _routes[_route].Points;
                for (var k = _wp; k <= _wp + 1 && k < pts.Length; k++)
                {
                    var a = pts[k].Action;
                    if (a == WaypointAction.Jump || a == WaypointAction.Land || a == WaypointAction.JumpToPlatform || a == WaypointAction.Platform || a == WaypointAction.Dive || a == WaypointAction.WaitClear || a == WaypointAction.Door)
                        return true;
                }

                return false;
            }
        }

        /// <summary>The command for <paramref name="tick"/>; <paramref name="checkpoints"/> = checkpoints this player has reached.</summary>
        public InputCmd Next(uint tick, in MotorState s, int checkpoints)
        {
            var cmd = new InputCmd { Seq = ++_seq, Tick = tick };
            if (s.Has(MotorState.Respawning))
            {
                _wasRespawning = true;
                return cmd;
            }

            if (_section != checkpoints || _route < 0)
                EnterSection(checkpoints, in s, tick);
            if (_wasRespawning)
            {
                _wasRespawning = false;
                Recoveries++;
                Reacquire(in s, tick);
            }

            if (s.StunTicks > 0)
            {
                _wasStunned = true;
                return cmd;
            }

            if (_wasStunned)
            {
                _wasStunned = false;
                Recoveries++;
                Reacquire(in s, tick);
            }

            if (_route < 0)
                return cmd;
            var pts = _routes[_route].Points;
            if (_wp >= pts.Length)
                _wp = pts.Length - 1;

            var p = pts[_wp];
            var tx = p.XMm;
            var tz = p.ZMm;
            if (p.Action == WaypointAction.Platform && (uint)p.Arg < (uint)_hz.Platforms.Length)
            {
                HazardMath.PlatformCenterMm(in _hz.Platforms[p.Arg], _hz.Time(tick), out tx, out _, out tz);
            }

            long dx = tx - s.PosX, dz = tz - s.PosZ;
            var d2 = dx * dx + dz * dz;
            var grounded = s.Has(MotorState.Grounded);
            var arrived = d2 <= (long)p.RadiusMm * p.RadiusMm && Math.Abs(s.PosY - p.YMm) < 1500;
            // Run / Jump points also count once passed (overshooting a jump point must not turn us back toward the gap).
            if (!arrived && (p.Action == WaypointAction.Run || p.Action == WaypointAction.Jump) && _wp > 0 && Math.Abs(s.PosY - p.YMm) < 1500)
            {
                var q = pts[_wp - 1];
                long ax = p.XMm - q.XMm, az = p.ZMm - q.ZMm;
                long px = s.PosX - p.XMm, pz = s.PosZ - p.ZMm;
                var al2 = ax * ax + az * az;
                if (al2 > 0 && ax * px + az * pz > 0)
                {
                    var cross = Math.Abs(ax * pz - az * px) / Math.Sqrt(al2);
                    arrived = cross < p.RadiusMm * 2 + 500;
                }
            }

            if (p.Action == WaypointAction.Land)
                arrived = grounded && arrived;
            if (p.Action == WaypointAction.Platform)
                arrived = grounded && s.Surface == (byte)(_hz.PlatformSurface(p.Arg) + 1) && d2 < 900L * 900L;

            var move = true;
            var jump = false;
            var dive = false;
            if (arrived)
            {
                switch (p.Action)
                {
                    case WaypointAction.Jump:
                        if (grounded)
                        {
                            jump = true;
                            Advance(tick);
                        }

                        break;
                    case WaypointAction.Dive:
                        dive = true;
                        Advance(tick);
                        break;
                    case WaypointAction.WaitClear:
                        if (PathClear(in s, tick, pts, _wp))
                            Advance(tick);
                        else
                            move = false;
                        break;
                    case WaypointAction.Door:
                        if (DoorPassable(p.Arg, in s, tick, pts, _wp))
                            Advance(tick);
                        else
                            move = false;
                        break;
                    case WaypointAction.JumpToPlatform:
                        if (grounded && PlatformInReach(p.Arg, in s, tick))
                        {
                            jump = true;
                            Advance(tick);
                        }
                        else
                        {
                            move = false;
                        }

                        break;
                    default:
                        Advance(tick);
                        break;
                }

                if (move && _wp < pts.Length)
                {
                    // Steer at the new target this tick already.
                    var n = pts[_wp];
                    tx = n.XMm;
                    tz = n.ZMm;
                    if (n.Action == WaypointAction.Platform && (uint)n.Arg < (uint)_hz.Platforms.Length)
                        HazardMath.PlatformCenterMm(in _hz.Platforms[n.Arg], _hz.Time(tick), out tx, out _, out tz);
                    dx = tx - s.PosX;
                    dz = tz - s.PosZ;
                }
            }

            // Progress watchdog: no 1 m of progress toward the target for 6 s → re-acquire (stuck).
            if (d2 + 1_000_000L < _bestDist2)
            {
                _bestDist2 = d2;
                _progressTick = tick;
            }
            else if (move && tick - _progressTick > 180)
            {
                Stuck++;
                LastStuckReason = $"no progress to {CurrentRouteName}#{_wp} ({p.Action}) at ({s.PosX / 1000},{s.PosY / 1000},{s.PosZ / 1000})";
                Reacquire(in s, tick);
            }

            if (move)
            {
                var l = Math.Sqrt((double)dx * dx + (double)dz * dz);
                if (l > 30)
                {
                    cmd.MoveX = (sbyte)Math.Round(dx / l * 127);
                    cmd.MoveY = (sbyte)Math.Round(dz / l * 127);
                    cmd.CamYaw = (ushort)(Yaw256.FromVector((int)dx, (int)dz, s.Yaw) << 8);
                }
            }

            // Reactive layer: a spinner bar about to hit us → jump it (low / mid bar) or dive under it (high bar).
            if (move && !jump && !dive && grounded && s.LandTicks == 0 && !s.Has(MotorState.Diving))
                ReactToBars(in s, tick, cmd.MoveX, cmd.MoveY, ref jump, ref dive);

            if (jump)
            {
                cmd.Buttons |= InputCmd.JumpPressed | InputCmd.JumpHeld;
                _jumpHeldUntil = tick + 8;
            }
            else if (tick < _jumpHeldUntil)
            {
                cmd.Buttons |= InputCmd.JumpHeld;
            }

            if (dive)
                cmd.Buttons |= InputCmd.DivePressed | InputCmd.DiveHeld;
            InputSanitizer.Sanitize(ref cmd);
            return cmd;
        }

        private void Advance(uint tick)
        {
            _wp++;
            _bestDist2 = long.MaxValue;
            _progressTick = tick;
        }

        private void EnterSection(int section, in MotorState s, uint tick)
        {
            _section = section;
            var count = 0;
            for (var i = 0; i < _routes.Length; i++)
                if (_routes[i].Section == section)
                    count++;
            _route = -1;
            if (count == 0)
                return;
            var choice = ForcedRoute >= 0 ? ForcedRoute % count : (int)(Next() % (uint)count);
            var pick = choice;
            for (var i = 0; i < _routes.Length; i++)
            {
                if (_routes[i].Section != section)
                    continue;
                if (pick-- == 0)
                {
                    _route = i;
                    break;
                }
            }

            if (section < RouteTaken.Length)
                RouteTaken[section] = choice;
            _wp = 0;
            Reacquire(in s, tick);
        }

        /// <summary>Nearest waypoint of the current route that is not behind us (by index), on our level.</summary>
        private void Reacquire(in MotorState s, uint tick)
        {
            _bestDist2 = long.MaxValue;
            _progressTick = tick;
            if (_route < 0)
                return;
            var pts = _routes[_route].Points;
            var best = long.MaxValue;
            var bi = 0;
            for (var i = 0; i < pts.Length; i++)
            {
                long dx = pts[i].XMm - s.PosX, dz = pts[i].ZMm - s.PosZ, dy = pts[i].YMm - s.PosY;
                var d = dx * dx + dz * dz + 4 * dy * dy;
                if (d < best)
                {
                    best = d;
                    bi = i;
                }
            }

            // Prefer the point after the nearest if we are already past it (closer to the one after than it is).
            if (bi + 1 < pts.Length)
            {
                long ax = pts[bi + 1].XMm - pts[bi].XMm, az = pts[bi + 1].ZMm - pts[bi].ZMm;
                long px = s.PosX - pts[bi].XMm, pz = s.PosZ - pts[bi].ZMm;
                if (ax * px + az * pz > 0 && pts[bi].Action == WaypointAction.Run)
                    bi++;
            }

            _wp = bi;
        }

        // ------------------------------------------------------------------ hazard reading (what a player can see)

        private const int RunMmPerTick = 220; // ≈ 6.6 m/s, a little under max speed

        /// <summary>Is the straight run from here to the next waypoint free of spinner bars and mallet heads for its whole duration (+ margin)?</summary>
        private bool PathClear(in MotorState s, uint tick, Waypoint[] pts, int wp)
        {
            if (wp + 1 >= pts.Length)
                return true;
            var n = pts[wp + 1];
            long dx = n.XMm - s.PosX, dz = n.ZMm - s.PosZ;
            var len = Math.Sqrt((double)dx * dx + (double)dz * dz);
            var ticks = (int)(len / RunMmPerTick) + 4 + 8; // acceleration + margin
            var r = MotorState.QuantizeMm(_cfg.Radius) + 120;
            var h = MotorState.QuantizeMm(_cfg.Height);
            for (var k = 0; k <= ticks; k++)
            {
                var along = Math.Min(len, Math.Max(0, (k - 3) * RunMmPerTick));
                var f = len > 0 ? along / len : 0;
                var x = s.PosX + (int)(dx * f);
                var z = s.PosZ + (int)(dz * f);
                var t = _hz.Time(tick + (uint)k);
                for (var i = 0; i < _hz.Sweepers.Length; i++)
                    if (HazardMath.SweeperContactMm(in _hz.Sweepers[i], t, x, s.PosY, z, r, h, out _, out _))
                        return false;
                for (var i = 0; i < _hz.Mallets.Length; i++)
                    if (HazardMath.MalletContactMm(in _hz.Mallets[i], t, x, s.PosY, z, r, h, out _, out _))
                        return false;
            }

            return true;
        }

        public int Jumps, Dives;

        private void ReactToBars(in MotorState s, uint tick, int mx, int mz, ref bool jump, ref bool dive)
        {
            if (_hz.Sweepers.Length == 0 || s.ImmunityTicks > 0)
                return;
            // Where we will be: along the current velocity, or the steering direction at running speed from a standstill.
            float vx = s.VelX * 10f, vz = s.VelZ * 10f; // mm/s
            var sp = Math.Sqrt(vx * vx + vz * vz);
            if (sp < 3000 && (mx != 0 || mz != 0))
            {
                var l = Math.Sqrt(mx * mx + mz * mz);
                vx = (float)(mx / l * 6600);
                vz = (float)(mz / l * 6600);
            }

            var r = MotorState.QuantizeMm(_cfg.Radius);
            var h = MotorState.QuantizeMm(_cfg.Height);
            for (var k = 3; k <= 6; k++)
            {
                var x = s.PosX + (int)(vx * k / 30f);
                var z = s.PosZ + (int)(vz * k / 30f);
                var t = _hz.Time(tick + (uint)k);
                for (var i = 0; i < _hz.Sweepers.Length; i++)
                {
                    ref readonly var d = ref _hz.Sweepers[i];
                    if (!HazardMath.SweeperContactMm(in d, t, x, s.PosY, z, r, h, out _, out _))
                        continue;
                    var barBottom = d.PivotYMm + d.BarHeightMm - d.BarRadiusMm - s.PosY;
                    if (barBottom >= 650)
                    {
                        if (k >= 4 && s.DiveCooldownTicks == 0)
                        {
                            dive = true;
                            Dives++;
                        }
                    }
                    else if (k <= 5)
                    {
                        jump = true;
                        Jumps++;
                    }

                    return;
                }
            }
        }

        /// <summary>Is gate door <paramref name="door"/> open now and still open when we are through it?</summary>
        private bool DoorPassable(int door, in MotorState s, uint tick, Waypoint[] pts, int wp)
        {
            if ((uint)door >= (uint)_hz.Doors.Length)
                return true;
            ref readonly var d = ref _hz.Doors[door];
            var t = _hz.Time(tick);
            if (!HazardMath.DoorOpen(in d, t))
                return false;
            var target = wp + 1 < pts.Length ? pts[wp + 1] : pts[wp];
            long dx = target.XMm - s.PosX, dz = target.ZMm - s.PosZ;
            var need = (int)(Math.Sqrt((double)dx * dx + (double)dz * dz) / RunMmPerTick) + 10;
            return HazardMath.DoorTicksUntilClosing(in d, t) > need;
        }

        private bool PlatformInReach(int platform, in MotorState s, uint tick)
        {
            if ((uint)platform >= (uint)_hz.Platforms.Length)
                return true;
            ref readonly var d = ref _hz.Platforms[platform];
            // Where the block will be when we land (~18 ticks of flight).
            HazardMath.PlatformCenterMm(in d, _hz.Time(tick + 18), out var x, out var y, out var z);
            long dx = x - s.PosX, dz = z - s.PosZ;
            var dist = Math.Sqrt((double)dx * dx + (double)dz * dz) - Math.Min(d.HalfXMm, d.HalfZMm) * 0.6;
            var top = y + d.HalfYMm;
            return dist < 3600 && top - s.PosY < 900 && top - s.PosY > -1500;
        }
    }
}
