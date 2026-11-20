using System;

namespace FallRush.Shared
{
    /// <summary>
    /// <c>GOO_PLANE</c> (doc 08 §2, M4): a rising kill plane, <c>y = y0 + v·max(0, t − delay)</c> in integer mm and ticks
    /// (hazard time t), capped at <see cref="MaxYMm"/>. Touching it (feet below its surface) knocks you out: the effect
    /// runs in <see cref="PlayerStep.Run"/> on both sides, so prediction shows your own goo at the right tick.
    /// </summary>
    [Serializable]
    public struct GooDef
    {
        public int Y0Mm;          // surface height at t ≤ delay
        public int RiseMmPerSec;  // v (mm/s); y(t) = Y0 + floor(v · (t − delay) / 30)
        public int DelayTicks;
        public int MaxYMm;
        public int CenterXMm, CenterZMm, HalfXMm, HalfZMm; // visual extent only (the plane kills everywhere)
        public int WarnTicks;     // client: "Goo rising!" subtitle this long before it starts (telegraph)
    }

    /// <summary>
    /// <c>CRUMBLE_TILE</c> grid (Crumble Shelf, M4): square tiles on stacked shelves. Reactive: a tile is solid until
    /// <see cref="WobbleTicks"/> after its first contact (server-owned contact tick), then its collider is off (dropped).
    /// G1: on the top shelf a dropped tile returns <see cref="RespawnTicks"/> later (a new cycle starts at its next contact).
    /// Tile id = shelf · Cols · Rows + row · Cols + col; shelf 0 is the top one.
    /// </summary>
    [Serializable]
    public struct TileGridDef
    {
        public int OriginXMm, OriginZMm; // min corner of the grid (x, z) on every shelf
        public int TileMm;               // pitch (1.6 m)
        public int GapMm;                // visual / collider gap between tiles (rounded-corner look)
        public int Cols, Rows;
        public int[] ShelfTopsMm;        // top surface per shelf, top shelf first (12 000, 6 000, 0)
        public int ThicknessMm;
        public int WobbleTicks;          // G1: 1.0 s = 30 ticks (M4: 0.6 s)
        /// <summary>G1: a dropped tile on the top <see cref="RespawnShelves"/> shelves comes back this long after it dropped (0 = never).</summary>
        public int RespawnTicks;
        public int RespawnShelves;

        /// <summary>Gone time of tile <paramref name="id"/> before it returns (0 = it never returns).</summary>
        public int RespawnFor(int id) => RespawnTicks > 0 && Shelf(id) < RespawnShelves ? RespawnTicks : 0;

        public int PerShelf => Cols * Rows;
        public int Count => PerShelf * (ShelfTopsMm?.Length ?? 0);
        public int Shelf(int id) => id / PerShelf;

        public void Center(int id, out int x, out int y, out int z)
        {
            var shelf = id / PerShelf;
            var k = id % PerShelf;
            var row = k / Cols;
            var col = k % Cols;
            x = OriginXMm + col * TileMm + TileMm / 2;
            z = OriginZMm + row * TileMm + TileMm / 2;
            y = ShelfTopsMm[shelf] - ThicknessMm / 2;
        }

        /// <summary>The tile under the feet (grounded on a shelf top), or −1. Integer only.</summary>
        public int TileUnder(int fx, int fy, int fz)
        {
            if (ShelfTopsMm == null)
                return -1;
            for (var s = 0; s < ShelfTopsMm.Length; s++)
            {
                if (Math.Abs(fy - ShelfTopsMm[s]) > 80)
                    continue;
                var dx = fx - OriginXMm;
                var dz = fz - OriginZMm;
                if (dx < 0 || dz < 0)
                    return -1;
                var col = dx / TileMm;
                var row = dz / TileMm;
                if (col >= Cols || row >= Rows)
                    return -1;
                return s * PerShelf + row * Cols + col;
            }

            return -1;
        }
    }

    /// <summary>
    /// Occupancy-triggered falling block (Goo Tower, M4): standing on it starts a <see cref="DelayTicks"/> shake, then it
    /// falls (collider off) for <see cref="GoneTicks"/> and comes back. Reactive: the server owns the trigger ticks.
    /// </summary>
    [Serializable]
    public struct FallingBlockDef
    {
        public int CenterXMm, CenterYMm, CenterZMm;
        public int HalfXMm, HalfYMm, HalfZMm;
        public int DelayTicks;
        public int GoneTicks;
    }

    /// <summary>
    /// <c>STAR_PICKUP</c> field (Star Scramble, M4): the star timeline is pure (seeded anchors per wave), the collected
    /// state and dropped stars are reactive. Anchors are absolute (static), or offsets riding a moving platform
    /// (<c>Attach = platform index</c>) or a turn table (<c>Attach = 1000 + table index</c>, offset in the table frame).
    /// </summary>
    [Serializable]
    public struct StarFieldDef
    {
        public int[] AnchorsMm;        // x, y, z per anchor
        public int[] Attach;           // per anchor: −1 static, p = platform p, 1000 + t = turn table t
        public int[] Golden;           // anchor indices golden stars can use (the islands)
        public int WaveEveryTicks;     // a wave of WaveSize normal stars every …
        public int WaveSize;
        public int WaveLifeTicks;      // a wave's uncollected stars vanish after …
        public int GoldenEveryTicks;   // a golden star (+3) every … (first at GoldenEveryTicks)
        public int GoldenLifeTicks;
        public int GoldenValue;
        public int PickupRadiusMm;     // horizontal reach beyond the capsule radius
        public int DropPopTicks;       // a dropped star pops for … before it can be grabbed again
        public int DropLifeTicks;
        public int DropDistanceMm;

        public int AnchorCount => AnchorsMm == null ? 0 : AnchorsMm.Length / 3;
    }

    public static partial class HazardMath
    {
        // ------------------------------------------------------------------ goo plane

        /// <summary>Goo surface height (mm) at hazard time <paramref name="t"/>: <c>y0 + v·max(0, t − delay)</c>, integer, capped.</summary>
        public static int GooYMm(in GooDef d, int t)
        {
            var k = t - d.DelayTicks;
            if (k <= 0)
                return d.Y0Mm;
            var y = d.Y0Mm + (long)d.RiseMmPerSec * k / TickRate;
            return y >= d.MaxYMm ? d.MaxYMm : (int)y;
        }

        /// <summary>Hazard time at which the goo surface first reaches <paramref name="yMm"/> (QA: the goo vs the slowest route).</summary>
        public static int GooTicksToReach(in GooDef d, int yMm)
        {
            if (yMm <= d.Y0Mm)
                return d.DelayTicks;
            if (d.RiseMmPerSec <= 0 || yMm > d.MaxYMm)
                return int.MaxValue;
            var k = ((long)(yMm - d.Y0Mm) * TickRate + d.RiseMmPerSec - 1) / d.RiseMmPerSec;
            return d.DelayTicks + (int)k;
        }

        // ------------------------------------------------------------------ star timeline (pure, seeded)

        public const int StarGoldenBase = 512, StarDropBase = 1024;

        /// <summary>FNV-1a over (seed, salt, index): the seeded choices of the star timeline.</summary>
        public static uint StarHash(ulong seed, uint salt, uint index)
        {
            var h = 2166136261u;
            for (var i = 0; i < 8; i++)
            {
                h ^= (byte)(seed >> (8 * i));
                h *= 16777619u;
            }

            for (var i = 0; i < 4; i++)
            {
                h ^= (byte)(salt >> (8 * i));
                h *= 16777619u;
            }

            for (var i = 0; i < 4; i++)
            {
                h ^= (byte)(index >> (8 * i));
                h *= 16777619u;
            }

            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return h;
        }

        /// <summary>
        /// The anchor of timeline star <paramref name="id"/> and its spawn / expiry hazard ticks, or false if the id is not a
        /// timeline star. Normal star id = wave · WaveSize + k; golden id = <see cref="StarGoldenBase"/> + g (g ≥ 1).
        /// Each wave places its stars on WaveSize distinct non-golden anchors (seeded partial shuffle).
        /// </summary>
        public static bool StarSlot(in StarFieldDef d, ulong seed, int id, out int anchor, out int spawnT, out int expireT, out int value)
        {
            anchor = -1;
            spawnT = expireT = 0;
            value = 1;
            var n = d.AnchorCount;
            if (n == 0 || d.WaveSize <= 0)
                return false;
            if (id >= StarGoldenBase && id < StarDropBase)
            {
                var g = id - StarGoldenBase;
                if (g < 1 || d.Golden == null || d.Golden.Length == 0 || d.GoldenEveryTicks <= 0)
                    return false;
                anchor = d.Golden[(int)(StarHash(seed, 0x60_1D, (uint)g) % (uint)d.Golden.Length)];
                spawnT = g * d.GoldenEveryTicks;
                expireT = spawnT + d.GoldenLifeTicks;
                value = d.GoldenValue;
                return true;
            }

            if (id < 0 || id >= StarGoldenBase)
                return false;
            var wave = id / d.WaveSize;
            var k = id % d.WaveSize;
            // Candidates: the non-golden anchors.
            Span<int> pool = stackalloc int[64];
            var m = 0;
            for (var i = 0; i < n && m < pool.Length; i++)
            {
                var golden = false;
                if (d.Golden != null)
                    foreach (var gi in d.Golden)
                        golden |= gi == i;
                if (!golden)
                    pool[m++] = i;
            }

            if (k >= m)
                return false;
            for (var i = 0; i <= k; i++)
            {
                var j = i + (int)(StarHash(seed, 0x57A2, (uint)(wave * 64 + i)) % (uint)(m - i));
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            anchor = pool[k];
            spawnT = wave * d.WaveEveryTicks;
            expireT = spawnT + d.WaveLifeTicks;
            return true;
        }

        /// <summary>Position (mm) of anchor <paramref name="a"/> at hazard time <paramref name="t"/> (static, on a platform, or on a turn table).</summary>
        public static void StarAnchorMm(in StarFieldDef d, HazardSet h, int a, int t, out int x, out int y, out int z)
        {
            x = d.AnchorsMm[a * 3];
            y = d.AnchorsMm[a * 3 + 1];
            z = d.AnchorsMm[a * 3 + 2];
            var att = d.Attach != null && a < d.Attach.Length ? d.Attach[a] : -1;
            if (att >= 1000)
            {
                var ti = att - 1000;
                if ((uint)ti >= (uint)h.TurnTables.Length)
                    return;
                ref readonly var tt = ref h.TurnTables[ti];
                var p = TurnTablePhase(in tt, t);
                long c = CosQ30(p), s = SinQ30(p);
                var wx = RoundShift(x * c - z * s, 30);
                var wz = RoundShift(x * s + z * c, 30);
                x = tt.CenterXMm + (int)wx;
                z = tt.CenterZMm + (int)wz;
                y += tt.CenterYMm;
            }
            else if (att >= 0)
            {
                if ((uint)att >= (uint)h.Platforms.Length)
                    return;
                PlatformCenterMm(in h.Platforms[att], t, out var px, out var py, out var pz);
                x += px;
                y += py + h.Platforms[att].HalfYMm;
                z += pz;
            }
        }

        /// <summary>Is a star at (sx, sy, sz) touched by the capsule (feet fx/fy/fz, radius r, height hgt)? Integer mm.</summary>
        public static bool StarTouch(int sx, int sy, int sz, int fx, int fy, int fz, int r, int hgt, int reach)
        {
            if (sy < fy - 300 || sy > fy + hgt + 300)
                return false;
            long dx = sx - fx, dz = sz - fz;
            long rr = r + reach;
            return dx * dx + dz * dz <= rr * rr;
        }

        /// <summary>A dropped star's position at <paramref name="age"/> ticks: a 1 m hop to its landing point over the pop time.</summary>
        public static void DropPosMm(in StarFieldDef d, int x0, int y0, int z0, uint seedish, int age, out int x, out int y, out int z)
        {
            var dir = (byte)(seedish * 2654435761u >> 24);
            var ex = x0 + Yaw256.SinQ(dir) * d.DropDistanceMm / Yaw256.Scale;
            var ez = z0 + Yaw256.CosQ(dir) * d.DropDistanceMm / Yaw256.Scale;
            var pop = Math.Max(1, d.DropPopTicks);
            if (age >= pop)
            {
                x = ex;
                y = y0;
                z = ez;
                return;
            }

            x = x0 + (ex - x0) * age / pop;
            z = z0 + (ez - z0) * age / pop;
            y = y0 + 4000 * age * (pop - age) / (pop * pop); // ≤ 1 m arc
        }
    }
}
