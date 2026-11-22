using System;
using System.Runtime.InteropServices;

namespace FallRush.FeelLab
{
    /// <summary>
    /// P/Invoke into <c>Plugins/macOS/rush_core.bundle</c> (crates/rush-core-ffi, the same C ABI as rush_core.wasm; see
    /// <c>crates/rush-core-wasm/src/abi.rs</c>). Feel Lab and the P1 EditMode tests only.
    /// </summary>
    public static class RushCoreNative
    {
        private const string Lib = "rush_core";

        [DllImport(Lib)] public static extern uint rc_version();
        [DllImport(Lib)] public static extern uint rc_world_size();
        [DllImport(Lib)] public static extern int rc_init(byte[] map, uint mapLen, byte[] cfg);
        [DllImport(Lib)] public static extern void rc_free(int h);
        [DllImport(Lib)] public static extern uint rc_step(int h, byte[] inputs);
        [DllImport(Lib)] public static extern ulong rc_hash(int h);
        [DllImport(Lib)] public static extern int rc_player(int h, uint slot, byte[] out36);
        [DllImport(Lib)] public static extern int rc_set_player(int h, uint slot, byte[] in33);
        [DllImport(Lib)] public static extern int rc_set_rate(int h, uint rate);
        [DllImport(Lib)] public static extern int rc_respawn(int h, uint slot);
        [DllImport(Lib)] public static extern ulong rc_clock(int h);
        [DllImport(Lib)] public static extern ulong rc_vectors(uint kind, ulong seed, uint n, byte[] map, uint mapLen);

        /// <summary>The 24-byte init config: rate, active mask, bot mask, flags, hazard seed.</summary>
        public static byte[] Cfg(int rate, uint active, uint bots, ulong seed)
        {
            var b = new byte[24];
            BitConverter.GetBytes((uint)rate).CopyTo(b, 0);
            BitConverter.GetBytes(active).CopyTo(b, 4);
            BitConverter.GetBytes(bots).CopyTo(b, 8);
            BitConverter.GetBytes(seed).CopyTo(b, 16);
            return b;
        }

        /// <summary>A decoded <c>rc_player</c> record (36 B: motor state + slot flags, respawn ticks, last step).</summary>
        public struct Player
        {
            public int X, Y, Z, VX, VY, VZ; // mm, mm/s
            public byte Yaw, Flags, Stun, Coyote, Buffer, DiveCd, Land, Immunity, Surface, SlotFlags, Respawn, LastStep;
        }

        public static Player Decode(byte[] b)
        {
            int I(int o) => BitConverter.ToInt32(b, o);
            return new Player
            {
                X = I(0), Y = I(4), Z = I(8), VX = I(12), VY = I(16), VZ = I(20),
                Yaw = b[24], Flags = b[25], Stun = b[26], Coyote = b[27], Buffer = b[28], DiveCd = b[29], Land = b[30], Immunity = b[31], Surface = b[32],
                SlotFlags = b[33], Respawn = b[34], LastStep = b[35],
            };
        }

        public static byte[] Encode(in Player p)
        {
            var b = new byte[33];
            BitConverter.GetBytes(p.X).CopyTo(b, 0);
            BitConverter.GetBytes(p.Y).CopyTo(b, 4);
            BitConverter.GetBytes(p.Z).CopyTo(b, 8);
            BitConverter.GetBytes(p.VX).CopyTo(b, 12);
            BitConverter.GetBytes(p.VY).CopyTo(b, 16);
            BitConverter.GetBytes(p.VZ).CopyTo(b, 20);
            b[24] = p.Yaw; b[25] = p.Flags; b[26] = p.Stun; b[27] = p.Coyote; b[28] = p.Buffer; b[29] = p.DiveCd; b[30] = p.Land; b[31] = p.Immunity; b[32] = p.Surface;
            return b;
        }

        /// <summary>One input record (6 B): present, move x, move y, buttons, cam yaw LE.</summary>
        public static void PutInput(byte[] inputs, int slot, sbyte mx, sbyte my, byte buttons, ushort camYaw)
        {
            var o = slot * 6;
            inputs[o] = 1;
            inputs[o + 1] = (byte)mx;
            inputs[o + 2] = (byte)my;
            inputs[o + 3] = buttons;
            inputs[o + 4] = (byte)camYaw;
            inputs[o + 5] = (byte)(camYaw >> 8);
        }
    }
}
