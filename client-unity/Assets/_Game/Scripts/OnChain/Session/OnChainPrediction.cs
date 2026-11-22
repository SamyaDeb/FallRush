using System;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-D: own-player prediction on rush-core (the M2b pattern with <see cref="IRushCore"/>).
    /// <list type="bullet">
    /// <item>A tick-keyed input history (the commands we stamped and sent) and a tick-keyed record of our predicted
    /// player.</item>
    /// <item>Each authoritative World (tick w): compare our predicted player at w with the arena's; a difference over
    /// 5 mm is a correction. Then always rebase: set the World and replay w+1 … present with our inputs (other humans
    /// "missing" = rush-core repeats their last command; bots run their brain inside step, so they are exact).</item>
    /// <item>Each local tick: one step with the new command.</item>
    /// <item>Rendering: the two newest predicted ticks interpolated, plus a correction offset that decays over
    /// <c>smoothingMs</c> (120 ms); over <c>snapMeters</c> (2 m) it snaps.</item>
    /// </list>
    /// One batched core call per World update (<see cref="IRushCore.Run"/>).
    /// </summary>
    public sealed class OnChainPrediction : IDisposable
    {
        public const int Hist = 128;
        private readonly IRushCore _core;
        private readonly int _h;
        public readonly int Slot;
        private readonly byte[] _cmd = new byte[Hist * 5];
        private readonly uint[] _cmdTick = new uint[Hist];
        private readonly byte[] _rec = new byte[Hist * 36];
        private readonly uint[] _recTick = new uint[Hist];
        private readonly byte[] _inputs = new byte[64 * RushCoreAbi.InputsPerTick];
        private readonly byte[] _out = new byte[64 * 12 * 36];
        private readonly byte[] _auth = new byte[36];
        private readonly float _smoothing, _snap;

        public uint Present { get; private set; }
        public bool HasState { get; private set; }
        public Vector3 RenderOffset { get; private set; }

        // stats
        public long Corrections, Rebases, Snaps, ReplayTicks, Matches, LateInputs;
        public int LastReplay, MaxReplay;
        public float LastCorrectionM, MaxCorrectionM;
        private readonly double[] _corrAt = new double[256];
        private int _corrI;

        public OnChainPrediction(IRushCore core, byte[] map, int rate, uint activeMask, uint botMask, int slot, float smoothingMs, float snapMeters)
        {
            _core = core;
            Slot = slot;
            _smoothing = Mathf.Max(1f, smoothingMs) / 1000f;
            _snap = snapMeters;
            _h = core.Init(map, RushCoreAbi.Cfg(rate, activeMask, botMask, 0));
            if (_h < 0)
                throw new InvalidOperationException("rc_init failed for the prediction world");
        }

        public void Dispose() => _core.Free(_h);

        /// <summary>Stores our command for <paramref name="tick"/> (what we sent).</summary>
        public void SetInput(uint tick, in InputCmd c)
        {
            var i = (int)(tick % Hist);
            _cmdTick[i] = tick;
            var o = i * 5;
            _cmd[o] = (byte)c.MoveX;
            _cmd[o + 1] = (byte)c.MoveY;
            _cmd[o + 2] = c.Buttons;
            _cmd[o + 3] = (byte)c.CamYaw;
            _cmd[o + 4] = (byte)(c.CamYaw >> 8);
        }

        private void PutTick(int k, uint tick)
        {
            var o = k * RushCoreAbi.InputsPerTick;
            Array.Clear(_inputs, o, RushCoreAbi.InputsPerTick);
            var i = (int)(tick % Hist);
            if (_cmdTick[i] != tick)
                return; // no command (missing): rush-core repeats our last one, as the arena does
            var s = o + Slot * 6;
            _inputs[s] = 1;
            Buffer.BlockCopy(_cmd, i * 5, _inputs, s + 1, 5);
        }

        private void Keep(int k, uint tick)
        {
            var i = (int)(tick % Hist);
            _recTick[i] = tick;
            Buffer.BlockCopy(_out, (k * 12 + Slot) * 36, _rec, i * 36, 36);
        }

        /// <summary>Advances the predicted World to <paramref name="tick"/> (one step per tick, ≤ 64).</summary>
        public void StepTo(uint tick)
        {
            if (!HasState || tick <= Present)
                return;
            var n = (int)Math.Min(64, tick - Present);
            for (var k = 0; k < n; k++)
                PutTick(k, Present + 1 + (uint)k);
            _core.Run(_h, null, 0, n, _inputs, 1u << Slot, _out);
            for (var k = 0; k < n; k++)
                Keep(k, Present + 1 + (uint)k);
            Present += (uint)n;
        }

        /// <summary>
        /// An authoritative World of tick <paramref name="w"/>: count a correction if our prediction for w was off,
        /// then rebase and replay to <paramref name="present"/>.
        /// </summary>
        public void OnAuthoritative(byte[] world, int offset, int len, uint w, uint present, double now, uint appliedTick)
        {
            var view = new WorldView(world, offset, len);
            view.PlayerRecord(Slot, _auth, 0);
            var before = HasState ? RenderFeet(1f) : Vector3.zero;
            var i = (int)(w % Hist);
            if (HasState && _recTick[i] == w)
            {
                var dx = BitConverter.ToInt32(_rec, i * 36) - BitConverter.ToInt32(_auth, 0);
                var dy = BitConverter.ToInt32(_rec, i * 36 + 4) - BitConverter.ToInt32(_auth, 4);
                var dz = BitConverter.ToInt32(_rec, i * 36 + 8) - BitConverter.ToInt32(_auth, 8);
                var errM = Mathf.Sqrt((float)dx * dx + (float)dy * dy + (float)dz * dz) * 0.001f;
                if (errM > 0.005f)
                {
                    Corrections++;
                    _corrAt[_corrI++ % _corrAt.Length] = now;
                    LastCorrectionM = errM;
                    if (errM > MaxCorrectionM)
                        MaxCorrectionM = errM;
                    if (appliedTick < w && _cmdTick[i] == w)
                        LateInputs++;
                }
                else
                    Matches++;
            }

            if (present < w)
                present = w;
            var n = (int)Math.Min(64, present - w);
            for (var k = 0; k < n; k++)
                PutTick(k, w + 1 + (uint)k);
            var buf = offset == 0 ? world : Slice(world, offset, len);
            _core.Run(_h, buf, len, n, _inputs, 1u << Slot, _out);
            // the authoritative record itself
            _recTick[i] = w;
            Buffer.BlockCopy(_auth, 0, _rec, i * 36, 36);
            for (var k = 0; k < n; k++)
                Keep(k, w + 1 + (uint)k);
            Present = w + (uint)n;
            Rebases++;
            ReplayTicks += n;
            LastReplay = n;
            if (n > MaxReplay)
                MaxReplay = n;
            if (!HasState)
            {
                HasState = true;
                RenderOffset = Vector3.zero;
                return;
            }

            var after = RenderFeet(1f);
            var off = RenderOffset + before - after;
            if (off.magnitude > _snap)
            {
                Snaps++;
                off = Vector3.zero;
            }

            RenderOffset = off;
        }

        private byte[] _slice;

        private byte[] Slice(byte[] b, int o, int len)
        {
            _slice ??= new byte[RushCoreAbi.WorldMaxBytes + 64];
            Buffer.BlockCopy(b, o, _slice, 0, len);
            return _slice;
        }

        public void UpdateSmoothing(float dt)
        {
            if (RenderOffset == Vector3.zero)
                return;
            var k = Mathf.Exp(-dt / (_smoothing / 3f)); // ~95 % gone after smoothingMs
            RenderOffset *= k;
            if (RenderOffset.sqrMagnitude < 1e-6f)
                RenderOffset = Vector3.zero;
        }

        public int CorrectionsSince(double now, double seconds)
        {
            var c = 0;
            foreach (var t in _corrAt)
                if (t > 0 && now - t <= seconds)
                    c++;
            return c;
        }

        private static Vector3 Pos(byte[] r, int o) => new Vector3(BitConverter.ToInt32(r, o), BitConverter.ToInt32(r, o + 4), BitConverter.ToInt32(r, o + 8)) * 0.001f;

        /// <summary>Feet between the two newest predicted ticks (no smoothing offset).</summary>
        public Vector3 RenderFeet(float frac)
        {
            var a = (int)(Present % Hist);
            var b = (int)((Present + Hist - 1) % Hist);
            var pa = Pos(_rec, a * 36);
            if (_recTick[b] != Present - 1)
                return pa;
            return Vector3.Lerp(Pos(_rec, b * 36), pa, Mathf.Clamp01(frac));
        }

        /// <summary>The newest predicted 36-B record (velocity, flags, yaw, stun).</summary>
        public void Newest(out Vector3 vel, out byte flags, out byte yaw, out byte stun, out byte respawn)
        {
            var o = (int)(Present % Hist) * 36;
            vel = new Vector3(BitConverter.ToInt32(_rec, o + 12), BitConverter.ToInt32(_rec, o + 16), BitConverter.ToInt32(_rec, o + 20)) * 0.001f;
            yaw = _rec[o + 24];
            flags = _rec[o + 25];
            stun = _rec[o + 26];
            respawn = _rec[o + 34];
        }

        public ulong Hash() => _core.Hash(_h);
    }
}
