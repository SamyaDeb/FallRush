using System;

namespace FallRush.Shared
{
    /// <summary>
    /// Authoritative per-player motor state. Blittable and integer-only: position in millimetres and velocity in cm/s,
    /// which is exactly the wire precision (doc 04 §4.1). <see cref="KinematicMotor.Step"/> quantises at the end of
    /// every step, so the state the server keeps simulating is bit-for-bit the state it sends.
    /// <para><c>Pos</c> is the capsule's bottom point (feet).</para>
    /// </summary>
    public struct MotorState : IEquatable<MotorState>
    {
        // Flags (u8 on the wire).
        public const byte Grounded = 1 << 0;
        public const byte Diving = 1 << 1;
        public const byte DiveLanding = 1 << 2;   // ground stun after a dive landing (LandTicks > 0)
        public const byte Respawning = 1 << 3;    // fell out; frozen until the server teleports to a spawn
        public const byte Stumbling = 1 << 4;     // G1: hard landing (fall > 3 m); LandTicks counts it down, input stays live at reduced speed
        public const byte Bot = 1 << 7;           // display only, set by the server for test bots

        public int PosX, PosY, PosZ;        // mm
        public short VelX, VelY, VelZ;      // cm/s
        public byte Yaw;                    // facing, 256 steps (Yaw256)
        public byte Flags;
        public byte StunTicks;              // hazard stun (M2b); no input while > 0

        // Timers. Sent only to the owning client (snapshot header) so a rewind restores the full state.
        public byte CoyoteTicks;
        public byte JumpBufferTicks;
        public byte DiveCooldownTicks;
        public byte LandTicks;              // dive-landing belly slide (DiveLanding) or stumble (Stumbling) remaining
        public byte ImmunityTicks;          // post-hit immunity (M2b hazards)
        public byte Surface;                // moving surface stood on at the end of the last step: 0 = static/none, else SurfaceId + 1

        public bool Has(byte flag) => (Flags & flag) != 0;

        public void Set(byte flag, bool on)
        {
            if (on)
                Flags |= flag;
            else
                Flags = (byte)(Flags & ~flag);
        }

        public Vec3 Position
        {
            get => new Vec3(PosX * 0.001f, PosY * 0.001f, PosZ * 0.001f);
            set
            {
                PosX = QuantizeMm(value.X);
                PosY = QuantizeMm(value.Y);
                PosZ = QuantizeMm(value.Z);
            }
        }

        public Vec3 Velocity
        {
            get => new Vec3(VelX * 0.01f, VelY * 0.01f, VelZ * 0.01f);
            set
            {
                VelX = QuantizeCm(value.X);
                VelY = QuantizeCm(value.Y);
                VelZ = QuantizeCm(value.Z);
            }
        }

        /// <summary>Round half away from zero to 1 mm.</summary>
        public static int QuantizeMm(float metres)
        {
            var v = metres * 1000f;
            return v >= 0f ? (int)(v + 0.5f) : -(int)(-v + 0.5f);
        }

        /// <summary>Round half away from zero to 1 cm/s, saturating at ±327.67 m/s.</summary>
        public static short QuantizeCm(float metresPerSecond)
        {
            var v = metresPerSecond * 100f;
            var i = v >= 0f ? (int)(v + 0.5f) : -(int)(-v + 0.5f);
            if (i > short.MaxValue)
                i = short.MaxValue;
            if (i < -short.MaxValue)
                i = -short.MaxValue;
            return (short)i;
        }

        public bool Equals(MotorState o) =>
            PosX == o.PosX && PosY == o.PosY && PosZ == o.PosZ &&
            VelX == o.VelX && VelY == o.VelY && VelZ == o.VelZ &&
            Yaw == o.Yaw && Flags == o.Flags && StunTicks == o.StunTicks &&
            CoyoteTicks == o.CoyoteTicks && JumpBufferTicks == o.JumpBufferTicks &&
            DiveCooldownTicks == o.DiveCooldownTicks && LandTicks == o.LandTicks && ImmunityTicks == o.ImmunityTicks && Surface == o.Surface;

        public override bool Equals(object obj) => obj is MotorState s && Equals(s);

        public override int GetHashCode() => PosX ^ (PosY << 7) ^ (PosZ << 13) ^ (VelX << 3) ^ (VelY << 11) ^ (VelZ << 17) ^ (Flags << 24);

        public override string ToString() =>
            $"pos=({PosX},{PosY},{PosZ})mm vel=({VelX},{VelY},{VelZ})cm/s yaw={Yaw} flags=0x{Flags:X2} stun={StunTicks} coyote={CoyoteTicks} buf={JumpBufferTicks} diveCd={DiveCooldownTicks} land={LandTicks} imm={ImmunityTicks} surf={Surface}";
    }
}
