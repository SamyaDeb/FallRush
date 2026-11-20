using System;
using System.Text;

namespace FallRush.Shared
{
    /// <summary>
    /// Cross-platform determinism vectors v2 (M4): the 1,000 v1 cases (<see cref="HazardVectors"/>, unchanged, digest
    /// <c>fc06826c…</c>) followed by <see cref="M4Count"/> cases of the M4 pure functions: the goo plane (height, time to
    /// reach a height, contact), the star timeline (seeded anchor, spawn / expiry, value, position on static / platform /
    /// turn-table anchors), dropped-star hops and the crumble grid's tile lookup. Same width, integers only. Committed as
    /// <c>Tests/Vectors/hazard_vectors_v2.json</c> and <see cref="HazardVectorsV2Expected"/>.
    /// </summary>
    public static class HazardVectorsV2
    {
        public const int Version = 2;
        public const int M4Count = 400;
        public const int Count = HazardVectors.Count + M4Count;
        public const int Width = HazardVectors.Width;
        public const int Goo = 9, Star = 10, Drop = 11, Tile = 12;

        private static ulong SplitMix(ref ulong s)
        {
            s += 0x9E3779B97F4A7C15UL;
            var z = s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private static int R(ref ulong s, int lo, int hi) => lo + (int)(SplitMix(ref s) % (ulong)(hi - lo + 1));

        public static void Compute(int i, int[] o)
        {
            if (i < HazardVectors.Count)
            {
                HazardVectors.Compute(i, o);
                return;
            }

            Array.Clear(o, 0, Width);
            var k = i - HazardVectors.Count;
            var s = 0xFA11_0004_0000UL + (ulong)k * 104729UL;
            var type = Goo + k % 4;
            o[0] = type;
            switch (type)
            {
                case Goo:
                {
                    var g = new GooDef { Y0Mm = R(ref s, -8000, 0), RiseMmPerSec = R(ref s, 50, 900), DelayTicks = R(ref s, 0, 600), MaxYMm = R(ref s, 10000, 60000) };
                    var t = R(ref s, -100, 6000);
                    var y = R(ref s, -8000, 60000);
                    var feet = R(ref s, -8000, 60000);
                    o[1] = g.Y0Mm;
                    o[2] = g.RiseMmPerSec;
                    o[3] = t;
                    o[4] = HazardMath.GooYMm(in g, t);
                    o[5] = y;
                    var reach = HazardMath.GooTicksToReach(in g, y);
                    o[6] = reach == int.MaxValue ? -1 : reach;
                    o[7] = feet < o[4] ? 1 : 0;
                    o[8] = g.DelayTicks;
                    o[9] = g.MaxYMm;
                    break;
                }
                case Star:
                {
                    var n = R(ref s, 6, 24);
                    var a = new int[n * 3];
                    var att = new int[n];
                    for (var j = 0; j < n; j++)
                    {
                        a[j * 3] = R(ref s, -18000, 18000);
                        a[j * 3 + 1] = R(ref s, 0, 3000);
                        a[j * 3 + 2] = R(ref s, -18000, 18000);
                        att[j] = R(ref s, 0, 3) switch { 0 => 0, 1 => 1000, _ => -1 };
                    }

                    var golden = new[] { n - 1, n - 2 };
                    var f = new StarFieldDef
                    {
                        AnchorsMm = a, Attach = att, Golden = golden, WaveEveryTicks = R(ref s, 120, 360), WaveSize = Math.Min(8, n - 2), WaveLifeTicks = R(ref s, 240, 720),
                        GoldenEveryTicks = 450, GoldenLifeTicks = 300, GoldenValue = 3, PickupRadiusMm = 400,
                    };
                    var pl = new PlatformDef { CenterXMm = R(ref s, -15000, 15000), CenterYMm = R(ref s, 0, 2000), CenterZMm = R(ref s, -15000, 15000), HalfXMm = 1500, HalfYMm = 200, HalfZMm = 1500, PeriodTicks = R(ref s, 120, 600), TravelXMm = R(ref s, -4000, 4000), TravelZMm = R(ref s, -4000, 4000), Phase16 = R(ref s, 0, 65535) };
                    var tt = new TurnTableDef { CenterXMm = 0, CenterYMm = 300, CenterZMm = 0, RadiusMm = 5000, ThicknessMm = 400, PeriodTicks = R(ref s, 240, 900) * (R(ref s, 0, 1) == 0 ? 1 : -1), Phase16 = R(ref s, 0, 65535) };
                    var hz = new HazardSet(null, new[] { pl }, null, null, new[] { tt }, null, null, null);
                    var seed = SplitMix(ref s);
                    var id = R(ref s, 0, 1) == 0 ? R(ref s, 0, 80) : HazardMath.StarGoldenBase + R(ref s, 1, 6);
                    var t = R(ref s, 0, 2400);
                    var ok = HazardMath.StarSlot(in f, seed, id, out var anchor, out var spawn, out var expire, out var value);
                    o[1] = id;
                    o[2] = ok ? 1 : 0;
                    o[3] = t;
                    o[4] = anchor;
                    o[5] = spawn;
                    o[6] = expire;
                    o[7] = value;
                    if (ok)
                    {
                        HazardMath.StarAnchorMm(in f, hz, anchor, t, out var x, out var y, out var z);
                        o[8] = att[anchor];
                        o[9] = x;
                        o[10] = y;
                        o[11] = z;
                    }

                    break;
                }
                case Drop:
                {
                    var f = new StarFieldDef { DropPopTicks = R(ref s, 5, 30), DropDistanceMm = R(ref s, 500, 2500) };
                    int x0 = R(ref s, -18000, 18000), y0 = R(ref s, 0, 12000), z0 = R(ref s, -18000, 18000);
                    var seedish = (uint)SplitMix(ref s);
                    var age = R(ref s, 0, 40);
                    HazardMath.DropPosMm(in f, x0, y0, z0, seedish, age, out var x, out var y, out var z);
                    o[1] = x0;
                    o[2] = y0;
                    o[3] = age;
                    o[4] = z0;
                    o[5] = (int)(seedish & 0x7FFFFFFF);
                    o[6] = f.DropPopTicks;
                    o[7] = f.DropDistanceMm;
                    o[9] = x;
                    o[10] = y;
                    o[11] = z;
                    break;
                }
                default:
                {
                    var g = new TileGridDef { OriginXMm = R(ref s, -9000, -5000), OriginZMm = R(ref s, -9000, -5000), TileMm = 1600, GapMm = 40, Cols = 9, Rows = 9, ShelfTopsMm = new[] { 12000, 6000, 0 }, ThicknessMm = 400, WobbleTicks = 18 };
                    int fx = R(ref s, -10000, 10000), fy = R(ref s, -200, 12200), fz = R(ref s, -10000, 10000);
                    if (R(ref s, 0, 1) == 0)
                        fy = new[] { 12000, 6000, 0 }[R(ref s, 0, 2)] + R(ref s, -60, 60);
                    var tile = g.TileUnder(fx, fy, fz);
                    o[1] = g.OriginXMm;
                    o[2] = g.OriginZMm;
                    o[3] = fx;
                    o[4] = fy;
                    o[5] = fz;
                    o[6] = tile;
                    if (tile >= 0)
                    {
                        g.Center(tile, out var cx, out var cy, out var cz);
                        o[7] = g.Shelf(tile);
                        o[9] = cx;
                        o[10] = cy;
                        o[11] = cz;
                    }

                    break;
                }
            }
        }

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

        public static string Digest(int[] all)
        {
            var w = new CanonicalWriter();
            w.I32(Version).I32(Count).I32(Width);
            foreach (var v in all)
                w.I32(v);
            return Sha256.Hex(w.Sha256());
        }

        public static bool SelfTest(int[] expected, string expectedDigest, string platform, out string line)
        {
            var all = ComputeAll();
            var digest = Digest(all);
            var mismatches = 0;
            var sb = new StringBuilder();
            for (var i = 0; i < Count; i++)
                for (var f = 0; f < Width; f++)
                {
                    var a = all[i * Width + f];
                    var e = expected != null && expected.Length == Count * Width ? expected[i * Width + f] : int.MinValue;
                    if (a == e)
                        continue;
                    mismatches++;
                    if (mismatches <= 8)
                        sb.Append($" case {i} (type {all[i * Width]}) field {f}: got {a} expected {e};");
                }

            var ok = mismatches == 0;
            line = $"[selftest] {(ok ? "PASS" : "FAIL")} platform={platform} hazard_vectors_v{Version} cases={Count} fields={Count * Width} mismatches={mismatches} digest={digest} expected={expectedDigest}{(ok ? "" : " first:" + sb)}";
            return ok;
        }
    }
}
