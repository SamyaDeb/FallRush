using System;
using System.Text;
using D = FallRush.Shared.MovementConfigData;

namespace FallRush.Shared
{
    /// <summary>
    /// Runtime movement constants (GDD §7, doc 04 §5), built from <see cref="MovementConfigData"/>, which is generated
    /// from <c>packages/shared/config/movement_v3.json</c> (G4.1c: v2 + the bump flag; v1 and v2 are kept unchanged for history). Integers in the JSON (mm, mm/s, ms, permille) become floats
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
        /// <summary>G1 belly slide: horizontal deceleration while a dive landing slides (m/s²).</summary>
        public readonly float SlideDecel;
        /// <summary>G1 stumble: a landing faster than this (m/s down) — a fall of more than <c>stumble.minFallHeight</c> under this config's gravity — stumbles.</summary>
        public readonly float StumbleImpactSpeed;
        /// <summary>G1 stumble: run speed cap while stumbling, as a fraction of <see cref="MaxSpeed"/>.</summary>
        public readonly float StumbleSpeedFactor;
        public readonly float MaxSlopeCos, StepOffset, SnapDistance, LedgeTolerance;
        public readonly int SlideIterations;
        public readonly float PushMaxSpeed, KillY;

        /// <summary>G4.1c bump stagger (movement_v3 <c>bump</c>, default off): two players meeting at a closing speed ≥
        /// <see cref="BumpMinClosingMms"/> (mm/s, along the contact normal) → the slower one stumbles for <see cref="BumpTicks"/>.</summary>
        public readonly bool BumpEnabled;
        public readonly int BumpMinClosingMms;
        public readonly int BumpTicks;

        public readonly int CoyoteTicks, JumpBufferTicks, DiveCooldownTicks, DiveLandStunTicks, DiveCancelWindowTicks, RespawnTicks, StumbleTicks;

        /// <summary>DEV ONLY (practice tuning mode, G1 §3.7): a config from edited values; never used by the server.</summary>
        public static MovementConfig FromTuning(in MovementTuning t, int modifierId) => new MovementConfig(ModifierEffects.Get(modifierId), t);

        /// <summary>
        /// G4.1c: the same constants with the bump flag forced on/off (tests and the measurement runs behind
        /// <c>-fallrushBump 1</c> / <c>?bump=1</c>). The shipped config is movement_v3 as generated (bump off).
        /// </summary>
        public static MovementConfig WithBump(int modifierId, bool on)
        {
            var b = For(modifierId);
            return b.BumpEnabled == on ? b : new MovementConfig(ModifierEffects.Get(modifierId), MovementTuning.Generated, on);
        }

        private MovementConfig(in ModifierEffects m) : this(m, MovementTuning.Generated)
        {
        }

        private MovementConfig(in ModifierEffects m, in MovementTuning t, bool? bump = null)
        {
            ModifierId = m.AffectsMotor ? m.Id : ModifierEffects.NoneId;
            Dt = 1f / TickRate;
            Radius = Mm(D.Capsule_Radius);
            Height = Mm(D.Capsule_Height);
            Skin = Mm(D.Capsule_Skin);
            MaxSpeed = Mm(t.MaxSpeed);
            GroundAccel = Mm(t.GroundAccel);
            GroundDecel = Mm(Scale(t.GroundDecel, m.GroundDecelPermille));
            AirAccel = GroundAccel * (t.AirControlPermille / 1000f);
            Gravity = Mm(Scale(t.Gravity, m.GravityPermille));
            FallMultiplier = t.FallMultiplierPermille / 1000f;
            MaxFallSpeed = Mm(D.Gravity_MaxFallSpeed);
            JumpApex = Mm(Scale(t.JumpApex, m.JumpApexPermille));
            JumpSpeed = (float)Math.Sqrt(2f * Gravity * JumpApex);
            DiveHorizontal = Mm(t.DiveHorizontal);
            DiveVertical = Mm(t.DiveVertical);
            SlideDecel = Mm(Scale(t.SlideDecel, m.GroundDecelPermille)); // ButterFloors makes the slide longer too
            var fallG = Gravity * (t.FallMultiplierPermille / 1000f);
            StumbleImpactSpeed = (float)Math.Sqrt(2f * fallG * Mm(t.StumbleMinFall));
            StumbleSpeedFactor = t.StumbleSpeedPermille / 1000f;
            MaxSlopeCos = (float)Math.Cos(D.Ground_MaxSlopeDeg * Math.PI / 180.0);
            StepOffset = Mm(D.Ground_StepOffset);
            SnapDistance = Mm(D.Ground_SnapDistance);
            LedgeTolerance = Mm(D.Ground_LedgeTolerance);
            SlideIterations = D.Ground_SlideIterations;
            PushMaxSpeed = Mm(D.Push_MaxSpeed);
            KillY = Mm(D.Respawn_KillY);
            BumpEnabled = bump ?? D.Bump_Enabled;
            BumpMinClosingMms = D.Bump_MinClosingSpeed;
            BumpTicks = (D.Bump_StaggerMs * TickRate + 999) / 1000; // ⌈250 · 30 / 1000⌉ = 8

            CoyoteTicks = MsToTicks(D.Jump_CoyoteMs, TickRate);
            JumpBufferTicks = MsToTicks(D.Jump_BufferMs, TickRate);
            DiveCooldownTicks = MsToTicks(D.Dive_CooldownMs, TickRate);
            DiveLandStunTicks = MsToTicks(t.DiveLandMs, TickRate);
            DiveCancelWindowTicks = MsToTicks(D.Dive_JumpCancelWindowMs, TickRate);
            RespawnTicks = MsToTicks(D.Respawn_DelayMs, TickRate);
            StumbleTicks = MsToTicks(t.StumbleMs, TickRate);
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
            sb.Append(Str("bump")).Append(":{").Append(Str("enabled")).Append(':').Append(D.Bump_Enabled ? "true" : "false").Append(',');
            sb.Append(Str("minClosingSpeed")).Append(':').Append(D.Bump_MinClosingSpeed).Append(',');
            sb.Append(Str("staggerMs")).Append(':').Append(D.Bump_StaggerMs).Append("},");
            Obj(sb, "capsule", ("height", D.Capsule_Height), ("radius", D.Capsule_Radius), ("skin", D.Capsule_Skin));
            sb.Append(',');
            Obj(sb, "dive", ("cooldownMs", D.Dive_CooldownMs), ("horizontalSpeed", D.Dive_HorizontalSpeed),
                ("jumpCancelWindowMs", D.Dive_JumpCancelWindowMs), ("landStunMs", D.Dive_LandStunMs), ("slideDecel", D.Dive_SlideDecel), ("verticalSpeed", D.Dive_VerticalSpeed));
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
            sb.Append(',');
            Obj(sb, "stumble", ("durationMs", D.Stumble_DurationMs), ("minFallHeight", D.Stumble_MinFallHeight), ("speedPermille", D.Stumble_SpeedPermille));
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
