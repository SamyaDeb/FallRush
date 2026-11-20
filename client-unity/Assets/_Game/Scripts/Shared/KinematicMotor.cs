using System;

namespace FallRush.Shared
{
    /// <summary>
    /// The shared kinematic capsule motor (doc 04 §5, GDD §7). The same code runs on the server (authoritative) and in
    /// client prediction. <see cref="Step"/> is a pure function of (state, input, dt, world): no Time, no Random, no
    /// frame-rate dependence, no trigonometry (facing uses <see cref="Yaw256"/>). Collision goes through
    /// <see cref="IMotorWorld"/>.
    /// <para>Order of one step: depenetrate → input, timers, stun / dive-landing → facing → jump (buffer + coyote) →
    /// dive → horizontal accel/decel/air control → gravity (exact per-step integration) → collide-and-slide with
    /// step-up → ground probe + snap → landing transitions → quantise to mm / cm/s.</para>
    /// <para>G1 (movement v2): a dive landing is a <b>belly slide</b> (no input, slide deceleration, jump-cancel in the
    /// first ticks); a hard landing (fall &gt; 3 m) is a <b>stumble</b> (input live, speed capped, a jump ends it). Both
    /// count down <see cref="MotorState.LandTicks"/>, told apart by the <see cref="MotorState.DiveLanding"/> /
    /// <see cref="MotorState.Stumbling"/> flag, so the snapshot layout is unchanged.</para>
    /// </summary>
    public sealed class KinematicMotor
    {
        /// <summary>What happened in the last <see cref="Step"/> (diagnostics / visuals; not part of the state).</summary>
        public struct StepEvents
        {
            public bool Jumped;
            public bool Dived;
            public bool Landed;
            public bool SteppedUp;
            public bool JumpCancelled;
            public bool Carried;
            public bool Inherited;
            /// <summary>G1: this step's landing was a hard one (fall &gt; stumble.minFallHeight): the stumble started.</summary>
            public bool Stumbled;
        }

        public readonly MovementConfig Cfg;
        public StepEvents Last;

        // Ray used to read the face normal under an edge contact (see IsWalkableContact).
        private const float EdgeProbeInset = 0.02f;
        private const float EdgeProbeRise = 0.05f;

        public KinematicMotor(MovementConfig cfg)
        {
            Cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        }

        public void Step(ref MotorState s, in InputCmd cmd, float dt, IMotorWorld world)
        {
            Last = default;
            if (s.Has(MotorState.Respawning))
                return;

            var c = Cfg;
            var pos = s.Position;
            var vel = s.Velocity;
            var wasGrounded = s.Has(MotorState.Grounded);

            Depenetrate(ref pos, world);

            // ---- surface carry: ride the surface we stood on at the end of the last tick (platform, turn table, belt) ----
            var carry = Vec3.Zero;
            if (wasGrounded && s.Surface != 0 && world.SurfaceDelta(s.Surface - 1, pos, out carry) && carry.SqrMagnitude > 0f)
            {
                var v0 = vel;
                SlideMove(ref pos, ref v0, carry, groundedMode: true, allowStep: false, world);
                Last.Carried = true;
            }
            else
            {
                carry = Vec3.Zero;
            }

            // ---- input ----
            float wx = cmd.MoveX / 127f, wz = cmd.MoveY / 127f;
            var wl2 = wx * wx + wz * wz;
            if (wl2 > 1f)
            {
                var inv = 1f / (float)Math.Sqrt(wl2);
                wx *= inv;
                wz *= inv;
            }

            var hasMove = cmd.MoveX != 0 || cmd.MoveY != 0;
            var jumpPress = cmd.Has(InputCmd.JumpPressed);
            var divePress = cmd.Has(InputCmd.DivePressed);

            if (s.DiveCooldownTicks > 0)
                s.DiveCooldownTicks--;
            if (s.ImmunityTicks > 0)
                s.ImmunityTicks--;

            var stunned = s.StunTicks > 0;
            if (stunned)
            {
                s.StunTicks--;
                wx = wz = 0f;
                hasMove = jumpPress = divePress = false;
            }

            var cancelJump = false;
            var landing = s.LandTicks > 0 && s.Has(MotorState.DiveLanding);
            var stumbling = s.LandTicks > 0 && s.Has(MotorState.Stumbling);
            if (landing && !wasGrounded)
            {
                // Slid off an edge: the belly slide ends, air control returns.
                EndLanding(ref s);
                landing = false;
            }

            if (landing)
            {
                var sinceLanding = c.DiveLandStunTicks - s.LandTicks; // 0 on the first tick after touching down
                if (jumpPress && sinceLanding < c.DiveCancelWindowTicks)
                {
                    EndLanding(ref s);
                    cancelJump = true;
                    landing = false;
                }
                else
                {
                    s.LandTicks--;
                    if (s.LandTicks == 0)
                        s.Set(MotorState.DiveLanding, false);
                    wx = wz = 0f;
                    hasMove = jumpPress = divePress = false;
                }
            }
            else if (stumbling)
            {
                s.LandTicks--;
                if (s.LandTicks == 0)
                    s.Set(MotorState.Stumbling, false);
            }
            else if (s.LandTicks > 0 || s.Has(MotorState.Stumbling))
            {
                EndLanding(ref s); // inconsistent (e.g. a timer without its flag): clear
            }

            var diving = s.Has(MotorState.Diving);
            if (hasMove && !diving)
                s.Yaw = Yaw256.FromVector(cmd.MoveX, cmd.MoveY, s.Yaw);

            // ---- jump (buffer + coyote; jump-cancel out of a dive landing) ----
            if (jumpPress)
                s.JumpBufferTicks = (byte)c.JumpBufferTicks;

            var jumped = false;
            var canGroundJump = wasGrounded || s.CoyoteTicks > 0;
            if (cancelJump || (!diving && !landing && !stunned && s.JumpBufferTicks > 0 && canGroundJump))
            {
                vel.Y = c.JumpSpeed;
                if (s.Has(MotorState.Stumbling))
                    EndLanding(ref s); // hopping out of a stumble ends it
                s.JumpBufferTicks = 0;
                s.CoyoteTicks = 0;
                jumped = true;
                Last.Jumped = true;
                Last.JumpCancelled = cancelJump;
            }

            // ---- dive ----
            var dived = false;
            if (!jumped && divePress && !diving && !landing && !stunned && s.DiveCooldownTicks == 0)
            {
                if (!hasMove)
                    s.Yaw = Yaw256.FromCamYaw(cmd.CamYaw);
                Yaw256.Dir(s.Yaw, out var dx, out var dz);
                var along = vel.X * dx + vel.Z * dz;
                var hs = along > c.DiveHorizontal ? along : c.DiveHorizontal;
                vel.X = dx * hs;
                vel.Z = dz * hs;
                vel.Y = c.DiveVertical;
                s.Set(MotorState.Diving, true);
                s.DiveCooldownTicks = (byte)c.DiveCooldownTicks;
                s.JumpBufferTicks = 0;
                s.CoyoteTicks = 0;
                diving = dived = true;
                Last.Dived = true;
                if (s.Has(MotorState.Stumbling))
                    EndLanding(ref s);
            }

            // Jump-off inheritance (M3): leaving a moving surface by a jump or a dive keeps its horizontal velocity
            // (platform, turn-table rim, belt). Walking off an edge does not. Deterministic, so prediction stays exact.
            if ((jumped || dived) && Last.Carried)
            {
                vel.X += carry.X / dt;
                vel.Z += carry.Z / dt;
                Last.Inherited = true;
            }

            var grounded = wasGrounded && !jumped && !dived;

            // ---- horizontal velocity ----
            float vx = vel.X, vz = vel.Z;
            if (diving)
            {
                // Committed: no steering, no friction in the air.
            }
            else if (grounded)
            {
                if (landing)
                    MoveTowards(ref vx, ref vz, 0f, 0f, c.SlideDecel * dt); // belly slide
                else if (wx == 0f && wz == 0f)
                    MoveTowards(ref vx, ref vz, 0f, 0f, c.GroundDecel * dt);
                else
                {
                    var top = stumbling ? c.MaxSpeed * c.StumbleSpeedFactor : c.MaxSpeed; // incl. the stumble's last tick
                    MoveTowards(ref vx, ref vz, wx * top, wz * top, c.GroundAccel * dt);
                }
            }
            else if (wx != 0f || wz != 0f)
            {
                // Air control steers; it never brakes a launch, a knockback exit or a carried jump below its speed (M3):
                // above the run speed the target keeps the current speed, so holding forward never shortens a spring hop.
                var target = c.MaxSpeed;
                var cur2 = vx * vx + vz * vz;
                if (cur2 > target * target)
                    target = (float)Math.Sqrt(cur2);
                MoveTowards(ref vx, ref vz, wx * target, wz * target, c.AirAccel * dt);
            }

            // ---- vertical: exact integration under constant gravity for the step ----
            float dy;
            if (grounded)
            {
                vel.Y = 0f;
                dy = 0f;
            }
            else
            {
                var g = c.Gravity * (vel.Y <= 0f ? c.FallMultiplier : 1f);
                dy = vel.Y * dt - 0.5f * g * dt * dt;
                vel.Y -= g * dt;
                if (vel.Y < -c.MaxFallSpeed)
                    vel.Y = -c.MaxFallSpeed;
            }

            vel.X = vx;
            vel.Z = vz;

            var fallSpeed = -vel.Y; // before the move and the ground snap zero it (stumble check)

            // ---- move ----
            SlideMove(ref pos, ref vel, new Vec3(vx * dt, dy, vz * dt), grounded, allowStep: grounded, world);

            // ---- ground probe + snap ----
            var groundedNow = false;
            var surface = 0;
            if (!jumped && !dived && vel.Y <= 0f)
            {
                var probe = (wasGrounded ? c.SnapDistance : 0f) + c.Skin * 2f;
                // Edge contacts (sphere resting on a corner) count as ground only if the face is not too far above the
                // feet: up to the step offset while walking (you could step onto it anyway), but only the ledge tolerance
                // when landing from the air — otherwise a 1.6 m jump could "stand" on the corner of a 1.7 m wall and step up.
                var maxRise = wasGrounded ? c.StepOffset : c.LedgeTolerance;
                if (world.CapsuleSweep(pos, c.Radius, c.Height, Vec3.Down, probe + c.Skin, out var g) &&
                    IsWalkableContact(in g, pos, world, out var faceY) &&
                    faceY - (pos.Y - (g.Distance - c.Skin)) <= maxRise)
                {
                    pos.Y -= g.Distance - c.Skin;
                    groundedNow = true;
                    vel.Y = 0f;
                    surface = g.SurfaceId >= 0 && g.SurfaceId < 255 ? g.SurfaceId + 1 : 0;
                }
            }

            // ---- transitions ----
            if (groundedNow && !wasGrounded)
            {
                Last.Landed = true;
                if (s.Has(MotorState.Diving))
                {
                    s.Set(MotorState.Diving, false);
                    s.Set(MotorState.Stumbling, false);
                    s.Set(MotorState.DiveLanding, true);
                    s.LandTicks = (byte)c.DiveLandStunTicks;
                    s.JumpBufferTicks = 0;
                }
                else if (fallSpeed > c.StumbleImpactSpeed && c.StumbleTicks > 0)
                {
                    s.Set(MotorState.Stumbling, true);
                    s.LandTicks = (byte)c.StumbleTicks;
                    Last.Stumbled = true;
                }
            }

            if (groundedNow)
                s.CoyoteTicks = (byte)c.CoyoteTicks;
            else if (!wasGrounded && s.CoyoteTicks > 0)
                s.CoyoteTicks--;

            if (!jumpPress && s.JumpBufferTicks > 0)
                s.JumpBufferTicks--;

            s.Set(MotorState.Grounded, groundedNow);
            s.Surface = (byte)surface;
            s.Position = pos;
            s.Velocity = vel;
        }

        private static void EndLanding(ref MotorState s)
        {
            s.LandTicks = 0;
            s.Set(MotorState.DiveLanding, false);
            s.Set(MotorState.Stumbling, false);
        }

        /// <summary>
        /// Server-only external displacement (player–player separation). Collides and slides, never steps up, keeps velocity.
        /// </summary>
        public void Displace(ref MotorState s, Vec3 displacement, IMotorWorld world)
        {
            if (s.Has(MotorState.Respawning))
                return;
            var pos = s.Position;
            var vel = s.Velocity;
            SlideMove(ref pos, ref vel, displacement, groundedMode: true, allowStep: false, world);
            s.Position = pos;
        }

        private void Depenetrate(ref Vec3 pos, IMotorWorld world)
        {
            for (var i = 0; i < 2; i++)
            {
                if (!world.ComputePenetration(pos, Cfg.Radius, Cfg.Height, out var corr))
                    return;
                pos += corr;
            }
        }

        private void SlideMove(ref Vec3 pos, ref Vec3 vel, Vec3 disp, bool groundedMode, bool allowStep, IMotorWorld world)
        {
            var c = Cfg;
            var stepped = false;
            for (var i = 0; i < c.SlideIterations; i++)
            {
                var len2 = disp.SqrMagnitude;
                if (len2 < 1e-10f)
                    return;
                var len = (float)Math.Sqrt(len2);
                var dir = disp / len;

                if (!world.CapsuleSweep(pos, c.Radius, c.Height, dir, len + c.Skin, out var hit))
                {
                    pos += disp;
                    return;
                }

                var travel = hit.Distance - c.Skin;
                if (travel < 0f)
                    travel = 0f;
                if (travel > len)
                    travel = len;
                pos += dir * travel;
                var rem = disp - dir * travel;
                var n = hit.Normal;

                if (n.Y < c.MaxSlopeCos)
                {
                    if (allowStep && !stepped && n.Y > -0.01f && TryStepUp(ref pos, rem, world))
                    {
                        stepped = true;
                        Last.SteppedUp = true;
                        return;
                    }

                    // Too steep to walk: slide along it horizontally (never climb it) when on the ground or moving up.
                    if (n.Y > 0f && (groundedMode || disp.Y >= 0f))
                    {
                        var h = new Vec3(n.X, 0f, n.Z).Normalized;
                        if (h.SqrMagnitude > 0f)
                            n = h;
                    }
                }

                rem -= n * Vec3.Dot(rem, n);
                var vn = Vec3.Dot(vel, n);
                if (vn < 0f && !(groundedMode && n.Y >= c.MaxSlopeCos))
                    vel -= n * vn;
                disp = rem;
            }
        }

        /// <summary>Up by the step offset, forward by the blocked remainder, down onto walkable ground no higher than the step offset.</summary>
        private bool TryStepUp(ref Vec3 pos, Vec3 rem, IMotorWorld world)
        {
            var c = Cfg;
            var fwd = rem.Horizontal;
            var fl = fwd.Magnitude;
            if (fl < 1e-4f)
                return false;
            var fdir = fwd / fl;

            var up = c.StepOffset;
            if (world.CapsuleSweep(pos, c.Radius, c.Height, Vec3.Up, up + c.Skin, out var h1))
                up = h1.Distance - c.Skin;
            if (up < 0.01f)
                return false;
            var p = pos + Vec3.Up * up;

            if (world.CapsuleSweep(p, c.Radius, c.Height, fdir, fl + c.Skin, out var h2))
            {
                var t = h2.Distance - c.Skin;
                if (t < 0.001f)
                    return false;
                p += fdir * t;
            }
            else
            {
                p += fwd;
            }

            if (!world.CapsuleSweep(p, c.Radius, c.Height, Vec3.Down, up + c.Skin, out var h3))
                return false;
            if (!IsWalkableContact(in h3, p, world, out var faceY))
                return false;
            var footBefore = pos.Y - c.Skin;
            if (faceY - footBefore > c.StepOffset + 0.001f)
                return false;

            p.Y -= h3.Distance - c.Skin;
            if (p.Y - pos.Y < 0.001f)
                return false;
            pos = p;
            return true;
        }

        /// <summary>
        /// A downward contact is walkable if its normal is within the slope limit, or — for edge contacts, where the
        /// sweep reports the impact normal — if the face just past the edge is. <paramref name="faceY"/> is the height of that face.
        /// </summary>
        private bool IsWalkableContact(in MotorHit hit, Vec3 feet, IMotorWorld world, out float faceY)
        {
            var c = Cfg;
            faceY = hit.Point.Y;
            if (hit.Normal.Y >= c.MaxSlopeCos)
                return true;
            if (hit.Normal.Y <= 0.01f)
                return false;

            var away = new Vec3(hit.Point.X - feet.X, 0f, hit.Point.Z - feet.Z).Normalized;
            var origin = hit.Point + away * EdgeProbeInset + Vec3.Up * EdgeProbeRise;
            if (world.Raycast(origin, Vec3.Down, EdgeProbeRise * 2f, out var rh) && rh.Normal.Y >= c.MaxSlopeCos)
            {
                faceY = rh.Point.Y;
                return true;
            }

            return false;
        }

        private static void MoveTowards(ref float x, ref float z, float tx, float tz, float maxDelta)
        {
            float dx = tx - x, dz = tz - z;
            var d2 = dx * dx + dz * dz;
            if (d2 <= maxDelta * maxDelta)
            {
                x = tx;
                z = tz;
                return;
            }

            var k = maxDelta / (float)Math.Sqrt(d2);
            x += dx * k;
            z += dz * k;
        }
    }
}
