using FallRush.Shared;

namespace FallRush.Net
{
    /// <summary>
    /// MEASUREMENT TOOL: a scripted "player" that runs the Map_Sandbox hazard course through the real client path, so the
    /// gate numbers (corrections while running the course) are reproducible. It steers from the <b>predicted</b> local
    /// state and the hazard poses at the predicted tick — exactly what a human sees.
    /// <para>Loop (z = −28): run west through the sweeper (jumping the bar when it is about to hit) → wait at the −X edge
    /// → jump onto the moving platform → ride → jump to the island → ride back → run east through the sweeper → repeat.
    /// Falling out restarts it from the spawn.</para>
    /// </summary>
    public sealed class CourseBot : IInputSource
    {
        // Course geometry (SandboxBuilder): lane z, main edge x = −40, island edge x = −52.
        private const float LaneZ = -28f;
        private const float EastX = -18f;
        private const float MainEdgeX = -39.7f;
        private const float IslandEdgeX = -52.4f;
        private const float IslandX = -55f;
        private const float NearMainCenterX = -43.1f;   // platform at its east extreme (≈ 1 m gap)
        private const float NearIslandCenterX = -48.9f; // platform at its west extreme

        public enum Phase : byte
        {
            ToStart, RunWest, TakeHit, WaitEdge, BoardWest, RideWest, ToIsland, IslandWait, BoardEast, RideEast, ToMain, RunEast,
        }

        private readonly ClientSim _sim;
        private float _laneZ = LaneZ;
        private bool _tookHit;
        private const float SweeperLaneX = -30f; // where the bar crosses the lane (pivot x)
        private uint _jumpUntil;
        private uint _phaseTick;

        public CourseBot(ClientSim sim)
        {
            _sim = sim;
        }

        public Phase Current { get; private set; } = Phase.ToStart;
        public int Laps { get; private set; }
        public int Falls { get; private set; }

        public void Sample(ref InputCmd cmd)
        {
            var pred = _sim.Prediction;
            if (pred == null || !pred.HasState)
                return;
            var s = pred.State;
            var tick = cmd.Tick;
            if (s.Has(MotorState.Respawning))
            {
                if (Current != Phase.ToStart)
                {
                    Falls++;
                    Set(Phase.ToStart, tick);
                }

                return;
            }

            if (_sim.OwnSlot != SnapHeader.NoSlot)
                _laneZ = LaneZ + ((_sim.OwnSlot % 3) - 1) * 0.9f; // three lanes 0.9 m apart (capsules are 0.62 m wide)
            var p = s.Position;
            var grounded = s.Has(MotorState.Grounded);
            var onPlatform = grounded && s.Surface != 0;
            var hz = pred.Hazards;
            var platX = hz.Platforms.Length > 0 ? HazardMath.PlatformCenter(in hz.Platforms[0], tick).X : -46f;

            float tx = p.X, tz = _laneZ;
            var move = true;
            var jump = false;

            switch (Current)
            {
                case Phase.ToStart:
                    tx = EastX;
                    if (Near(p, EastX, _laneZ, 1.2f))
                        Set(Phase.RunWest, tick);
                    break;
                case Phase.RunWest:
                    tx = MainEdgeX;
                    jump = SweeperAhead(pred, in s, tick);
                    // Odd laps stop in the bar's path and wait to be hit: exercises predicted knockback + stun.
                    if ((Laps & 1) == 1 && p.X <= SweeperLaneX && !_tookHit)
                        Set(Phase.TakeHit, tick);
                    if (p.X <= MainEdgeX + 0.4f)
                        Set(Phase.WaitEdge, tick);
                    break;
                case Phase.TakeHit:
                    tx = SweeperLaneX;
                    move = System.Math.Abs(p.X - SweeperLaneX) > 0.3f && s.StunTicks == 0;
                    if (s.StunTicks > 0)
                        _tookHit = true;
                    if ((_tookHit && s.StunTicks == 0) || tick - _phaseTick > 240)
                    {
                        _tookHit = true;
                        Set(Phase.RunWest, tick);
                    }

                    break;
                case Phase.WaitEdge:
                    tx = MainEdgeX;
                    move = p.X > MainEdgeX + 0.15f;
                    if (grounded && platX >= NearMainCenterX)
                    {
                        jump = true;
                        Set(Phase.BoardWest, tick);
                    }

                    break;
                case Phase.BoardWest:
                    tx = platX;
                    if (onPlatform)
                        Set(Phase.RideWest, tick);
                    break;
                case Phase.RideWest:
                    tx = platX;
                    move = System.Math.Abs(p.X - platX) > 0.3f || System.Math.Abs(p.Z - _laneZ) > 0.3f;
                    if (onPlatform && platX <= NearIslandCenterX)
                    {
                        tx = IslandX;
                        jump = true;
                        Set(Phase.ToIsland, tick);
                    }

                    break;
                case Phase.ToIsland:
                    tx = IslandX;
                    if (grounded && !onPlatform && p.X < IslandEdgeX - 0.5f)
                        Set(Phase.IslandWait, tick);
                    break;
                case Phase.IslandWait:
                    tx = IslandEdgeX;
                    move = p.X < IslandEdgeX - 0.15f;
                    if (grounded && platX <= NearIslandCenterX && tick - _phaseTick > 15)
                    {
                        tx = platX;
                        jump = true;
                        Set(Phase.BoardEast, tick);
                    }

                    break;
                case Phase.BoardEast:
                    tx = platX;
                    if (onPlatform)
                        Set(Phase.RideEast, tick);
                    break;
                case Phase.RideEast:
                    tx = platX;
                    move = System.Math.Abs(p.X - platX) > 0.3f || System.Math.Abs(p.Z - _laneZ) > 0.3f;
                    if (onPlatform && platX >= NearMainCenterX)
                    {
                        tx = MainEdgeX + 2f;
                        jump = true;
                        Set(Phase.ToMain, tick);
                    }

                    break;
                case Phase.ToMain:
                    tx = MainEdgeX + 3f;
                    if (grounded && !onPlatform && p.X > MainEdgeX + 0.5f)
                        Set(Phase.RunEast, tick);
                    break;
                case Phase.RunEast:
                    tx = EastX;
                    jump = SweeperAhead(pred, in s, tick);
                    if (p.X >= EastX - 0.5f)
                    {
                        Laps++;
                        _tookHit = false;
                        Set(Phase.RunWest, tick);
                    }

                    break;
            }

            // A phase stuck for 20 s (missed the platform and stood somewhere odd): start over.
            if (tick - _phaseTick > 600)
                Set(Phase.ToStart, tick);

            if (move)
            {
                var dx = tx - p.X;
                var dz = tz - p.Z;
                var l = (float)System.Math.Sqrt(dx * dx + dz * dz);
                if (l > 0.05f)
                {
                    cmd.MoveX = (sbyte)System.Math.Round(dx / l * 127f);
                    cmd.MoveY = (sbyte)System.Math.Round(dz / l * 127f);
                }
            }

            if (jump && grounded && tick >= _jumpUntil)
            {
                cmd.Buttons |= InputCmd.JumpPressed | InputCmd.JumpHeld;
                _jumpUntil = tick + 8;
            }
            else if (tick < _jumpUntil)
            {
                cmd.Buttons |= InputCmd.JumpHeld;
            }

            InputSanitizer.Sanitize(ref cmd);
        }

        private void Set(Phase p, uint tick)
        {
            Current = p;
            _phaseTick = tick;
        }

        private static bool Near(Vec3 p, float x, float z, float r) => (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z) < r * r;

        /// <summary>Would the bar hit us within the next ~0.2 s if we kept running? Then jump now.</summary>
        private static bool SweeperAhead(ClientPrediction pred, in MotorState s, uint tick)
        {
            var hz = pred.Hazards;
            if (hz.Sweepers.Length == 0 || s.ImmunityTicks > 0)
                return false;
            var cfg = pred.Cfg;
            var p = s.Position;
            var v = s.Velocity;
            for (var k = 2; k <= 7; k++)
            {
                var t = k * cfg.Dt;
                var q = new Vec3(p.X + v.X * t, p.Y, p.Z + v.Z * t);
                if (HazardMath.SweeperContact(in hz.Sweepers[0], tick + (uint)k, q, cfg.Radius, cfg.Height, out _, out _))
                    return true;
            }

            return false;
        }
    }
}
