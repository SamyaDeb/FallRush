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
    /// <para>Smooth run (2026-11-25): the same steps also keep every other slot's predicted record (rush-core steps the
    /// whole World anyway), so bots, whose brain runs inside step and is exact, are drawn at the predicted present
    /// instead of from late, bursty Worlds. Each slot has its own correction offset (decays over smoothingMs, snaps over
    /// snapMeters), like the own player.</para>
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
        public readonly uint BotMask;
        /// <summary>Slots whose records rush-core writes out each tick: every active slot (before: the own slot only).</summary>
        private readonly uint _outMask;
        private const int Slots = 12;
        private readonly byte[] _all = new byte[Hist * Slots * 36];
        private readonly uint[] _allTick = new uint[Hist];
        private readonly Vector3[] _offAll = new Vector3[Slots];
        private readonly Vector3[] _beforeAll = new Vector3[Slots];
        private readonly byte[] _authAll = new byte[Slots * 36];
        public long SlotCorrections, SlotSnaps;

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
            BotMask = botMask;
            _outMask = (activeMask | (slot >= 0 ? 1u << slot : 0u)) & 0xFFFu;
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
            if (Slot < 0)
                return; // spectator world: no own inputs (every slot repeats its last command, bots run their brain)
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
            if (Slot >= 0)
                Buffer.BlockCopy(_out, (k * 12 + Slot) * 36, _rec, i * 36, 36);
            _allTick[i] = tick;
            Buffer.BlockCopy(_out, k * 12 * 36, _all, i * Slots * 36, Slots * 36);
        }

        /// <summary>Advances the predicted World to <paramref name="tick"/> (one step per tick, ≤ 64).</summary>
        public void StepTo(uint tick)
        {
            if (!HasState || tick <= Present)
                return;
            var n = (int)Math.Min(64, tick - Present);
            for (var k = 0; k < n; k++)
                PutTick(k, Present + 1 + (uint)k);
            _core.Run(_h, null, 0, n, _inputs, _outMask, _out);
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
            var own = Slot >= 0;
            if (own)
                view.PlayerRecord(Slot, _auth, 0);
            var before = HasState && own ? RenderFeet(1f) : Vector3.zero;
            for (var s = 0; s < Slots; s++)
            {
                view.PlayerRecord(s, _authAll, s * 36);
                _beforeAll[s] = HasState && RawSlot(s, 1f, out var bf) ? bf : new Vector3(float.NaN, 0f, 0f);
            }
            var i = (int)(w % Hist);
            if (own && HasState && _recTick[i] == w)
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
            _core.Run(_h, buf, len, n, _inputs, _outMask, _out);
            // the authoritative record itself
            _recTick[i] = w;
            if (own)
                Buffer.BlockCopy(_auth, 0, _rec, i * 36, 36);
            _allTick[i] = w;
            Buffer.BlockCopy(_authAll, 0, _all, i * Slots * 36, Slots * 36);
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
                Array.Clear(_offAll, 0, Slots);
                return;
            }

            // every other slot: carry the rebase jump into its offset (decays in UpdateSmoothing), snap past snapMeters
            for (var s = 0; s < Slots; s++)
            {
                if (s == Slot || float.IsNaN(_beforeAll[s].x) || !RawSlot(s, 1f, out var af))
                {
                    _offAll[s] = Vector3.zero;
                    continue;
                }
                var o = _offAll[s] + _beforeAll[s] - af;
                if (o.magnitude > _snap)
                {
                    SlotSnaps++;
                    o = Vector3.zero;
                }
                else if ((_beforeAll[s] - af).sqrMagnitude > 0.005f * 0.005f)
                    SlotCorrections++;
                _offAll[s] = o;
            }

            if (own)
            {
                var after = RenderFeet(1f);
                var off = RenderOffset + before - after;
                if (off.magnitude > _snap)
                {
                    Snaps++;
                    off = Vector3.zero;
                }

                RenderOffset = off;
            }
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

        /// <summary>Decays the other slots' correction offsets (call once per frame).</summary>
        public void UpdateSlotSmoothing(float dt)
        {
            var k = Mathf.Exp(-dt / (_smoothing / 3f));
            for (var s = 0; s < Slots; s++)
            {
                var o = _offAll[s] * k;
                _offAll[s] = o.sqrMagnitude < 1e-6f ? Vector3.zero : o;
            }
        }

        /// <summary>A slot's predicted feet between the two newest ticks, without its offset; false = no record.</summary>
        private bool RawSlot(int slot, float frac, out Vector3 feet)
        {
            feet = Vector3.zero;
            if (slot < 0 || slot >= Slots)
                return false;
            var a = (int)(Present % Hist);
            if (_allTick[a] != Present)
                return false;
            var pa = Pos(_all, (a * Slots + slot) * 36);
            var b = (int)((Present + Hist - 1) % Hist);
            feet = _allTick[b] != Present - 1 ? pa : Vector3.Lerp(Pos(_all, (b * Slots + slot) * 36), pa, Mathf.Clamp01(frac));
            return true;
        }

        /// <summary>A slot's render feet at the predicted present (with its correction offset).</summary>
        public bool RenderSlot(int slot, float frac, out Vector3 feet)
        {
            if (!HasState || !RawSlot(slot, frac, out feet))
            {
                feet = Vector3.zero;
                return false;
            }
            feet += _offAll[slot];
            return true;
        }

        /// <summary>A slot's newest predicted record (velocity, flags, yaw, stun, respawn ticks).</summary>
        public void NewestSlot(int slot, out Vector3 vel, out byte flags, out byte yaw, out byte stun, out byte respawn)
        {
            var o = ((int)(Present % Hist) * Slots + slot) * 36;
            vel = new Vector3(BitConverter.ToInt32(_all, o + 12), BitConverter.ToInt32(_all, o + 16), BitConverter.ToInt32(_all, o + 20)) * 0.001f;
            yaw = _all[o + 24];
            flags = _all[o + 25];
            stun = _all[o + 26];
            respawn = _all[o + 34];
        }

        public bool IsBot(int slot) => slot >= 0 && slot < Slots && (BotMask & (1u << slot)) != 0;

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
