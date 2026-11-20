using System;
using System.Text;

namespace FallRush.Shared
{
    /// <summary>
    /// Cross-platform determinism vectors (doc 08 §9, M3 §3.4): <see cref="Count"/> pseudo-random
    /// <c>(type, def, seed, variant, modifier, tick)</c> cases, each evaluated through the real pose and effect functions,
    /// with the outputs quantised to wire precision (mm, cm/s, ticks, phase16) as <see cref="Width"/> integers.
    /// <list type="bullet">
    /// <item>The expected outputs are committed twice: <c>Tests/Vectors/hazard_vectors_v1.json</c> (readable) and the
    /// generated <see cref="HazardVectorsExpected"/> (so player builds can check themselves).</item>
    /// <item>The EditMode test recomputes them on every run; the self-test entry points (<c>-fallrushSelfTest</c>,
    /// WebGL <c>?selftest=1</c>) run the same code in a Linux / Mac / wasm player and print one pass/fail line + a digest,
    /// naming the first differing case and field.</item>
    /// </list>
    /// Case generation uses splitmix64 (integers only), so the inputs themselves are identical everywhere.
    /// </summary>
    public static class HazardVectors
    {
        public const int Version = 1;
        public const int Count = 1000;
        public const int Width = 12;

        public static readonly string[] Fields =
        {
            "type", "variant", "modifier", "t", "phase", "a", "b", "c", "contact", "velX", "velY", "velZ",
        };

        private static ulong SplitMix(ref ulong s)
        {
            s += 0x9E3779B97F4A7C15UL;
            var z = s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private static int R(ref ulong s, int lo, int hi) => lo + (int)(SplitMix(ref s) % (ulong)(hi - lo + 1));

        /// <summary>Evaluates case <paramref name="i"/> into <paramref name="o"/> (<see cref="Width"/> ints).</summary>
        public static void Compute(int i, int[] o)
        {
            Array.Clear(o, 0, Width);
            var s = 0xFA11_0000_0000UL + (ulong)i * 7919UL;
            var type = (HazardType)(1 + i % 8);
            var variant = R(ref s, 0, 1);
            var modifier = R(ref s, 0, 5);
            var seed = SplitMix(ref s);
            var t = R(ref s, -1000, 200000);
            var m = ModifierEffects.Get(modifier);
            var cfg = MovementConfig.For(modifier);
            o[0] = (int)type;
            o[1] = variant;
            o[2] = modifier;
            o[3] = t;
            var id = "vec_" + i;
            // Feet near the hazard so contacts happen about half the time.
            int Near(ref ulong st, int c, int spread) => c + R(ref st, -spread, spread);
            var st0 = new MotorState { VelX = (short)R(ref s, -700, 700), VelZ = (short)R(ref s, -700, 700), Flags = (byte)(R(ref s, 0, 3) == 0 ? MotorState.Diving : MotorState.Grounded) };
            switch (type)
            {
                case HazardType.SpinnerBar:
                {
                    var a = new SweeperDef
                    {
                        PivotXMm = R(ref s, -50000, 50000), PivotYMm = R(ref s, -2000, 5000), PivotZMm = R(ref s, -50000, 50000),
                        ArmMm = R(ref s, 1000, 6000), BarRadiusMm = R(ref s, 100, 300), BarHeightMm = R(ref s, 200, 1200),
                        PeriodTicks = R(ref s, 60, 450) * (R(ref s, 0, 1) == 0 ? 1 : -1), Tangential = R(ref s, 0, 1),
                        KnockbackCms = KnockProfile.SoftPushCms, KnockUpCms = KnockProfile.SoftUpCms, StunTicks = KnockProfile.SoftStunTicks,
                        ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks),
                    };
                    var b = a;
                    b.PeriodTicks = -a.PeriodTicks;
                    var d = variant == 1 ? b : a;
                    d.Phase16 = ObstaclePhase.Phase16(seed, id, 0, 65535);
                    HazardTuning.Apply(ref d, in m);
                    var hz = new HazardSet(new[] { d }, null) { RoundStartTick = 0 };
                    var p = HazardMath.PhaseAt(t, d.PeriodTicks, d.Phase16);
                    o[4] = p;
                    o[5] = d.PivotXMm + HazardMath.MulQ30(d.ArmMm, HazardMath.CosQ30(p));
                    o[6] = d.PivotZMm + HazardMath.MulQ30(d.ArmMm, HazardMath.SinQ30(p));
                    o[7] = d.PeriodTicks;
                    st0.PosX = Near(ref s, d.PivotXMm, d.ArmMm);
                    st0.PosY = d.PivotYMm + R(ref s, -400, 600);
                    st0.PosZ = Near(ref s, d.PivotZMm, d.ArmMm);
                    Effect(hz, st0, t, cfg, o);
                    break;
                }
                case HazardType.MovingPlatform:
                {
                    var knots = R(ref s, 0, 6);
                    var a = new PlatformDef
                    {
                        CenterXMm = R(ref s, -50000, 50000), CenterYMm = R(ref s, -5000, 5000), CenterZMm = R(ref s, -50000, 50000),
                        HalfXMm = 1500, HalfYMm = 250, HalfZMm = 1500, PeriodTicks = R(ref s, 60, 600),
                        TravelXMm = R(ref s, -4000, 4000), TravelYMm = R(ref s, -1000, 1000), TravelZMm = R(ref s, -4000, 4000),
                    };
                    if (knots >= 2)
                    {
                        a.KnotsMm = new int[knots * 3];
                        for (var k = 0; k < a.KnotsMm.Length; k++)
                            a.KnotsMm[k] = R(ref s, -5000, 5000);
                    }

                    var b = a;
                    b.TravelXMm = a.TravelZMm;
                    b.TravelZMm = a.TravelXMm;
                    var d = variant == 1 ? b : a;
                    d.Phase16 = ObstaclePhase.Phase16(seed, id, 0, 65535);
                    HazardTuning.Apply(ref d, in m);
                    HazardMath.PlatformCenterMm(in d, t, out var x, out var y, out var z);
                    var dl = HazardMath.PlatformDelta(in d, t);
                    o[4] = HazardMath.PhaseAt(t, d.PeriodTicks, d.Phase16);
                    o[5] = x;
                    o[6] = y;
                    o[7] = z;
                    o[9] = MotorState.QuantizeMm(dl.X);
                    o[10] = MotorState.QuantizeMm(dl.Y);
                    o[11] = MotorState.QuantizeMm(dl.Z);
                    break;
                }
                case HazardType.Conveyor:
                {
                    var d = new ConveyorDef { BeltXMms = R(ref s, -4000, 4000), BeltYMms = R(ref s, -1000, 1000), BeltZMms = R(ref s, -4000, 4000) };
                    if (variant == 1)
                        d.BeltXMms = -d.BeltXMms;
                    HazardTuning.Apply(ref d, in m);
                    HazardMath.ConveyorDeltaMm(in d, t, out var x, out var y, out var z);
                    o[5] = x;
                    o[6] = y;
                    o[7] = z;
                    o[9] = d.BeltXMms;
                    o[10] = d.BeltYMms;
                    o[11] = d.BeltZMms;
                    break;
                }
                case HazardType.SpringPad:
                {
                    var d = new SpringPadDef
                    {
                        CenterXMm = R(ref s, -50000, 50000), CenterYMm = R(ref s, -2000, 5000), CenterZMm = R(ref s, -50000, 50000),
                        HalfXMm = R(ref s, 500, 2000), HalfZMm = R(ref s, 500, 2000), ThicknessMm = 200,
                        LaunchXCms = R(ref s, -800, 800), LaunchYCms = R(ref s, 600, 1600), LaunchZCms = R(ref s, -800, 800),
                    };
                    var fx = Near(ref s, d.CenterXMm, d.HalfXMm + 300);
                    var fy = d.CenterYMm + R(ref s, -80, 80);
                    var fz = Near(ref s, d.CenterZMm, d.HalfZMm + 300);
                    o[5] = fx;
                    o[6] = fy;
                    o[7] = fz;
                    o[8] = HazardMath.OnSpringPad(in d, fx, fy, fz) ? 1 : 0;
                    var hz = new HazardSet(null, null, null, new[] { d }, null, null, null, null);
                    var st = new MotorState { PosX = fx, PosY = fy, PosZ = fz, Flags = MotorState.Grounded, Surface = (byte)(hz.PadSurface(0) + 1) };
                    hz.ApplySprings(ref st);
                    o[9] = st.VelX;
                    o[10] = st.VelY;
                    o[11] = st.VelZ;
                    break;
                }
                case HazardType.TurnTable:
                {
                    var a = new TurnTableDef
                    {
                        CenterXMm = R(ref s, -50000, 50000), CenterYMm = 0, CenterZMm = R(ref s, -50000, 50000),
                        RadiusMm = R(ref s, 1500, 6000), ThicknessMm = 300, PeriodTicks = R(ref s, 150, 900) * (R(ref s, 0, 1) == 0 ? 1 : -1),
                    };
                    var b = a;
                    b.PeriodTicks = -a.PeriodTicks;
                    var d = variant == 1 ? b : a;
                    d.Phase16 = ObstaclePhase.Phase16(seed, id, 0, 65535);
                    HazardTuning.Apply(ref d, in m);
                    var fx = Near(ref s, d.CenterXMm, d.RadiusMm);
                    var fz = Near(ref s, d.CenterZMm, d.RadiusMm);
                    HazardMath.TurnTableCarryMm(in d, t, fx, fz, out var dx, out var dz);
                    o[4] = HazardMath.TurnTablePhase(in d, t);
                    o[5] = fx;
                    o[6] = fz;
                    o[7] = d.PeriodTicks;
                    o[9] = dx;
                    o[11] = dz;
                    break;
                }
                case HazardType.GateDoor:
                {
                    var a = new GateDoorDef
                    {
                        CenterXMm = R(ref s, -50000, 50000), CenterYMm = 1300, CenterZMm = R(ref s, -50000, 50000),
                        HalfXMm = R(ref s, 0, 1) == 0 ? 200 : 1500, HalfYMm = 1300, HalfZMm = 0, LiftMm = R(ref s, 1500, 3000),
                        ClosedTicks = R(ref s, 0, 120), OpeningTicks = R(ref s, 3, 30), OpenTicks = R(ref s, 15, 150), ClosingTicks = R(ref s, 6, 30),
                        OffsetTicks = R(ref s, 0, 200), TelegraphTicks = 15,
                    };
                    a.HalfZMm = a.HalfXMm == 200 ? 1500 : 200;
                    var b = a;
                    b.OffsetTicks = a.OffsetTicks + 60;
                    var d = variant == 1 ? b : a;
                    d.Phase16 = 0;
                    HazardTuning.Apply(ref d, in m);
                    var hz = new HazardSet(null, null, null, null, null, new[] { d }, null, null);
                    o[4] = HazardMath.DoorCycleTick(in d, t);
                    o[5] = HazardMath.DoorLiftMm(in d, t);
                    o[6] = HazardMath.DoorTicksUntilClosing(in d, t);
                    o[7] = HazardMath.DoorOpen(in d, t) ? 1 : 0;
                    var st = new MotorState
                    {
                        PosX = Near(ref s, d.CenterXMm, d.HalfXMm + 400), PosY = R(ref s, 0, 1500), PosZ = Near(ref s, d.CenterZMm, d.HalfZMm + 400),
                        VelX = (short)R(ref s, -700, 700), VelZ = (short)R(ref s, -700, 700),
                    };
                    o[8] = hz.ApplyDoors(ref st, unchecked((uint)t), cfg) ? 1 : 0;
                    o[9] = st.PosX;
                    o[10] = st.VelX * 1000 + st.VelZ;
                    o[11] = st.PosZ;
                    break;
                }
                case HazardType.MalletSwing:
                {
                    var a = new MalletDef
                    {
                        PivotXMm = R(ref s, -50000, 50000), PivotYMm = R(ref s, 3000, 7000), PivotZMm = R(ref s, -50000, 50000),
                        SwingAxis = R(ref s, 0, 1) * 2, ArmMm = R(ref s, 2500, 6000), HeadRadiusMm = R(ref s, 300, 600), HeadHalfWidthMm = R(ref s, 400, 1500),
                        AmplitudePhase16 = R(ref s, 4000, 13000), PeriodTicks = R(ref s, 60, 300), TelegraphTicks = 15,
                        KnockbackCms = KnockProfile.HardPushCms, KnockUpCms = KnockProfile.HardUpCms, StunTicks = KnockProfile.HardStunTicks,
                        ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.HardStunTicks),
                    };
                    var b = a;
                    b.PeriodTicks = a.PeriodTicks + 30;
                    var d = variant == 1 ? b : a;
                    d.Phase16 = ObstaclePhase.Phase16(seed, id, 0, 65535);
                    HazardTuning.Apply(ref d, in m);
                    HazardMath.MalletHeadMm(in d, t, out var hx, out var hy, out var hzz, out var vs);
                    o[4] = HazardMath.MalletAngle16(in d, t);
                    o[5] = hx;
                    o[6] = hy * 4 + (vs + 1);
                    o[7] = hzz;
                    st0.PosX = Near(ref s, hx, 1200);
                    st0.PosY = hy + R(ref s, -1200, 400);
                    st0.PosZ = Near(ref s, hzz, 1200);
                    var hz = new HazardSet(null, null, null, null, null, null, new[] { d }, null);
                    Effect(hz, st0, t, cfg, o);
                    break;
                }
                default:
                {
                    var d = new BumperDef
                    {
                        CenterXMm = R(ref s, -50000, 50000), CenterYMm = R(ref s, -2000, 3000), CenterZMm = R(ref s, -50000, 50000),
                        RadiusMm = R(ref s, 400, 1500), HeightMm = R(ref s, 600, 1500), ContactMarginMm = 40,
                        KnockbackCms = KnockProfile.SoftPushCms, KnockUpCms = KnockProfile.SoftUpCms, StunTicks = KnockProfile.SoftStunTicks,
                        ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks),
                    };
                    HazardTuning.Apply(ref d, in m);
                    st0.PosX = Near(ref s, d.CenterXMm, d.RadiusMm + 600);
                    st0.PosY = d.CenterYMm + R(ref s, -1200, 1600);
                    st0.PosZ = Near(ref s, d.CenterZMm, d.RadiusMm + 600);
                    o[5] = st0.PosX;
                    o[6] = st0.PosY;
                    o[7] = st0.PosZ;
                    var hz = new HazardSet(null, null, null, null, null, null, null, new[] { d });
                    Effect(hz, st0, t, cfg, o);
                    break;
                }
            }
        }

        private static void Effect(HazardSet hz, MotorState st, int t, MovementConfig cfg, int[] o)
        {
            o[8] = hz.ApplyKnocks(ref st, unchecked((uint)t), cfg) ? 1 : 0;
            o[9] = st.VelX;
            o[10] = st.VelY * 1000 + st.StunTicks * 10 + (st.ImmunityTicks > 0 ? 1 : 0);
            o[11] = st.VelZ;
        }

        /// <summary>All outputs, case after case.</summary>
        public static int[] ComputeAll()
        {
            var all = new int[Count * Width];
            var o = new int[Width];
            for (var i = 0; i < Count; i++)
            {
                Compute(i, o);
                Array.Copy(o, 0, all, i * Width, Width);
            }

            return all;
        }

        /// <summary>sha256 over the outputs as little-endian i32 (the digest every platform prints).</summary>
        public static string Digest(int[] all)
        {
            var w = new CanonicalWriter();
            w.I32(Version).I32(Count).I32(Width);
            foreach (var v in all)
                w.I32(v);
            return Sha256.Hex(w.Sha256());
        }

        /// <summary>Runs every case against <paramref name="expected"/>. Returns one line: PASS/FAIL, digests, first mismatches.</summary>
        public static bool SelfTest(int[] expected, string platform, out string line)
        {
            var all = ComputeAll();
            var digest = Digest(all);
            var mismatches = 0;
            var sb = new StringBuilder();
            for (var i = 0; i < Count; i++)
            {
                for (var f = 0; f < Width; f++)
                {
                    var a = all[i * Width + f];
                    var e = expected != null && expected.Length == Count * Width ? expected[i * Width + f] : int.MinValue;
                    if (a == e)
                        continue;
                    mismatches++;
                    if (mismatches <= 8)
                        sb.Append($" case {i} ({(HazardType)all[i * Width]}) {Fields[f]}: got {a} expected {e} (diff {(long)a - e});");
                }
            }

            var ok = mismatches == 0;
            line = $"[selftest] {(ok ? "PASS" : "FAIL")} platform={platform} hazard_vectors_v{Version} cases={Count} fields={Count * Width} mismatches={mismatches} digest={digest} expected={HazardVectorsExpected.Digest}{(ok ? "" : " first:" + sb)}";
            return ok;
        }
    }
}
