using System;
using FallRush.Shared;

namespace FallRush.Net
{
    /// <summary>
    /// Remote-player snapshot interpolation (doc 04 §4.4).
    /// <para><c>renderTick = (now + offset − delay) / dt</c>, where <c>offset</c> is the smoothed
    /// (server tick time − local arrival time) of the freshest snapshots, i.e. a server-time estimate.</para>
    /// <para><b>Adaptive delay:</b> <c>delay = clamp(snapInterval + dt + lateness_p95, 100 ms, 150 ms)</c>, where
    /// <c>lateness</c> is how much later than the freshest-arrival line each of the last 64 snapshots came in. At 15 Hz with
    /// no jitter that is 66.7 + 33.3 = 100 ms. It moves toward its target at ≤ 5 % of real time (no visible time warps).</para>
    /// <para><b>Starvation:</b> past the newest snapshot it extrapolates with the snapshot velocity for up to 150 ms;
    /// beyond that it freezes there and sets <see cref="Lagging"/> (the view shows a "lagging" icon).</para>
    /// Engine-free (times are passed in), so EditMode tests drive it.
    /// </summary>
    public sealed class Interpolation
    {
        public const int Ring = 32;
        public const float MinDelay = 0.100f;
        public const float MaxDelay = 0.150f;
        public const float MaxExtrapolation = 0.150f;
        public const float DelaySlewPerSecond = 0.05f;

        public struct Snap
        {
            public uint Tick;
            public double ReceivedAt;
            public int Count;
            public PlayerState[] Players;
        }

        private readonly Snap[] _ring = new Snap[Ring];
        private readonly double _dt;
        private readonly RollingSamples _lateness = new RollingSamples(64);
        private readonly RollingSamples _spacing = new RollingSamples(16);
        private int _newest = -1;
        private int _stored;
        private double _offset;
        private bool _offsetInit;
        private double _renderTick;
        private int _aIdx, _bIdx;
        private float _t;
        private double _extra;

        public Interpolation(double dt)
        {
            _dt = dt;
            for (var i = 0; i < Ring; i++)
                _ring[i].Players = new PlayerState[Protocol.MaxPlayers];
        }

        /// <summary>Current interpolation delay (s).</summary>
        public float Delay { get; private set; } = MinDelay;
        public float TargetDelay { get; private set; } = MinDelay;
        public bool Lagging { get; private set; }
        public bool Extrapolating { get; private set; }
        public double RenderTick => _renderTick;
        public bool HasData => _newest >= 0;
        public ref readonly Snap Newest => ref _ring[_newest];

        /// <summary>Server-time estimate (in ticks) at <paramref name="now"/>.</summary>
        public double ServerTickEstimate(double now) => (now + _offset) / _dt;

        // ---- stats ----
        public long Frames, ExtrapolatedFrames, FrozenFrames, LagEvents;
        public float LatenessP95Ms { get; private set; }

        public void Reset()
        {
            _newest = -1;
            _stored = 0;
            _offsetInit = false;
            _lateness.Clear();
            _spacing.Clear();
            Delay = TargetDelay = MinDelay;
            Lagging = Extrapolating = false;
        }

        /// <summary>Stores a snapshot (newer ticks only).</summary>
        public bool Add(uint tick, ReadOnlySpan<PlayerState> players, double now)
        {
            if (_newest >= 0 && tick <= _ring[_newest].Tick)
                return false;
            if (_newest >= 0)
                _spacing.Add(tick - _ring[_newest].Tick);
            var next = (_newest + 1) % Ring;
            ref var s = ref _ring[next];
            players.CopyTo(s.Players);
            s.Tick = tick;
            s.ReceivedAt = now;
            s.Count = players.Length;
            _newest = next;
            if (_stored < Ring)
                _stored++;

            var offset = tick * _dt - now;
            if (!_offsetInit)
            {
                _offset = offset;
                _offsetInit = true;
            }
            else
            {
                // Fresher (earlier-than-expected) arrivals pull the line forward fast; late ones drift it back slowly.
                _offset += (offset - _offset) * (offset > _offset ? 0.2 : 0.01);
            }

            var late = _offset - offset;
            _lateness.Add((float)(late > 0 ? late : 0));
            return true;
        }

        /// <summary>Picks the render time for this frame; call once per frame before <see cref="Sample"/>.</summary>
        public void BeginFrame(double now, float frameDt)
        {
            if (_newest < 0)
                return;
            Frames++;

            var spacingTicks = _spacing.Count > 0 ? _spacing.Percentile(0.5f) : 2f;
            var lateP95 = _lateness.Count > 0 ? _lateness.Percentile(0.95f) : 0f;
            LatenessP95Ms = lateP95 * 1000f;
            var target = (float)(spacingTicks * _dt + _dt) + lateP95;
            TargetDelay = target < MinDelay ? MinDelay : target > MaxDelay ? MaxDelay : target;
            var step = DelaySlewPerSecond * Math.Max(0f, frameDt);
            Delay = Delay < TargetDelay ? Math.Min(TargetDelay, Delay + step) : Math.Max(TargetDelay, Delay - step);

            _renderTick = (now + _offset - Delay) / _dt;
            var newestTick = _ring[_newest].Tick;
            Extrapolating = false;
            var wasLagging = Lagging;
            Lagging = false;
            _extra = 0;

            if (_renderTick >= newestTick)
            {
                _aIdx = _bIdx = _newest;
                _t = 1f;
                _extra = (_renderTick - newestTick) * _dt;
                if (_extra > MaxExtrapolation)
                {
                    _extra = MaxExtrapolation;
                    Lagging = true;
                    FrozenFrames++;
                    if (!wasLagging)
                        LagEvents++;
                }
                else if (_extra > 1e-4)
                {
                    Extrapolating = true;
                    ExtrapolatedFrames++;
                }

                return;
            }

            // a = newest snapshot at or before renderTick, b = the one after it.
            _bIdx = _newest;
            _aIdx = -1;
            for (var k = 1; k < _stored; k++)
            {
                var i = (_newest - k + Ring) % Ring;
                if (_ring[i].Tick <= _renderTick)
                {
                    _aIdx = i;
                    break;
                }

                _bIdx = i;
            }

            if (_aIdx < 0)
            {
                _aIdx = _bIdx; // older than everything we hold: show the oldest
                _t = 0f;
                return;
            }

            var span = (double)(_ring[_bIdx].Tick - _ring[_aIdx].Tick);
            _t = span > 0 ? (float)((_renderTick - _ring[_aIdx].Tick) / span) : 1f;
        }

        /// <summary>Interpolated (or extrapolated ≤ 150 ms, then frozen) state of <paramref name="slot"/> at this frame's render time.</summary>
        public bool Sample(int slot, out Vec3 feet, out Vec3 vel, out PlayerState nearest)
        {
            feet = vel = Vec3.Zero;
            nearest = default;
            if (_newest < 0)
                return false;
            if (!Find(in _ring[_bIdx], slot, out var pb))
                return false;
            if (_aIdx == _bIdx || !Find(in _ring[_aIdx], slot, out var pa))
            {
                feet = Pos(in pb);
                vel = Vel(in pb);
                if (_extra > 0)
                {
                    // Horizontal only: vertical extrapolation of jumps looks worse than a short hold.
                    feet = new Vec3(feet.X + vel.X * (float)_extra, feet.Y, feet.Z + vel.Z * (float)_extra);
                }

                nearest = pb;
                return true;
            }

            var fa = Pos(in pa);
            var fb = Pos(in pb);
            // Big jumps (respawn teleports) snap instead of sliding across the map.
            feet = (fb - fa).SqrMagnitude > 4f ? fb : fa + (fb - fa) * _t;
            var va = Vel(in pa);
            vel = va + (Vel(in pb) - va) * _t;
            nearest = _t < 0.5f ? pa : pb;
            return true;
        }

        public bool IsPresent(int slot) => _newest >= 0 && Find(in _ring[_newest], slot, out _);

        public static bool Find(in Snap s, int slot, out PlayerState p)
        {
            for (var i = 0; i < s.Count; i++)
            {
                if (s.Players[i].Slot == slot)
                {
                    p = s.Players[i];
                    return true;
                }
            }

            p = default;
            return false;
        }

        private static Vec3 Pos(in PlayerState p) => new Vec3(p.PosX * 0.001f, p.PosY * 0.001f, p.PosZ * 0.001f);
        private static Vec3 Vel(in PlayerState p) => new Vec3(p.VelX * 0.01f, p.VelY * 0.01f, p.VelZ * 0.01f);
    }
}
