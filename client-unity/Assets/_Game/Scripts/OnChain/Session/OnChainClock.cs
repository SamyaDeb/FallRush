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
        private readonly double[] _tick = new double[Window];
        private readonly double[] _at = new double[Window];
        private int _n, _i;
        private readonly int _rate;
        public double LastRecvAt { get; private set; } = double.NaN;
        public uint NewestTick { get; private set; }
        public float RttMs = float.NaN;
        public float LagMs = 300f;
        /// <summary>
        /// Input run 2 (2026-11-25): cap of the one-way delay added to the freshest World. RttMs is the bundle's HTTP
        /// getSlot probe, which queues behind the input sends and polls in the browser (measured 700–1,900 ms), so RTT/2
        /// put our clock 10–28 ticks ahead of the arena: production match EmLEdj…, 307 of 412 refused input batches were
        /// stamped too far ahead. A pushed World already carries its delay; the true one-way to the ER is tens of ms.
        /// </summary>
        public float MaxOneWayMs = 60f;
        public long Samples;

        public OnChainClock(int rate) => _rate = rate;

        // input run 3: the disciplined clock (see ChainNow)
        private bool _discInit;
        private double _discOff, _discAt; // the disciplined clock's tick at _discAt
        /// <summary>Largest forward / backward slew of the disciplined clock (fraction of real time) and the snap threshold (ticks).</summary>
        public double SlewUp = 0.20, SlewDown = 0.01, SnapTicks = 90; // a World can only arrive after its tick: the raw estimate is a lower bound, so it pulls the clock up fast and down barely
        public long Snaps;
        /// <summary>false = the raw estimate (the old behaviour; tests and A/B).</summary>
        public bool Disciplined = true;
        /// <summary>Input run 3: the rollup runs exactly <c>rate</c> ticks/s; a rate measured from bunched, late arrivals
        /// read 15–20/s in gaps and walked the estimate seconds off. Only the local stack (20/s) needs the measured rate.</summary>
        public bool NominalRateOnly;

        public void Reset()
        {
            _discInit = false;
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
            _tick[_i] = tick;
            _at[_i] = recvAt;
            _i = (_i + 1) % Window;
            if (_n < Window)
                _n++;
            LastRecvAt = recvAt;
            Samples++;
        }

        public bool HasData => _n > 0;

        /// <summary>
        /// Measured tick rate (ticks/s) from the World arrivals of the last 4 s (≥ 1.5 s span), else the nominal rate.
        /// The local stack runs 20 ticks/s (50 ms slots: 0.66 × real time); devnet-as runs 30.
        /// </summary>
        public double Rate(double now)
        {
            if (NominalRateOnly)
                return _rate;
            int lo = -1, hi = -1;
            for (var k = 0; k < _n; k++)
            {
                if (now - _at[k] > 4.0)
                    continue;
                if (lo < 0 || _at[k] < _at[lo])
                    lo = k;
                if (hi < 0 || _at[k] > _at[hi])
                    hi = k;
            }

            if (lo < 0 || _at[hi] - _at[lo] < 1.5)
                return _rate;
            var r = (_tick[hi] - _tick[lo]) / (_at[hi] - _at[lo]);
            // only a clearly slower chain (the local stack: 20/s) overrides the nominal rate: on devnet-as the chain runs
            // at exactly 30/s and an estimate from bunched arrivals would only add error (late inputs, measured)
            return r < 5 || r > _rate * 0.85 ? _rate : r;
        }

        /// <summary>
        /// The chain's current tick (fractional) at <paramref name="now"/>. Input run 3 (2026-11-25): disciplined. The
        /// arena runs a steady 30 ticks/s (measured from the account: +15 per 0.5 s), but its Worlds reach the browser late
        /// and in bursts (lateness p95 up to 4.8 s, update gaps of seconds). The raw estimate (freshest World of the last
        /// 2 s) then jumped back when the fresh samples aged out, the prediction found itself ahead of the target, and
        /// AdvanceAndSend resynced every frame without sending inputs (229 resyncs in 10 s at 60 fps; inputs applied 13 %).
        /// Now the clock runs on wall time and the raw estimate only steers it: forward ≤ 20 %, backward ≤ 1 % of real
        /// time, a snap only past 3 s (a new match / a long outage).
        /// </summary>
        public double ChainNow(double now)
        {
            var raw = RawChainNow(now);
            if (!Disciplined || _n == 0)
                return raw;
            if (!_discInit || Math.Abs(raw - Project(now)) > SnapTicks)
            {
                if (_discInit)
                    Snaps++;
                _discInit = true;
                _discOff = raw; // the clock's tick at _discAt
                _discAt = now;
                return raw;
            }

            if (now <= _discAt)
                return _discOff;
            // integrate: advance at the chain rate, then steer toward the raw estimate within the slew limits, so a
            // rate re-estimate or an aged-out sample can never step it back
            var rate = Rate(now);
            var dt = now - _discAt;
            var pred = _discOff + rate * dt;
            var err = raw - pred;
            pred += err > 0 ? Math.Min(err, SlewUp * rate * dt) : Math.Max(err, -SlewDown * rate * dt);
            _discOff = pred;
            _discAt = now;
            return pred;
        }

        private double Project(double now) => _discOff + Rate(now) * Math.Max(0, now - _discAt);

        /// <summary>The raw estimate: freshest World of the last 2 s + a capped one-way delay.</summary>
        public double RawChainNow(double now)
        {
            if (_n == 0)
                return 0;
            var rate = Rate(now);
            var best = double.MinValue;
            for (var k = 0; k < _n; k++)
                if (now - _at[k] <= 2.0)
                    best = Math.Max(best, _tick[k] - _at[k] * rate);
            if (best == double.MinValue)
            {
                var j = (_i + Window - 1) % Window;
                best = _tick[j] - _at[j] * rate;
            }

            var oneWay = float.IsNaN(RttMs) ? 45f : Math.Min(MaxOneWayMs, RttMs * 0.5f);
            return now * rate + best + oneWay * 0.001 * rate;
        }

        public bool Lagging(double now) => double.IsNaN(LastRecvAt) || (now - LastRecvAt) * 1000.0 > LagMs;

        public float AgeMs(double now) => double.IsNaN(LastRecvAt) ? float.NaN : (float)((now - LastRecvAt) * 1000.0);
    }
}
