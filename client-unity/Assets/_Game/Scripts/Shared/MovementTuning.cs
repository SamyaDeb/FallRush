using System.Text;
using D = FallRush.Shared.MovementConfigData;

namespace FallRush.Shared
{
    /// <summary>
    /// The tunable movement integers (the units of <c>movement_v2.json</c>: mm, mm/s, mm/s², ms, permille). The game always
    /// uses <see cref="Generated"/> (the hashed config). G1 §3.7: the dev-only practice mode edits a copy live and prints it
    /// as JSON to paste into the config file; a tuned copy never reaches the server.
    /// </summary>
    public struct MovementTuning
    {
        public int MaxSpeed, GroundAccel, GroundDecel, AirControlPermille;
        public int Gravity, FallMultiplierPermille;
        public int JumpApex;
        public int DiveHorizontal, DiveVertical, DiveLandMs, SlideDecel;
        public int StumbleMinFall, StumbleMs, StumbleSpeedPermille;

        public static MovementTuning Generated => new MovementTuning
        {
            MaxSpeed = D.Run_MaxSpeed, GroundAccel = D.Run_GroundAccel, GroundDecel = D.Run_GroundDecel, AirControlPermille = D.Run_AirControlPermille,
            Gravity = D.Gravity_Accel, FallMultiplierPermille = D.Gravity_FallMultiplierPermille,
            JumpApex = D.Jump_ApexHeight,
            DiveHorizontal = D.Dive_HorizontalSpeed, DiveVertical = D.Dive_VerticalSpeed, DiveLandMs = D.Dive_LandStunMs, SlideDecel = D.Dive_SlideDecel,
            StumbleMinFall = D.Stumble_MinFallHeight, StumbleMs = D.Stumble_DurationMs, StumbleSpeedPermille = D.Stumble_SpeedPermille,
        };

        public bool IsGenerated => Equals(Generated);

        /// <summary>The changed sections as JSON, in the config file's shape (paste over the matching keys).</summary>
        public string ToJson()
        {
            var sb = new StringBuilder(400);
            sb.Append("\"run\": { \"maxSpeed\": ").Append(MaxSpeed).Append(", \"groundAccel\": ").Append(GroundAccel)
                .Append(", \"groundDecel\": ").Append(GroundDecel).Append(", \"airControlPermille\": ").Append(AirControlPermille).Append(" },\n");
            sb.Append("\"gravity\": { \"accel\": ").Append(Gravity).Append(", \"fallMultiplierPermille\": ").Append(FallMultiplierPermille)
                .Append(", \"maxFallSpeed\": ").Append(D.Gravity_MaxFallSpeed).Append(" },\n");
            sb.Append("\"jump\": { \"apexHeight\": ").Append(JumpApex).Append(", \"coyoteMs\": ").Append(D.Jump_CoyoteMs).Append(", \"bufferMs\": ").Append(D.Jump_BufferMs).Append(" },\n");
            sb.Append("\"dive\": { \"horizontalSpeed\": ").Append(DiveHorizontal).Append(", \"verticalSpeed\": ").Append(DiveVertical)
                .Append(", \"cooldownMs\": ").Append(D.Dive_CooldownMs).Append(", \"landStunMs\": ").Append(DiveLandMs)
                .Append(", \"jumpCancelWindowMs\": ").Append(D.Dive_JumpCancelWindowMs).Append(", \"slideDecel\": ").Append(SlideDecel).Append(" },\n");
            sb.Append("\"stumble\": { \"minFallHeight\": ").Append(StumbleMinFall).Append(", \"durationMs\": ").Append(StumbleMs)
                .Append(", \"speedPermille\": ").Append(StumbleSpeedPermille).Append(" }");
            return sb.ToString();
        }
    }
}
