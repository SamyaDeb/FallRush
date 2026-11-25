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
    /// <para><b>Profiles</b> (smooth run, 2026-11-25): <see cref="Profile.Classic"/> is the game server's tuning above (the
    /// constants; unchanged). <see cref="Profile.OnChain"/> is for rollup Worlds, which arrive late and in bursts (confirm
    /// p50 ≈ 0.5 s, 0.5–10 complete Worlds/s in the load tests): a 200–600 ms adaptive delay, eased extrapolation up to
    /// 350 ms, a speed-based teleport test (two Worlds 0.5 s apart are often &gt; 2 m apart while running) and a per-slot
    /// blend of any remaining jump over <see cref="Profile.BlendMs"/>, so a late World never shows as a teleport.</para>
    /// Engine-free (times are passed in), so EditMode tests drive it.
    /// </summary>
    public sealed class Interpolation
    {
        public const int Ring = 32;
        public const float MinDelay = 0.100f;
        public const float MaxDelay = 0.150f;
        public const float MaxExtrapolation = 0.150f;
        public const float DelaySlewPerSecond = 0.05f;

        /// <summary>Tuning of one feed. Classic = the constants above, exactly (no easing, no blending).</summary>
        public sealed class Profile
        {
            public string Name;
            public float MinDelay, MaxDelay, MaxExtrapolation, DelaySlewPerSecond;
            /// <summary>0 = linear extrapolation (Classic); &gt; 0 = eased: v·τ·(1 − e^(−t/τ)), so a stall slows to a stop.</summary>
            public float EaseTau;
            /// <summary>Two buffered states further apart than this (m) are a teleport (snap).</summary>
            public float SnapMeters;
            /// <summary>&gt; 0: a pair is a teleport only if it is also faster than this (m/s); 0 = distance only (Classic).</summary>
            public float TeleportSpeed;
            /// <summary>&gt; 0: per-slot blend of render jumps (late World after extrapolation, delay changes) over this many ms.</summary>
            public float BlendMs;
            /// <summary>Jumps larger than this (m) are not blended; a buffered teleport (respawn) snaps at any size.</summary>
            public float BlendSnapMeters;
            /// <summary>&gt; 0: the render clock plays the buffer at 0.5–MaxPlaySpeed× real time to meet its target instead of
            /// jumping (after a stall it catches up fast-forward); also never runs past newest + MaxExtrapolation.</summary>
            public float MaxPlaySpeed;

            public static readonly Profile Classic = new Profile
            {
                Name = "classic", MinDelay = Interpolation.MinDelay, MaxDelay = Interpolation.MaxDelay,
                MaxExtrapolation = Interpolation.MaxExtrapolation, DelaySlewPerSecond = Interpolation.DelaySlewPerSecond,
                EaseTau = 0f, SnapMeters = 2f, TeleportSpeed = 0f, BlendMs = 0f, BlendSnapMeters = 0f, MaxPlaySpeed = 0f,
            };

            public static readonly Profile OnChain = new Profile
            {
                Name = "onchain", MinDelay = 0.200f, MaxDelay = 0.600f, MaxExtrapolation = 0.350f, DelaySlewPerSecond = 0.20f,
                EaseTau = 0.200f, SnapMeters = 2f, TeleportSpeed = 18f, BlendMs = 300f, BlendSnapMeters = 10f, MaxPlaySpeed = 2f,
            };
        }

        private Profile _p = Profile.Classic;

        /// <summary>The active tuning (Classic by default). Switching resets the per-slot blends.</summary>
        public Profile Tuning
        {
            get => _p;
            set
            {
                if (value == null || ReferenceEquals(value, _p))
                    return;
                _p = value;
                Array.Clear(_blend, 0, _blend.Length);
                _playInit = false;
                Delay = TargetDelay = Math.Max(_p.MinDelay, Math.Min(_p.MaxDelay, Delay));
            }
        }

        private struct Blend
        {
            public bool Valid, Teleported;
            public long Frame;
            public Vec3 Raw, Rate, Off, Out;
        }

        private const int MaxSlots = 64;
        private readonly Blend[] _blend = new Blend[MaxSlots];
        private float _frameDt;
        private double _play;
        private bool _playInit, _heldAtCap;
        /// <summary>Render clock lag behind its target (s, on-chain profile): &gt; 0 while catching up.</summary>
        public float CatchUpSeconds { get; private set; }

        /// <summary>Per-slot blends applied / teleport snaps (on-chain profile only).</summary>
        public long Blends, BlendSnaps;

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
            Delay = TargetDelay = _p.MinDelay;
            Lagging = Extrapolating = false;
            Array.Clear(_blend, 0, _blend.Length);
            _playInit = false;
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
            _frameDt = Math.Max(0f, frameDt);

            var spacingTicks = _spacing.Count > 0 ? _spacing.Percentile(0.5f) : 2f;
            var lateP95 = _lateness.Count > 0 ? _lateness.Percentile(0.95f) : 0f;
            LatenessP95Ms = lateP95 * 1000f;
            var target = (float)(spacingTicks * _dt + _dt) + lateP95;
            TargetDelay = target < _p.MinDelay ? _p.MinDelay : target > _p.MaxDelay ? _p.MaxDelay : target;
            var step = _p.DelaySlewPerSecond * Math.Max(0f, frameDt);
            Delay = Delay < TargetDelay ? Math.Min(TargetDelay, Delay + step) : Math.Max(TargetDelay, Delay - step);

            _renderTick = (now + _offset - Delay) / _dt;
            var newestTick = _ring[_newest].Tick;
            if (_p.MaxPlaySpeed > 0f)
                _renderTick = PlayClock(_renderTick, newestTick);
            Extrapolating = false;
            var wasLagging = Lagging;
            Lagging = false;
            _extra = 0;

            if (_renderTick >= newestTick)
            {
                _aIdx = _bIdx = _newest;
                _t = 1f;
                _extra = (_renderTick - newestTick) * _dt;
                if (_extra > _p.MaxExtrapolation || _p.MaxPlaySpeed > 0f && _heldAtCap)
                {
                    _extra = _p.MaxExtrapolation;
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

        /// <summary>
        /// On-chain profile: a render clock that never jumps. It advances at real time, sped up (≤ MaxPlaySpeed×) or slowed
        /// (≥ 0.5×) toward the target with a 0.25 s time constant, and holds at newest + MaxExtrapolation during a stall.
        /// After the stall the backlog plays fast-forward instead of the remote players jumping to the present.
        /// </summary>
        private double PlayClock(double target, uint newestTick)
        {
            var cap = newestTick + _p.MaxExtrapolation / _dt;
            var desired = Math.Min(target, cap);
            if (!_playInit || Math.Abs(desired - _play) * _dt > 3.0)
            {
                _play = desired; // first frame / a long outage (> 3 s): start over at the target
                _playInit = true;
            }
            else
            {
                var adv = _frameDt / _dt;
                var next = _play + adv;
                var err = desired - next;
                var k = 1.0 - Math.Exp(-_frameDt / 0.25);
                var corr = Math.Max(-0.5 * adv, Math.Min((_p.MaxPlaySpeed - 1.0) * adv, err * k));
                next += corr;
                if (next > cap)
                    next = Math.Max(_play, cap);
                _play = next;
            }

            CatchUpSeconds = (float)((target - _play) * _dt);
            _heldAtCap = target > cap + 1e-6 && _play >= cap - 1e-6;
            return _play;
        }

        /// <summary>Interpolated (or extrapolated ≤ 150 ms, then frozen) state of <paramref name="slot"/> at this frame's render time.</summary>
        public bool Sample(int slot, out Vec3 feet, out Vec3 vel, out PlayerState nearest)
        {
            if (!SampleRaw(slot, out feet, out vel, out nearest))
                return false;
            if (_p.BlendMs > 0f && slot >= 0 && slot < MaxSlots)
                feet = Smooth(slot, feet, vel);
            return true;
        }

        /// <summary>
        /// On-chain profile: rendered = raw + offset. A frame-to-frame jump of the raw position beyond what its velocity
        /// explains (a late World after extrapolation, a delay change) moves into the offset, which decays over BlendMs;
        /// jumps over BlendSnapMeters (respawn) snap. Idempotent within a frame (several views sample the same slot).
        /// </summary>
        private Vec3 Smooth(int slot, Vec3 raw, Vec3 vel)
        {
            ref var b = ref _blend[slot];
            if (b.Valid && b.Frame == Frames)
                return b.Out;
            var off = Vec3.Zero;
            var rate = Vec3.Zero;
            if (b.Valid && Frames - b.Frame == 1 && _frameDt > 0f)
            {
                // the raw path's own rate (not the snapshot velocity: between two sparse Worlds the path is a straight
                // line, so a jump arc's vertical velocity would read as a jump every frame)
                var expected = b.Raw + b.Rate * _frameDt;
                var jump = raw - expected;
                var jm = jump.Magnitude;
                off = b.Off;
                rate = (raw - b.Raw) / _frameDt;
                // a buffered teleport (respawn, reset) snaps; any other jump (catch-up, delay change) is blended
                if (jm >= _p.BlendSnapMeters || _rawTeleport && jm > 0.5f)
                {
                    off = Vec3.Zero;
                    rate = Vec3.Zero;
                    BlendSnaps++;
                }
                else if (jm > Math.Max(0.05f, b.Rate.Magnitude * _frameDt * 0.5f))
                {
                    off = off - jump;
                    rate = b.Rate; // the jump is not motion: keep the path's rate
                    Blends++;
                }

                var k = (float)Math.Exp(-_frameDt / (_p.BlendMs / 1000f / 3f)); // ~95 % gone after BlendMs
                off = off * k;
                if (off.SqrMagnitude < 1e-6f || off.Magnitude > _p.BlendSnapMeters)
                    off = Vec3.Zero;
            }
            else
                rate = vel;

            b.Valid = true;
            b.Frame = Frames;
            b.Raw = raw;
            b.Rate = rate;
            b.Off = off;
            b.Out = raw + off;
            return b.Out;
        }

        private bool _rawTeleport;

        private bool SampleRaw(int slot, out Vec3 feet, out Vec3 vel, out PlayerState nearest)
        {
            _rawTeleport = false;
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
                    // On-chain: eased (v·τ·(1 − e^(−t/τ))) so a long gap slows the player to a stop instead of a hard freeze.
                    var e = _p.EaseTau > 0f ? _p.EaseTau * (1.0 - Math.Exp(-_extra / _p.EaseTau)) : _extra;
                    feet = new Vec3(feet.X + vel.X * (float)e, feet.Y, feet.Z + vel.Z * (float)e);
                }

                nearest = pb;
                return true;
            }

            var fa = Pos(in pa);
            var fb = Pos(in pb);
            // Big jumps (respawn teleports) snap instead of sliding across the map. On-chain Worlds can be 0.5 s+ apart,
            // so a running player covers > 2 m between two of them: there a pair is a teleport only if it is also faster
            // than TeleportSpeed.
            var tp = IsTeleport(in pa, in pb, fa, fb);
            _rawTeleport = tp;
            feet = tp ? fb : fa + (fb - fa) * _t;
            var va = Vel(in pa);
            vel = va + (Vel(in pb) - va) * _t;
            nearest = _t < 0.5f ? pa : pb;
            return true;
        }

        private bool IsTeleport(in PlayerState pa, in PlayerState pb, Vec3 fa, Vec3 fb)
        {
            var d2 = (fb - fa).SqrMagnitude;
            if (d2 <= _p.SnapMeters * _p.SnapMeters)
                return false;
            if (_p.TeleportSpeed <= 0f)
                return true;
            var span = (double)(_ring[_bIdx].Tick - _ring[_aIdx].Tick) * _dt;
            return span <= 0 || Math.Sqrt(d2) / span > _p.TeleportSpeed;
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
