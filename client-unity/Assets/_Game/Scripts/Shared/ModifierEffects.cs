using System;
using System.Globalization;
using System.Text;

namespace FallRush.Shared
{
    /// <summary>
    /// One Chaos Capsule modifier's effect values (GDD §8), from <c>packages/shared/config/modifiers_v1.json</c> via the
    /// generated <see cref="ModifiersData"/>. Ids equal <c>map_pool_v1.json</c>.
    /// <list type="bullet">
    /// <item><c>motor</c> scope (MoonBounce, ButterFloors): a modified <see cref="MovementConfig"/> used by the server and client prediction alike.</item>
    /// <item><c>hazard</c> scope (TurboToys, BigBonk): hazard periods / speeds / knockback rescaled when the defs are resolved (<see cref="HazardTuning"/>).</item>
    /// <item><c>visual</c> scope (TinyTilt): client camera only (FOV, fog); no simulation effect.</item>
    /// </list>
    /// </summary>
    public readonly struct ModifierEffects
    {
        public readonly int Id;
        public readonly string Key;
        public readonly string Scope;
        public readonly int GravityPermille, JumpApexPermille, GroundDecelPermille;
        public readonly int HazardSpeedPermille, TelegraphPermille, KnockbackPermille;
        public readonly int FovDeltaDeg, FogPermille;

        public ModifierEffects(int id, string key, string scope, int gravity, int apex, int decel, int hazardSpeed, int telegraph, int knockback, int fov, int fog)
        {
            Id = id;
            Key = key;
            Scope = scope;
            GravityPermille = gravity;
            JumpApexPermille = apex;
            GroundDecelPermille = decel;
            HazardSpeedPermille = hazardSpeed;
            TelegraphPermille = telegraph;
            KnockbackPermille = knockback;
            FovDeltaDeg = fov;
            FogPermille = fog;
        }

        public const int NoneId = 0;

        /// <summary>The modifier with <paramref name="id"/>, or None for an unknown id.</summary>
        public static ModifierEffects Get(int id)
        {
            foreach (var m in ModifiersData.All)
                if (m.Id == id)
                    return m;
            return ModifiersData.All[0];
        }

        public static bool TryParse(string keyOrId, out int id)
        {
            id = 0;
            if (string.IsNullOrEmpty(keyOrId))
                return false;
            if (int.TryParse(keyOrId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return id >= 0 && id < ModifiersData.All.Length;
            foreach (var m in ModifiersData.All)
            {
                if (string.Equals(m.Key, keyOrId, StringComparison.OrdinalIgnoreCase))
                {
                    id = m.Id;
                    return true;
                }
            }

            return false;
        }

        public bool AffectsMotor => GravityPermille != 1000 || JumpApexPermille != 1000 || GroundDecelPermille != 1000;

        /// <summary>Canonical JSON of the typed values (sorted keys, no whitespace); its sha256 must equal <see cref="ModifiersData.Sha256Hex"/>.</summary>
        public static string ToCanonicalJson()
        {
            var sb = new StringBuilder(1700);
            sb.Append("{\"mapPool\":").Append(Q(ModifiersData.MapPool)).Append(",\"modifiers\":[");
            for (var i = 0; i < ModifiersData.All.Length; i++)
            {
                var m = ModifiersData.All[i];
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"fogPermille\":").Append(m.FogPermille)
                    .Append(",\"fovDeltaDeg\":").Append(m.FovDeltaDeg.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"gravityPermille\":").Append(m.GravityPermille)
                    .Append(",\"groundDecelPermille\":").Append(m.GroundDecelPermille)
                    .Append(",\"hazardSpeedPermille\":").Append(m.HazardSpeedPermille)
                    .Append(",\"id\":").Append(m.Id)
                    .Append(",\"jumpApexPermille\":").Append(m.JumpApexPermille)
                    .Append(",\"key\":").Append(Q(m.Key))
                    .Append(",\"knockbackPermille\":").Append(m.KnockbackPermille)
                    .Append(",\"scope\":").Append(Q(m.Scope))
                    .Append(",\"telegraphPermille\":").Append(m.TelegraphPermille)
                    .Append('}');
            }

            sb.Append("],\"name\":").Append(Q(ModifiersData.Name))
                .Append(",\"notes\":").Append(Q(ModifiersData.Notes))
                .Append(",\"units\":").Append(Q(ModifiersData.Units))
                .Append(",\"version\":").Append(ModifiersData.Version)
                .Append('}');
            return sb.ToString();
        }

        private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// Hazard-scope modifier rescaling, applied once when a round's defs are resolved (never per tick).
    /// <b>TurboToys rounding:</b> a period of P ticks becomes <c>round_half_up(P · 1000 / hazardSpeedPermille)</c> whole
    /// ticks (×1.2 speed: 150 → 125, 90 → 75, 100 → 83); belt speeds become <c>round(v · permille / 1000)</c> mm/s;
    /// telegraph durations are kept (telegraphPermille 1000).
    /// </summary>
    public static class HazardTuning
    {
        public static int ScalePeriod(int periodTicks, int speedPermille)
        {
            if (periodTicks == 0 || speedPermille == 1000 || speedPermille <= 0)
                return periodTicks;
            var a = Math.Abs((long)periodTicks);
            var r = (int)((2 * a * 1000 + speedPermille) / (2L * speedPermille));
            if (r < 1)
                r = 1;
            return periodTicks < 0 ? -r : r;
        }

        public static int ScaleDuration(int ticks, int speedPermille) => ticks <= 0 ? ticks : ScalePeriod(ticks, speedPermille);

        public static int ScaleValue(int v, int permille) => permille == 1000 ? v : (int)HazardMath.RoundDiv((long)v * permille, 1000);

        public static void Apply(ref SweeperDef d, in ModifierEffects m) => d.PeriodTicks = ScalePeriod(d.PeriodTicks, m.HazardSpeedPermille);

        public static void Apply(ref PlatformDef d, in ModifierEffects m) => d.PeriodTicks = ScalePeriod(d.PeriodTicks, m.HazardSpeedPermille);

        public static void Apply(ref TurnTableDef d, in ModifierEffects m) => d.PeriodTicks = ScalePeriod(d.PeriodTicks, m.HazardSpeedPermille);

        public static void Apply(ref ConveyorDef d, in ModifierEffects m)
        {
            d.BeltXMms = ScaleValue(d.BeltXMms, m.HazardSpeedPermille);
            d.BeltYMms = ScaleValue(d.BeltYMms, m.HazardSpeedPermille);
            d.BeltZMms = ScaleValue(d.BeltZMms, m.HazardSpeedPermille);
        }

        public static void Apply(ref GateDoorDef d, in ModifierEffects m)
        {
            var s = m.HazardSpeedPermille;
            d.ClosedTicks = ScaleDuration(d.ClosedTicks, s);
            d.OpeningTicks = ScaleDuration(d.OpeningTicks, s);
            d.OpenTicks = ScaleDuration(d.OpenTicks, s);
            d.ClosingTicks = ScaleDuration(d.ClosingTicks, s);
            d.OffsetTicks = ScaleDuration(d.OffsetTicks, s);
            d.TelegraphTicks = ScaleValue(d.TelegraphTicks, m.TelegraphPermille);
        }

        public static void Apply(ref MalletDef d, in ModifierEffects m)
        {
            d.PeriodTicks = ScalePeriod(d.PeriodTicks, m.HazardSpeedPermille);
            d.TelegraphTicks = ScaleValue(d.TelegraphTicks, m.TelegraphPermille);
            d.KnockbackCms = ScaleValue(d.KnockbackCms, m.KnockbackPermille);
        }

        /// <summary>Spring pads are not rescaled (their launch must clear the authored gap under every modifier).</summary>
        public static void Apply(ref SpringPadDef d, in ModifierEffects m)
        {
        }

        public static void Apply(ref BumperDef d, in ModifierEffects m) => d.KnockbackCms = ScaleValue(d.KnockbackCms, m.KnockbackPermille);

        /// <summary>G4.1a: TurboToys rolls faster and releases more often; BigBonk hits harder; the telegraph scales like the mallet's.</summary>
        public static void Apply(ref RollingBallDef d, in ModifierEffects m)
        {
            d.SpeedMmS = ScaleValue(d.SpeedMmS, m.HazardSpeedPermille);
            d.PeriodTicks = ScalePeriod(d.PeriodTicks, m.HazardSpeedPermille);
            d.TelegraphTicks = ScaleValue(d.TelegraphTicks, m.TelegraphPermille);
            d.KnockbackCms = ScaleValue(d.KnockbackCms, m.KnockbackPermille);
            d.LenMm = 0;
            HazardMath.PrepareBall(ref d);
        }
    }
}
