using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    public enum WaypointAction : byte
    {
        Run = 0,          // steer to the point
        Jump = 1,         // on arrival press jump (the next point is across a gap)
        WaitClear = 2,    // on arrival wait until the path ahead is clear of spinner bars, mallets and rolling balls (Arg 0: the straight
                          // run to the next point, as in M3; Arg n ≥ 1: the next n points at the configured run speed, G4.2)
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
            else
            {
                Ride(pts, _wp, tick, ref tx, ref tz);
            }

            // G4.2: run-up before a jump that follows a look-ahead wait (a standing hop falls short; on ButterFloors the
            // bot also slid to the very edge while stopping).
            var runningUp = false;
            if (_runUpWp == _wp)
            {
                long rx = _runUpX - s.PosX, rz = _runUpZ - s.PosZ;
                if (rx * rx + rz * rz < 350L * 350L || tick > _runUpUntil)
                    _runUpWp = -2 - _wp; // done for this point
                else
                {
                    tx = _runUpX;
                    tz = _runUpZ;
                    runningUp = true;
                }
            }
            else if (_runUpWp != -2 - _wp && (p.Action == WaypointAction.Jump || p.Action == WaypointAction.JumpToPlatform) && _wp > 0 && _wp + 1 < pts.Length
                     && pts[_wp - 1].Action == WaypointAction.WaitClear && pts[_wp - 1].Arg > 0 && s.Has(MotorState.Grounded))
            {
                long ux = pts[_wp + 1].XMm - tx, uz = pts[_wp + 1].ZMm - tz;
                var ul = Math.Sqrt((double)ux * ux + (double)uz * uz);
                long ox = tx - s.PosX, oz = tz - s.PosZ;
                var along = ul > 1 ? (s.VelX * 10.0 * ux + s.VelZ * 10.0 * uz) / ul : 0; // mm/s toward the landing
                if (ul > 1 && along < 2500 && ox * ox + oz * oz < 1200L * 1200L)
                {
                    _runUpWp = _wp;
                    _runUpX = (int)(tx - ux / ul * 1300);
                    _runUpZ = (int)(tz - uz / ul * 1300);
                    _runUpUntil = tick + 30;
                    tx = _runUpX;
                    tz = _runUpZ;
                    runningUp = true;
                    RunUps++;
                }
            }

            long dx = tx - s.PosX, dz = tz - s.PosZ;
            var d2 = dx * dx + dz * dz;
            var grounded = s.Has(MotorState.Grounded);
            var arrived = !runningUp && d2 <= (long)p.RadiusMm * p.RadiusMm && Math.Abs(s.PosY - p.YMm) < 1500;
            // Run / Jump points also count once passed (overshooting a jump point must not turn us back toward the gap).
            if (!arrived && !runningUp && (p.Action == WaypointAction.Run || p.Action == WaypointAction.Jump) && _wp > 0 && Math.Abs(s.PosY - p.YMm) < 1500)
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
                        if (_waitSince == 0 || _waitWp != _wp)
                        {
                            _waitSince = tick;
                            _waitWp = _wp;
                        }

                        if (p.Arg > 0 ? PathClearAhead(in s, tick, pts, _wp, p.Arg) : PathClear(in s, tick, pts, _wp))
                        {
                            _waitSince = 0;
                            if (p.Arg > 0)
                                LookAheadGoTick = tick;
                            Advance(tick);
                        }
                        else if (p.Arg > 0 && tick - _waitSince > WaitTimeoutTicks)
                        {
                            // G4.2: no clear window for 8 s (a player would go): go anyway.
                            WaitTimeouts++;
                            _waitSince = 0;
                            Advance(tick);
                        }
                        else
                        {
                            move = false;
                        }

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
                    else
                        Ride(pts, _wp, tick, ref tx, ref tz);
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

            // G4.2: a rolling ball about to hit us (moving or waiting) → step out of its line.
            if (!jump && !dive && grounded && _hz.RollingBalls.Length > 0 && DodgeBalls(in s, tick, move ? cmd.MoveX : 0, move ? cmd.MoveY : 0, out var ddx, out var ddz))
            {
                cmd.MoveX = (sbyte)ddx;
                cmd.MoveY = (sbyte)ddz;
                if (ddx != 0 || ddz != 0)
                    cmd.CamYaw = (ushort)(Yaw256.FromVector(ddx, ddz, s.Yaw) << 8);
                Dodges++;
            }

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

        /// <summary>
        /// G4.2: a wait / take-off point right after a Platform point (we stand on that block) rides with the block's
        /// horizontal displacement: on a block bobbing sideways a fixed point made us run sideways and jump sideways.
        /// </summary>
        private void Ride(Waypoint[] pts, int wp, uint tick, ref int tx, ref int tz)
        {
            var a = pts[wp].Action;
            if (a != WaypointAction.JumpToPlatform && a != WaypointAction.WaitClear && a != WaypointAction.Jump)
                return;
            for (var i = wp - 1; i >= 0 && i >= wp - 2; i--)
            {
                var q = pts[i];
                if (q.Action == WaypointAction.Platform && (uint)q.Arg < (uint)_hz.Platforms.Length)
                {
                    ref readonly var d = ref _hz.Platforms[q.Arg];
                    HazardMath.PlatformCenterMm(in d, _hz.Time(tick), out var cx, out _, out var cz);
                    tx += cx - d.CenterXMm;
                    tz += cz - d.CenterZMm;
                    return;
                }

                if (q.Action != WaypointAction.WaitClear)
                    return;
            }
        }

        private void Advance(uint tick)
        {
            _runUpWp = -1;
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
                if (_hz.RollingBalls.Length > 0 && BallNear(x, s.PosY, z, t, 120))
                    return false;
            }

            return true;
        }

        // ------------------------------------------------------------------ G4.2: look-ahead clear check, rolling balls

        /// <summary>Run speed in mm per tick from the motor config (v2: 5 m/s → 167).</summary>
        private int RunPerTick => Math.Max(60, (int)(_cfg.MaxSpeed * 1000f / HazardMath.TickRate));

        /// <summary>
        /// Is a rolling ball (pure pose, what a player sees) within reach of a standing capsule at (x, y, z) at hazard time
        /// <paramref name="t"/>? 2D reach = ball r + capsule r + <paramref name="marginMm"/>; balls far above / below ignored.
        /// </summary>
        private bool BallNear(int x, int y, int z, int t, int marginMm)
        {
            var capR = MotorState.QuantizeMm(_cfg.Radius);
            for (var i = 0; i < _hz.RollingBalls.Length; i++)
            {
                ref readonly var d = ref _hz.RollingBalls[i];
                long reach = d.RadiusMm + capR + marginMm;
                var n = HazardMath.BallMaxAlive(in d);
                for (var j = 0; j < n; j++)
                {
                    if (!HazardMath.BallPosMm(in d, t, j, out var bx, out var by, out var bz, out _))
                        break;
                    if (Math.Abs(by - (y + 500)) > d.RadiusMm + 900)
                        continue;
                    long dx = x - bx, dz = z - bz;
                    if (dx * dx + dz * dz < reach * reach)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// WaitClear with Arg n ≥ 1: walk the next <paramref name="n"/> waypoints from here at the configured run speed
        /// (+ 3 ticks to accelerate, + 2 ticks at each landing / jump / platform point, + 6 at the end). Each sample point
        /// of that walk must be clear of the nearby spinner bars, mallet heads and rolling balls over a window of ticks
        /// around its predicted time (0.8× − 2 … + 6 ticks: a hop arrives a little earlier than a run, a wobble later).
        /// </summary>
        private bool PathClearAhead(in MotorState s, uint tick, Waypoint[] pts, int wp, int n)
        {
            var v = RunPerTick;
            var r = MotorState.QuantizeMm(_cfg.Radius) + Math.Max(0, 150 - 30 * TimingSlop);
            var h = MotorState.QuantizeMm(_cfg.Height);
            _aheadN = 0;
            long x = s.PosX, y = s.PosY, z = s.PosZ;
            var k = 3;
            AddSample(x, y, z, 0);
            for (var i = wp + 1; i <= wp + n && i < pts.Length; i++)
            {
                var p = pts[i];
                long dx = p.XMm - x, dy = p.YMm - y, dz = p.ZMm - z;
                var len = Math.Sqrt((double)dx * dx + (double)dz * dz);
                var steps = Math.Max(1, (int)Math.Ceiling(len / v));
                for (var j = 1; j <= steps; j++)
                    AddSample(x + dx * j / steps, y + dy * j / steps, z + dz * j / steps, k + j);
                k += steps;
                x = p.XMm;
                y = p.YMm;
                z = p.ZMm;
                var dwell = p.Action == WaypointAction.Run || p.Action == WaypointAction.WaitClear ? 0 : 2;
                if (i == wp + n || i == pts.Length - 1)
                    dwell = 6;
                k += dwell;
                AddSample(x, y, z, k);
            }

            // Only the hazards near the walk.
            long minX = long.MaxValue, maxX = long.MinValue, minZ = long.MaxValue, maxZ = long.MinValue;
            for (var i = 0; i < _aheadN; i++)
            {
                minX = Math.Min(minX, _aheadX[i]);
                maxX = Math.Max(maxX, _aheadX[i]);
                minZ = Math.Min(minZ, _aheadZ[i]);
                maxZ = Math.Max(maxZ, _aheadZ[i]);
            }

            bool Near(long px, long pz, long reach) => px >= minX - reach && px <= maxX + reach && pz >= minZ - reach && pz <= maxZ + reach;
            _nearSw.Clear();
            _nearMl.Clear();
            _nearBall.Clear();
            for (var i = 0; i < _hz.Sweepers.Length; i++)
                if (Near(_hz.Sweepers[i].PivotXMm, _hz.Sweepers[i].PivotZMm, _hz.Sweepers[i].ArmMm + 1500))
                    _nearSw.Add(i);
            for (var i = 0; i < _hz.Mallets.Length; i++)
                if (Near(_hz.Mallets[i].PivotXMm, _hz.Mallets[i].PivotZMm, _hz.Mallets[i].ArmMm + _hz.Mallets[i].HeadHalfWidthMm + 1500))
                    _nearMl.Add(i);
            for (var i = 0; i < _hz.RollingBalls.Length; i++)
            {
                ref readonly var d = ref _hz.RollingBalls[i];
                if (Near(Math.Min(d.StartXMm, d.EndXMm), Math.Min(d.StartZMm, d.EndZMm), 3000 + Math.Abs(d.EndXMm - d.StartXMm) + Math.Abs(d.EndZMm - d.StartZMm)))
                    _nearBall.Add(i);
            }

            if (_nearSw.Count + _nearMl.Count + _nearBall.Count == 0)
                return true;
            var capR = MotorState.QuantizeMm(_cfg.Radius);
            for (var i = 0; i < _aheadN; i++)
            {
                var kk = _aheadK[i];
                var lo = Math.Max(0, kk * 8 / 10 - 2 + TimingSlop);
                var hi = Math.Max(lo, kk + 6 - 2 * TimingSlop);
                for (var q = lo; q <= hi; q++)
                {
                    var t = _hz.Time(tick + (uint)q);
                    int sx = (int)_aheadX[i], sy = (int)_aheadY[i], sz = (int)_aheadZ[i];
                    if (t >= _hz.SweeperStartT)
                        foreach (var w in _nearSw)
                            if (HazardMath.SweeperContactMm(in _hz.Sweepers[w], t, sx, sy, sz, r, h, out _, out _))
                                return false;
                    foreach (var w in _nearMl)
                        if (HazardMath.MalletContactMm(in _hz.Mallets[w], t, sx, sy, sz, r, h, out _, out _))
                            return false;
                    foreach (var w in _nearBall)
                        if (BallLaneNear(in _hz.RollingBalls[w], sx, sy, sz, t, capR + 150))
                            return false;
                }
            }

            return true;
        }

        private readonly long[] _aheadX = new long[256], _aheadY = new long[256], _aheadZ = new long[256];
        private readonly int[] _aheadK = new int[256];
        private int _aheadN;
        private readonly List<int> _nearSw = new List<int>(), _nearMl = new List<int>(), _nearBall = new List<int>();

        private void AddSample(long x, long y, long z, int k)
        {
            if (_aheadN >= _aheadX.Length)
                return;
            _aheadX[_aheadN] = x;
            _aheadY[_aheadN] = y;
            _aheadZ[_aheadN] = z;
            _aheadK[_aheadN] = k;
            _aheadN++;
        }

        private static bool BallLaneNear(in RollingBallDef d, int x, int y, int z, int t, int extraMm)
        {
            long reach = d.RadiusMm + extraMm;
            var n = HazardMath.BallMaxAlive(in d);
            for (var j = 0; j < n; j++)
            {
                if (!HazardMath.BallPosMm(in d, t, j, out var bx, out var by, out var bz, out _))
                    break;
                if (Math.Abs(by - (y + 500)) > d.RadiusMm + 900)
                    continue;
                long dx = x - bx, dz = z - bz;
                if (dx * dx + dz * dz < reach * reach)
                    return true;
            }

            return false;
        }

        private static readonly int[] DodgeAngles = { 45, -45, 90, -90, 135, -135, 180 };
        private const int DodgeHorizon = 20;     // ticks (≈ 0.67 s: a ball closing at 9 m/s is ≈ 6 m away)
        private const int DodgeLaneRange = 16000; // mm: lanes farther than this from us are ignored this tick
        private readonly int[] _dodgeX = new int[(DodgeHorizon + 1) * 16];
        private readonly int[] _dodgeZ = new int[(DodgeHorizon + 1) * 16];
        private readonly int[] _dodgeN = new int[DodgeHorizon + 1];
        private readonly int[] _dodgeR = new int[(DodgeHorizon + 1) * 16];
        public int Dodges;

        /// <summary>
        /// Ball reflex (G4.2): if a ball reaches our predicted position within <see cref="DodgeHorizon"/> ticks on the
        /// current heading (or standing, when <paramref name="mx"/>, <paramref name="mz"/> = 0), pick the heading (the plan,
        /// ±45°, ±90°, ±135°, back, or stop) whose first contact is latest, preferring the plan's direction. Pure ball poses
        /// only. Out: the new stick (−127…127); false = keep the plan.
        /// </summary>
        private bool DodgeBalls(in MotorState s, uint tick, int mx, int mz, out int ox, out int oz)
        {
            ox = mx;
            oz = mz;
            // Balls that can matter this tick: lanes whose segment passes within range of us.
            var capR = MotorState.QuantizeMm(_cfg.Radius);
            var any = false;
            for (var k = 0; k <= DodgeHorizon; k++)
                _dodgeN[k] = 0;
            for (var i = 0; i < _hz.RollingBalls.Length; i++)
            {
                ref readonly var d = ref _hz.RollingBalls[i];
                if (SegDist2(s.PosX, s.PosZ, d.StartXMm, d.StartZMm, d.EndXMm, d.EndZMm) > (long)DodgeLaneRange * DodgeLaneRange)
                    continue;
                if (Math.Abs(d.StartYMm - s.PosY) > 9000 && Math.Abs(d.EndYMm - s.PosY) > 9000)
                    continue;
                var n = HazardMath.BallMaxAlive(in d);
                for (var k = 0; k <= DodgeHorizon; k++)
                {
                    var t = _hz.Time(tick + (uint)k);
                    for (var j = 0; j < n && _dodgeN[k] < 16; j++)
                    {
                        if (!HazardMath.BallPosMm(in d, t, j, out var bx, out var by, out var bz, out _))
                            break;
                        if (Math.Abs(by - (s.PosY + 500)) > d.RadiusMm + 1200)
                            continue;
                        var m = k * 16 + _dodgeN[k]++;
                        _dodgeX[m] = bx;
                        _dodgeZ[m] = bz;
                        _dodgeR[m] = d.RadiusMm + capR + 150;
                        any = true;
                    }
                }
            }

            if (!any)
                return false;
            double px = mx, pz = mz;
            var pl = Math.Sqrt(px * px + pz * pz);
            var hasPlan = pl > 1;
            var planHit = FirstHit(in s, hasPlan ? px / pl : 0, hasPlan ? pz / pl : 0, !hasPlan);
            if (planHit > DodgeHorizon)
                return false;
            // Reference heading for the candidates: the plan, else straight up the course (+z).
            double ux = hasPlan ? px / pl : 0, uz = hasPlan ? pz / pl : 1;
            var bestScore = planHit * 100 + (hasPlan ? 40 : 0);
            double bestX = hasPlan ? ux : 0, bestZ = hasPlan ? uz : 0;
            var stopHit = hasPlan ? FirstHit(in s, 0, 0, true) : planHit;
            if (stopHit * 100 + 10 > bestScore)
            {
                bestScore = stopHit * 100 + 10;
                bestX = bestZ = 0;
            }

            // Odd slots try the right side first, even slots the left (a fixed order sent every bot of a left-hand line
            // toward the rail, QA: 1.38× on that line).
            var flip = (Slot & 1) == 1 ? -1 : 1;
            foreach (var deg in DodgeAngles)
            {
                var a = flip * deg * Math.PI / 180.0;
                double cx = ux * Math.Cos(a) - uz * Math.Sin(a), cz = ux * Math.Sin(a) + uz * Math.Cos(a);
                var hit = FirstHit(in s, cx, cz, false);
                var score = hit * 100 + (int)(Math.Cos(a) * 30);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = cx;
                    bestZ = cz;
                }
            }

            ox = (int)Math.Round(bestX * 127);
            oz = (int)Math.Round(bestZ * 127);
            return ox != mx || oz != mz;
        }

        /// <summary>First tick (1…horizon) a ball of the cached set reaches us on heading (hx, hz) (unit; stop = brake), horizon + 1 = never.</summary>
        private int FirstHit(in MotorState s, double hx, double hz, bool stop)
        {
            var horizon = Math.Max(6, DodgeHorizon - 2 * TimingSlop); // G4.2: looser tiers see a ball later
            // Velocity (mm per tick) blends from the current one to the heading at full speed over ~5 ticks (accel 28 m/s²).
            double vx = s.VelX * 10.0 / HazardMath.TickRate, vz = s.VelZ * 10.0 / HazardMath.TickRate;
            var top = stop ? 0 : RunPerTick;
            double x = s.PosX, z = s.PosZ;
            for (var k = 1; k <= horizon; k++)
            {
                var a = Math.Min(1.0, k / 5.0);
                var cvx = vx + (hx * top - vx) * a;
                var cvz = vz + (hz * top - vz) * a;
                x += cvx;
                z += cvz;
                for (var m = k * 16; m < k * 16 + _dodgeN[k]; m++)
                {
                    double dx = x - _dodgeX[m], dz = z - _dodgeZ[m];
                    if (dx * dx + dz * dz < (double)_dodgeR[m] * _dodgeR[m])
                        return k;
                }
            }

            return DodgeHorizon + 1;
        }

        private static long SegDist2(long px, long pz, long ax, long az, long bx, long bz)
        {
            long ux = bx - ax, uz = bz - az;
            var l2 = ux * ux + uz * uz;
            var f = l2 > 0 ? Math.Max(0.0, Math.Min(1.0, (double)((px - ax) * ux + (pz - az) * uz) / l2)) : 0.0;
            var cx = ax + ux * f - px;
            var cz = az + uz * f - pz;
            return (long)(cx * cx + cz * cz);
        }

        public int Jumps, Dives;
        public int WaitTimeouts, RunUps;
        private int _runUpWp = -1, _runUpX, _runUpZ;
        private uint _runUpUntil;
        /// <summary>
        /// G4.2: how loosely the bot judges a look-ahead WaitClear window (0 = reference: the full margins; BotBrain sets it
        /// per tier). Each step trims the window by 1 tick at the front and 2 at the back and the reach margin by 30 mm:
        /// a looser bot goes in tighter gaps and gets clipped now and then, as players do.
        /// </summary>
        public int TimingSlop;
        /// <summary>G4.2: the tick a look-ahead WaitClear said "go" (BotBrain's tiers may hesitate there, a mistimed crossing).</summary>
        public uint LookAheadGoTick = uint.MaxValue;
        private const int WaitTimeoutTicks = 240;
        private uint _waitSince;
        private int _waitWp = -1;

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
