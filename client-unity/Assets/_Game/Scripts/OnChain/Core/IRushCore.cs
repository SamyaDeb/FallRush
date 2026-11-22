using System;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-A: rush-core for the on-chain client, one interface over the two builds of the same C ABI
    /// (<c>crates/rush-core-wasm/src/abi.rs</c>, built from P2's branch; see <c>Plugins/rush_core_version.txt</c>):
    /// <see cref="RushCoreNativeCore"/> (<c>Plugins/macOS/rush_core.bundle</c>, Editor + Mac player) and
    /// <see cref="RushCoreWasmCore"/> (<c>StreamingAssets/rush_core.wasm</c> through <c>RushCoreWasm.jslib</c>, WebGL).
    /// Same calls, same results (EditMode + <c>?rcselftest=1</c> parity). Calls are batched so the WebGL bridge is
    /// crossed about once per frame: <see cref="Run"/> rebases on a World and steps n ticks in one call.
    /// </summary>
    public interface IRushCore
    {
        /// <summary>False until the WASM module is instantiated (native: always true once loaded).</summary>
        bool Ready { get; }
        /// <summary>Non-null when the core failed to load.</summary>
        string Error { get; }
        /// <summary><c>rc_version</c>: ABI &lt;&lt; 16 | rush-core version.</summary>
        uint Version { get; }

        /// <summary><c>rc_init</c>: a handle (≥ 0) or −1. <paramref name="cfg"/> = 24 B (<see cref="RushCoreAbi.Cfg"/>).</summary>
        int Init(byte[] map, byte[] cfg);
        void Free(int h);

        /// <summary><c>rc_set_world</c>: true when the bytes decode.</summary>
        bool SetWorld(int h, byte[] world, int len);
        /// <summary><c>rc_world_bytes</c>: bytes written.</summary>
        int WorldBytes(int h, byte[] dst);

        /// <summary>
        /// One batched call: if <paramref name="worldLen"/> &gt; 0, <c>rc_set_world</c> first; then <paramref name="n"/>
        /// × <c>rc_step</c> with <paramref name="inputs"/>[k·72 … k·72+72) (12 × 6 B records); after each tick the 36-B
        /// <c>rc_player</c> record of every slot in <paramref name="slotMask"/> goes to
        /// <paramref name="outPlayers"/>[(k·12 + slot)·36]. Returns the ticks stepped.
        /// </summary>
        int Run(int h, byte[] world, int worldLen, int n, byte[] inputs, uint slotMask, byte[] outPlayers);

        /// <summary>All 12 <c>rc_player</c> records (12 × 36 B) of the current World.</summary>
        void Players(int h, byte[] out432);

        ulong Hash(int h);
        /// <summary><c>rc_clock</c>: tick | rate &lt;&lt; 32.</summary>
        ulong Clock(int h);
        ulong Vectors(uint kind, ulong seed, uint n, byte[] map);
    }

    /// <summary>ABI constants and record helpers shared by both cores.</summary>
    public static class RushCoreAbi
    {
        public const int MaxPlayers = 12;
        public const int InputRecord = 6;
        public const int InputsPerTick = MaxPlayers * InputRecord; // 72
        public const int PlayerRecord = 36;
        public const int WorldMaxBytes = 2048;

        public static byte[] Cfg(int rate, uint active, uint bots, ulong seed)
        {
            var b = new byte[24];
            BitConverter.GetBytes((uint)rate).CopyTo(b, 0);
            BitConverter.GetBytes(active).CopyTo(b, 4);
            BitConverter.GetBytes(bots).CopyTo(b, 8);
            BitConverter.GetBytes(seed).CopyTo(b, 16);
            return b;
        }

        /// <summary>Writes one present 6-B input record (present, move x, move y, buttons, cam yaw LE).</summary>
        public static void PutInput(byte[] inputs, int offset, int slot, sbyte mx, sbyte my, byte buttons, ushort camYaw)
        {
            var o = offset + slot * InputRecord;
            inputs[o] = 1;
            inputs[o + 1] = (byte)mx;
            inputs[o + 2] = (byte)my;
            inputs[o + 3] = buttons;
            inputs[o + 4] = (byte)camYaw;
            inputs[o + 5] = (byte)(camYaw >> 8);
        }

        /// <summary>The platform's core: WASM in WebGL players, the native bundle elsewhere.</summary>
        public static IRushCore Create()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new RushCoreWasmCore();
#else
            return new RushCoreNativeCore();
#endif
        }
    }
}
