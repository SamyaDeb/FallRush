using System;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-D: the chain's tick from World notifications (replaces the server-snapshot <c>TickSync</c> for on-chain play).
    /// <list type="bullet">
    /// <item>Each World of tick w received at t gives an offset sample w − t·rate; the newest-earliest sample of the last
    /// 2 s (max offset) is the least-delayed one, and one-way ≈ RTT/2 is added: chain now ≈ t·rate + offset + one-way.</item>
    /// <item>The local present = chain now + lead + batch − 1 (inputs for [T+lead, T+lead+batch) leave at T).</item>
    /// <item><see cref="Lagging"/> after <c>lagMs</c> without a World (HUD "! LAGGING", chip Pending).</item>
    /// </list>
    /// </summary>
    public sealed class OnChainClock
    {
        private const int Window = 64;
        private readonly double[] _off = new double[Window];
        private readonly double[] _at = new double[Window];
        private int _n, _i;
        private readonly int _rate;
        public double LastRecvAt { get; private set; } = double.NaN;
        public uint NewestTick { get; private set; }
        public float RttMs = float.NaN;
        public float LagMs = 300f;
        public long Samples;

        public OnChainClock(int rate) => _rate = rate;

        public void Reset()
        {
            _n = _i = 0;
            LastRecvAt = double.NaN;
            NewestTick = 0;
        }

        public void OnWorld(uint tick, double recvAt)
        {
            if (tick + 30 < NewestTick)
                Reset(); // a new arena (tick restarted)
            if (tick > NewestTick || double.IsNaN(LastRecvAt))
                NewestTick = tick;
            _off[_i] = tick - recvAt * _rate;
            _at[_i] = recvAt;
            _i = (_i + 1) % Window;
            if (_n < Window)
                _n++;
            LastRecvAt = recvAt;
            Samples++;
        }

        public bool HasData => _n > 0;

        /// <summary>The chain's current tick (fractional) at <paramref name="now"/>.</summary>
        public double ChainNow(double now)
        {
            if (_n == 0)
                return 0;
            var best = double.MinValue;
            for (var k = 0; k < _n; k++)
                if (now - _at[k] <= 2.0 && _off[k] > best)
                    best = _off[k];
            if (best == double.MinValue)
                best = _off[(_i + Window - 1) % Window];
            var oneWay = float.IsNaN(RttMs) ? 45f : RttMs * 0.5f;
            return now * _rate + best + oneWay * 0.001 * _rate;
        }

        public bool Lagging(double now) => double.IsNaN(LastRecvAt) || (now - LastRecvAt) * 1000.0 > LagMs;

        public float AgeMs(double now) => double.IsNaN(LastRecvAt) ? float.NaN : (float)((now - LastRecvAt) * 1000.0);
    }
}
