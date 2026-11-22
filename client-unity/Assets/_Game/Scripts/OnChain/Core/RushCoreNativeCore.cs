using System;
using FallRush.FeelLab;

namespace FallRush.OnChain
{
    /// <summary>P3-A: <see cref="IRushCore"/> over <c>rush_core.bundle</c> (P/Invoke, <see cref="RushCoreNative"/>).</summary>
    public sealed class RushCoreNativeCore : IRushCore
    {
        private readonly byte[] _one = new byte[RushCoreAbi.PlayerRecord];
        private readonly byte[] _tick = new byte[RushCoreAbi.InputsPerTick];

        public RushCoreNativeCore()
        {
            try
            {
                Version = RushCoreNative.rc_version();
                Ready = true;
            }
            catch (Exception e)
            {
                Error = "rush_core.bundle not loaded: " + e.Message;
            }
        }

        public bool Ready { get; }
        public string Error { get; }
        public uint Version { get; }

        public int Init(byte[] map, byte[] cfg) => RushCoreNative.rc_init(map, (uint)map.Length, cfg);
        public void Free(int h) => RushCoreNative.rc_free(h);

        public bool SetWorld(int h, byte[] world, int len)
        {
            if (len == world.Length)
                return RushCoreNative.rc_set_world(h, world, (uint)len) == 0;
            var b = new byte[len];
            Buffer.BlockCopy(world, 0, b, 0, len);
            return RushCoreNative.rc_set_world(h, b, (uint)len) == 0;
        }

        public int WorldBytes(int h, byte[] dst) => (int)RushCoreNative.rc_world_bytes(h, dst, (uint)dst.Length);

        public int Run(int h, byte[] world, int worldLen, int n, byte[] inputs, uint slotMask, byte[] outPlayers)
        {
            if (worldLen > 0 && !SetWorld(h, world, worldLen))
                return 0;
            for (var k = 0; k < n; k++)
            {
                if (inputs == null)
                    RushCoreNative.rc_step(h, null);
                else
                {
                    Buffer.BlockCopy(inputs, k * RushCoreAbi.InputsPerTick, _tick, 0, RushCoreAbi.InputsPerTick);
                    RushCoreNative.rc_step(h, _tick);
                }

                if (outPlayers == null || slotMask == 0)
                    continue;
                for (var s = 0; s < RushCoreAbi.MaxPlayers; s++)
                {
                    if ((slotMask >> s & 1) == 0)
                        continue;
                    RushCoreNative.rc_player(h, (uint)s, _one);
                    Buffer.BlockCopy(_one, 0, outPlayers, (k * RushCoreAbi.MaxPlayers + s) * RushCoreAbi.PlayerRecord, RushCoreAbi.PlayerRecord);
                }
            }

            return n;
        }

        public void Players(int h, byte[] out432)
        {
            for (var s = 0; s < RushCoreAbi.MaxPlayers; s++)
            {
                RushCoreNative.rc_player(h, (uint)s, _one);
                Buffer.BlockCopy(_one, 0, out432, s * RushCoreAbi.PlayerRecord, RushCoreAbi.PlayerRecord);
            }
        }

        public ulong Hash(int h) => RushCoreNative.rc_hash(h);
        public ulong Clock(int h) => RushCoreNative.rc_clock(h);
        public ulong Vectors(uint kind, ulong seed, uint n, byte[] map) => RushCoreNative.rc_vectors(kind, seed, n, map, map == null ? 0u : (uint)map.Length);
    }
}
