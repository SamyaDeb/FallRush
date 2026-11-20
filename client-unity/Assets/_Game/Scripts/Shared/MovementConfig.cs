using System;
using System.Text;
using D = FallRush.Shared.MovementConfigData;

namespace FallRush.Shared
{
    /// <summary>
    /// Runtime movement constants (GDD §7, doc 04 §5), built from <see cref="MovementConfigData"/>, which is generated
    /// from <c>packages/shared/config/movement_v1.json</c>. Integers in the JSON (mm, mm/s, ms, permille) become floats
    /// in metres/seconds here; times become ticks with round-half-up at the 30 Hz tick.
    /// </summary>
    public sealed class MovementConfig
    {
        public static readonly MovementConfig Default = new MovementConfig(ModifierEffects.Get(ModifierEffects.NoneId));
        private static readonly MovementConfig[] ByModifier = new MovementConfig[16];

        /// <summary>
        /// The motor constants under a modifier (MoonBounce: gravity ×0.7, apex ×1.25; ButterFloors: ground decel ×0.4).
        /// Scaled in integer mm before the float conversion, so the server and every client build the same floats.
        /// Modifiers that do not touch the motor return <see cref="Default"/>.
        /// </summary>
        public static MovementConfig For(int modifierId)
        {
            var m = ModifierEffects.Get(modifierId);
            if (!m.AffectsMotor)
                return Default;
            if (m.Id < 0 || m.Id >= ByModifier.Length)
                return new MovementConfig(m);
            return ByModifier[m.Id] ??= new MovementConfig(m);
        }

        /// <summary>The modifier this config was built for (0 = None).</summary>
        public readonly int ModifierId;

        public readonly int TickRate = D.TickRateHz;
        public readonly float Dt;

        public readonly float Radius, Height, Skin;
        public readonly float MaxSpeed, GroundAccel, GroundDecel, AirAccel;
        public readonly float Gravity, FallMultiplier, MaxFallSpeed;
        public readonly float JumpApex, JumpSpeed;
        public readonly float DiveHorizontal, DiveVertical;
        public readonly float MaxSlopeCos, StepOffset, SnapDistance, LedgeTolerance;
        public readonly int SlideIterations;
        public readonly float PushMaxSpeed, KillY;

        public readonly int CoyoteTicks, JumpBufferTicks, DiveCooldownTicks, DiveLandStunTicks, DiveCancelWindowTicks, RespawnTicks;

        private MovementConfig(in ModifierEffects m)
        {
            ModifierId = m.AffectsMotor ? m.Id : ModifierEffects.NoneId;
            Dt = 1f / TickRate;
            Radius = Mm(D.Capsule_Radius);
            Height = Mm(D.Capsule_Height);
            Skin = Mm(D.Capsule_Skin);
            MaxSpeed = Mm(D.Run_MaxSpeed);
            GroundAccel = Mm(D.Run_GroundAccel);
            GroundDecel = Mm(Scale(D.Run_GroundDecel, m.GroundDecelPermille));
            AirAccel = GroundAccel * (D.Run_AirControlPermille / 1000f);
            Gravity = Mm(Scale(D.Gravity_Accel, m.GravityPermille));
            FallMultiplier = D.Gravity_FallMultiplierPermille / 1000f;
            MaxFallSpeed = Mm(D.Gravity_MaxFallSpeed);
            JumpApex = Mm(Scale(D.Jump_ApexHeight, m.JumpApexPermille));
            JumpSpeed = (float)Math.Sqrt(2f * Gravity * JumpApex);
            DiveHorizontal = Mm(D.Dive_HorizontalSpeed);
            DiveVertical = Mm(D.Dive_VerticalSpeed);
            MaxSlopeCos = (float)Math.Cos(D.Ground_MaxSlopeDeg * Math.PI / 180.0);
            StepOffset = Mm(D.Ground_StepOffset);
            SnapDistance = Mm(D.Ground_SnapDistance);
            LedgeTolerance = Mm(D.Ground_LedgeTolerance);
            SlideIterations = D.Ground_SlideIterations;
            PushMaxSpeed = Mm(D.Push_MaxSpeed);
            KillY = Mm(D.Respawn_KillY);

            CoyoteTicks = MsToTicks(D.Jump_CoyoteMs, TickRate);
            JumpBufferTicks = MsToTicks(D.Jump_BufferMs, TickRate);
            DiveCooldownTicks = MsToTicks(D.Dive_CooldownMs, TickRate);
            DiveLandStunTicks = MsToTicks(D.Dive_LandStunMs, TickRate);
            DiveCancelWindowTicks = MsToTicks(D.Dive_JumpCancelWindowMs, TickRate);
            RespawnTicks = MsToTicks(D.Respawn_DelayMs, TickRate);
        }

        private static float Mm(int v) => v / 1000f;

        private static int Scale(int v, int permille) => permille == 1000 ? v : (int)HazardMath.RoundDiv((long)v * permille, 1000);

        /// <summary>Round half up: 100 ms → 3, 120 ms → 4 (3.6), 350 ms → 11 (10.5), 150 ms → 5 (4.5).</summary>
        public static int MsToTicks(int ms, int tickRate) => (ms * tickRate * 2 + 1000) / 2000;

        /// <summary>
        /// Canonical JSON (sorted keys, no whitespace) rebuilt from the generated constants, i.e. from the exact values
        /// this build uses. Its sha256 must equal <see cref="MovementConfigData.Sha256Hex"/> (EditMode test).
        /// </summary>
        public static string ToCanonicalJson()
        {
            var sb = new StringBuilder(700);
            sb.Append('{');
            Obj(sb, "capsule", ("height", D.Capsule_Height), ("radius", D.Capsule_Radius), ("skin", D.Capsule_Skin));
            sb.Append(',');
            Obj(sb, "dive", ("cooldownMs", D.Dive_CooldownMs), ("horizontalSpeed", D.Dive_HorizontalSpeed),
                ("jumpCancelWindowMs", D.Dive_JumpCancelWindowMs), ("landStunMs", D.Dive_LandStunMs), ("verticalSpeed", D.Dive_VerticalSpeed));
            sb.Append(',');
            Obj(sb, "gravity", ("accel", D.Gravity_Accel), ("fallMultiplierPermille", D.Gravity_FallMultiplierPermille), ("maxFallSpeed", D.Gravity_MaxFallSpeed));
            sb.Append(',');
            Obj(sb, "ground", ("ledgeTolerance", D.Ground_LedgeTolerance), ("maxSlopeDeg", D.Ground_MaxSlopeDeg), ("slideIterations", D.Ground_SlideIterations),
                ("snapDistance", D.Ground_SnapDistance), ("stepOffset", D.Ground_StepOffset));
            sb.Append(',');
            Obj(sb, "jump", ("apexHeight", D.Jump_ApexHeight), ("bufferMs", D.Jump_BufferMs), ("coyoteMs", D.Jump_CoyoteMs));
            sb.Append(",\"name\":").Append(Str(D.Name)).Append(',');
            Obj(sb, "push", ("maxSpeed", D.Push_MaxSpeed));
            sb.Append(',');
            Obj(sb, "respawn", ("delayMs", D.Respawn_DelayMs), ("killY", D.Respawn_KillY));
            sb.Append(',');
            Obj(sb, "run", ("airControlPermille", D.Run_AirControlPermille), ("groundAccel", D.Run_GroundAccel),
                ("groundDecel", D.Run_GroundDecel), ("maxSpeed", D.Run_MaxSpeed));
            sb.Append(",\"tickRateHz\":").Append(D.TickRateHz);
            sb.Append(",\"units\":").Append(Str(D.Units));
            sb.Append(",\"version\":").Append(D.Version);
            sb.Append('}');
            return sb.ToString();
        }

        private static void Obj(StringBuilder sb, string name, params (string key, int value)[] fields)
        {
            sb.Append(Str(name)).Append(":{");
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(Str(fields[i].key)).Append(':').Append(fields[i].value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            sb.Append('}');
        }

        private static string Str(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
