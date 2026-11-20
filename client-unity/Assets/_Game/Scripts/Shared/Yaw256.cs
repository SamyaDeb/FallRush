namespace FallRush.Shared
{
    /// <summary>
    /// Facing in 256 steps (u8 on the wire). Yaw 0 faces +Z; increasing yaw turns toward +X (Unity's Y rotation).
    /// The sine table is a literal (round(sin(2πi/256)·32767), generated once with Node), so the motor never calls
    /// Math.Sin and every platform gets bit-identical directions.
    /// </summary>
    public static class Yaw256
    {
        public const int Scale = 32767;

        private static readonly short[] Sin =
        {
            0, 804, 1608, 2410, 3212, 4011, 4808, 5602, 6393, 7179, 7962, 8739, 9512, 10278, 11039, 11793,
            12539, 13279, 14010, 14732, 15446, 16151, 16846, 17530, 18204, 18868, 19519, 20159, 20787, 21403, 22005, 22594,
            23170, 23731, 24279, 24811, 25329, 25832, 26319, 26790, 27245, 27683, 28105, 28510, 28898, 29268, 29621, 29956,
            30273, 30571, 30852, 31113, 31356, 31580, 31785, 31971, 32137, 32285, 32412, 32521, 32609, 32678, 32728, 32757,
            32767, 32757, 32728, 32678, 32609, 32521, 32412, 32285, 32137, 31971, 31785, 31580, 31356, 31113, 30852, 30571,
            30273, 29956, 29621, 29268, 28898, 28510, 28105, 27683, 27245, 26790, 26319, 25832, 25329, 24811, 24279, 23731,
            23170, 22594, 22005, 21403, 20787, 20159, 19519, 18868, 18204, 17530, 16846, 16151, 15446, 14732, 14010, 13279,
            12539, 11793, 11039, 10278, 9512, 8739, 7962, 7179, 6393, 5602, 4808, 4011, 3212, 2410, 1608, 804,
            0, -804, -1608, -2410, -3212, -4011, -4808, -5602, -6393, -7179, -7962, -8739, -9512, -10278, -11039, -11793,
            -12539, -13279, -14010, -14732, -15446, -16151, -16846, -17530, -18204, -18868, -19519, -20159, -20787, -21403, -22005, -22594,
            -23170, -23731, -24279, -24811, -25329, -25832, -26319, -26790, -27245, -27683, -28105, -28510, -28898, -29268, -29621, -29956,
            -30273, -30571, -30852, -31113, -31356, -31580, -31785, -31971, -32137, -32285, -32412, -32521, -32609, -32678, -32728, -32757,
            -32767, -32757, -32728, -32678, -32609, -32521, -32412, -32285, -32137, -31971, -31785, -31580, -31356, -31113, -30852, -30571,
            -30273, -29956, -29621, -29268, -28898, -28510, -28105, -27683, -27245, -26790, -26319, -25832, -25329, -24811, -24279, -23731,
            -23170, -22594, -22005, -21403, -20787, -20159, -19519, -18868, -18204, -17530, -16846, -16151, -15446, -14732, -14010, -13279,
            -12539, -11793, -11039, -10278, -9512, -8739, -7962, -7179, -6393, -5602, -4808, -4011, -3212, -2410, -1608, -804,        };

        public static int SinQ(int yaw) => Sin[yaw & 255];

        public static int CosQ(int yaw) => Sin[(yaw + 64) & 255];

        /// <summary>Unit direction on the XZ plane (x = sin, z = cos), float from the integer table.</summary>
        public static void Dir(byte yaw, out float x, out float z)
        {
            x = SinQ(yaw) / (float)Scale;
            z = CosQ(yaw) / (float)Scale;
        }

        /// <summary>Nearest of the 256 directions to the integer vector (x, z) (integer arithmetic only). (0,0) returns <paramref name="fallback"/>.</summary>
        public static byte FromVector(int x, int z, byte fallback)
        {
            if (x == 0 && z == 0)
                return fallback;
            long best = long.MinValue;
            var bestI = 0;
            for (var i = 0; i < 256; i++)
            {
                long d = (long)x * SinQ(i) + (long)z * CosQ(i);
                if (d > best)
                {
                    best = d;
                    bestI = i;
                }
            }

            return (byte)bestI;
        }

        /// <summary>Camera yaw (u16, 65536 per turn) → facing (u8).</summary>
        public static byte FromCamYaw(ushort camYaw) => (byte)(camYaw >> 8);
    }
}
