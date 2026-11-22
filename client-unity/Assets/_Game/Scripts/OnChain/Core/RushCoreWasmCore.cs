using System;
using System.Runtime.InteropServices;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-A: <see cref="IRushCore"/> over <c>StreamingAssets/rush_core.wasm</c>, instantiated once at boot by
    /// <c>Plugins/WebGL/RushCoreWasm.jslib</c> (async; <see cref="Ready"/> turns true). The jslib keeps one region in the
    /// core's linear memory for World + inputs + player records, so each call copies its bytes once.
    /// WebGL players only; elsewhere every call throws.
    /// </summary>
    public sealed class RushCoreWasmCore : IRushCore
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void rcw_boot(string url);
        [DllImport("__Internal")] private static extern int rcw_state();
        [DllImport("__Internal")] private static extern uint rcw_version();
        [DllImport("__Internal")] private static extern int rcw_init(byte[] map, int mapLen, byte[] cfg);
        [DllImport("__Internal")] private static extern void rcw_free(int h);
        [DllImport("__Internal")] private static extern int rcw_set_world(int h, byte[] world, int len);
        [DllImport("__Internal")] private static extern int rcw_world_bytes(int h, byte[] dst, int cap);
        [DllImport("__Internal")] private static extern int rcw_run(int h, byte[] world, int worldLen, int n, byte[] inputs, int inputsLen, uint slotMask, byte[] outPlayers, int outLen);
        [DllImport("__Internal")] private static extern void rcw_players(int h, byte[] out432);
        [DllImport("__Internal")] private static extern void rcw_hash(int h, byte[] out8);
        [DllImport("__Internal")] private static extern void rcw_clock(int h, byte[] out8);
        [DllImport("__Internal")] private static extern void rcw_vectors(uint kind, uint seedLo, uint seedHi, uint n, byte[] map, int mapLen, byte[] out8);
#else
        private static Exception No() => new PlatformNotSupportedException("rush_core.wasm runs in WebGL players only");
        private static void rcw_boot(string url) => throw No();
        private static int rcw_state() => -1;
        private static uint rcw_version() => throw No();
        private static int rcw_init(byte[] map, int mapLen, byte[] cfg) => throw No();
        private static void rcw_free(int h) => throw No();
        private static int rcw_set_world(int h, byte[] world, int len) => throw No();
        private static int rcw_world_bytes(int h, byte[] dst, int cap) => throw No();
        private static int rcw_run(int h, byte[] world, int worldLen, int n, byte[] inputs, int inputsLen, uint slotMask, byte[] outPlayers, int outLen) => throw No();
        private static void rcw_players(int h, byte[] out432) => throw No();
        private static void rcw_hash(int h, byte[] out8) => throw No();
        private static void rcw_clock(int h, byte[] out8) => throw No();
        private static void rcw_vectors(uint kind, uint seedLo, uint seedHi, uint n, byte[] map, int mapLen, byte[] out8) => throw No();
#endif

        private static bool _booted;
        private readonly byte[] _u64 = new byte[8];
        private uint _version;

        public RushCoreWasmCore()
        {
            if (_booted)
                return;
            _booted = true;
            rcw_boot(UnityEngine.Application.streamingAssetsPath + "/rush_core.wasm");
        }

        /// <summary>0 loading, 1 ready, −1 failed.</summary>
        public static int State => rcw_state();

        public bool Ready => rcw_state() == 1;
        public string Error => rcw_state() < 0 ? "rush_core.wasm failed to load (see the browser console)" : null;

        public uint Version
        {
            get
            {
                if (_version == 0 && Ready)
                    _version = rcw_version();
                return _version;
            }
        }

        public int Init(byte[] map, byte[] cfg) => rcw_init(map, map.Length, cfg);
        public void Free(int h) => rcw_free(h);
        public bool SetWorld(int h, byte[] world, int len) => rcw_set_world(h, world, len) == 0;
        public int WorldBytes(int h, byte[] dst) => rcw_world_bytes(h, dst, dst.Length);

        public int Run(int h, byte[] world, int worldLen, int n, byte[] inputs, uint slotMask, byte[] outPlayers) =>
            rcw_run(h, world, worldLen, n, inputs, inputs?.Length ?? 0, slotMask, outPlayers, outPlayers?.Length ?? 0);

        public void Players(int h, byte[] out432) => rcw_players(h, out432);

        public ulong Hash(int h)
        {
            rcw_hash(h, _u64);
            return BitConverter.ToUInt64(_u64, 0);
        }

        public ulong Clock(int h)
        {
            rcw_clock(h, _u64);
            return BitConverter.ToUInt64(_u64, 0);
        }

        public ulong Vectors(uint kind, ulong seed, uint n, byte[] map)
        {
            rcw_vectors(kind, (uint)seed, (uint)(seed >> 32), n, map, map?.Length ?? 0, _u64);
            return BitConverter.ToUInt64(_u64, 0);
        }
    }
}
