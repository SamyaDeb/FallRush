using System;
using FallRush.Shared;

namespace FallRush.Net
{
    /// <summary>
    /// Local-player prediction and reconciliation (doc 04 §4.3), history keyed by tick.
    /// <list type="number">
    /// <item><see cref="Predict"/>: every sampled command (tick T) is simulated at once with the shared
    /// <see cref="PlayerStep"/> on the local <see cref="IMotorWorld"/> posed for T, and stored as history[T] = (cmd, state).</item>
    /// <item><see cref="Reconcile"/>: a snapshot for server tick S carries our authoritative state after S. If it is
    /// bit-identical to history[S] nothing happens. Otherwise we rewind to it and replay the stored commands S+1 … T.
    /// A difference above <see cref="CorrectionThreshold"/> (or in flags / stun) counts as a <b>correction</b>; smaller
    /// ones are fixed silently (<see cref="SilentResyncs"/>).</item>
    /// <item>Visual smoothing: the jump in the predicted present goes into <see cref="RenderOffset"/>, which decays to
    /// ~5 % in <see cref="SmoothSeconds"/>; jumps over <see cref="SnapDistance"/> (and respawns) snap.</item>
    /// </list>
    /// Engine-free apart from the namespace, so EditMode tests drive it against the analytic world.
    /// </summary>
    public sealed class ClientPrediction
    {
        public const int HistorySize = 128;            // ticks (4.3 s at 30 Hz) — far more than lead + RTT
        public const float CorrectionThreshold = 0.05f; // m (doc 04 §4.3)
        public const float SnapDistance = 2f;          // m
        public const float SmoothSeconds = 0.12f;      // offset → ~5 % (exp decay, tau = SmoothSeconds / 3)
        private const int CorrectionLog = 256;

        private struct Entry
        {
            public uint Tick;
            public bool HasCmd;
            public bool HasState;
            public InputCmd Cmd;
            public MotorState State;
        }

        private readonly Entry[] _h = new Entry[HistorySize];
        private readonly KinematicMotor _motor;
        private readonly IMotorWorld _world;
        private readonly HazardSet _hazards;
        private readonly double[] _correctionTimes = new double[CorrectionLog];
        private int _correctionHead;
        private InputCmd _lastCmd;
        private uint _newestCmdTick;
        private bool _hasCmd;

        public ClientPrediction(MovementConfig cfg, IMotorWorld world, HazardSet hazards)
        {
            _motor = new KinematicMotor(cfg);
            _world = world;
            _hazards = hazards ?? HazardSet.Empty;
        }

        public MovementConfig Cfg => _motor.Cfg;
        public HazardSet Hazards => _hazards;

        public bool HasState { get; private set; }

        /// <summary>Predicted state after <see cref="Tick"/> (the newest sampled command).</summary>
        public MotorState State { get; private set; }

        /// <summary>Predicted state after <c>Tick − 1</c> (render interpolation start).</summary>
        public MotorState PrevState { get; private set; }

        public uint Tick { get; private set; }

        /// <summary>What the last <see cref="Predict"/> step did (for visuals: jump/land/dive events).</summary>
        public KinematicMotor.StepEvents LastEvents { get; private set; }
        public PlayerStep.Result LastResult { get; private set; }

        /// <summary>Set during a round's intro / countdown: a server teleport (everyone back on their spawn) is adopted, not counted.</summary>
        public bool TeleportsExpected;

        /// <summary>Render-space error being blended out (m), added to the interpolated predicted position.</summary>
        public Vec3 RenderOffset;

        // ---- stats (HUD, probe, gate) ----
        public long Snapshots;          // snapshots reconciled
        public long Matches;            // bit-identical at S
        public long SilentResyncs;      // differed by ≤ threshold, same flags: replayed without counting
        public long Corrections;        // > threshold or flags/stun differ, with no server-only cause (respawns and pushes excluded)
        public long PushCorrections;    // same, but the server reported a player–player push since the last snapshot (not predicted by design)
        public long HazardMismatches;   // stun start/state differed at S (predicted hit ≠ server hit)
        public long HazardCorrections;  // counted corrections whose stun/immunity state differed (subset of Corrections)
        public long Respawns;           // server respawn teleports adopted (not corrections)
        public long Adopted;            // state taken wholesale (start, history miss)
        public long ReplayTicksTotal;
        public long ReplayCount;
        public int LastReplay;
        public int MaxReplay;
        public float LastCorrection;    // m, jump of the predicted present on the last correction
        public float MaxCorrection;
        public double SumCorrection;
        public float LastErrorAtS;      // m, |auth − predicted| at S on the last mismatch
        public double SumDriftMm;       // position error at S over unexplained mismatches (no push, no respawn)
        public long DriftSamples;
        public float MaxDriftMm;
        public long SubMmMismatches;    // mismatches whose position error was < 1 mm (float drift between platforms)
        public long PredictedHits;      // sweeper hits in fresh prediction (not replays)

        /// <summary>The last mismatch (diagnostics): server tick, authoritative and predicted state at that tick.</summary>
        public uint LastMismatchTick;
        public MotorState LastMismatchAuth, LastMismatchPredicted;

        public void Reset()
        {
            Array.Clear(_h, 0, _h.Length);
            HasState = false;
            RenderOffset = Vec3.Zero;
            _lastCmd = default;
            _hasCmd = false;
            _newestCmdTick = 0;
        }

        /// <summary>Simulates the command just sampled for <c>cmd.Tick</c> (call after sending it).</summary>
        public void Predict(in InputCmd cmd)
        {
            ref var e = ref _h[cmd.Tick % HistorySize];
            e = new Entry { Tick = cmd.Tick, HasCmd = true, Cmd = cmd };
            var hadCmd = _hasCmd;
            _newestCmdTick = cmd.Tick;
            _hasCmd = true;
            if (!HasState)
            {
                _lastCmd = cmd;
                return; // first snapshot not here yet: keep the command for the replay
            }

            if (cmd.Tick <= Tick)
            {
                _lastCmd = cmd;
                return; // cannot happen with TickSync (ticks never go back); keep it safe
            }

            if (cmd.Tick - Tick > HistorySize / 2)
            {
                // Long stall (hidden tab, R12): don't simulate seconds of gap in one frame. Wait for the next
                // snapshot and adopt it (the server simulated the gap with neutral input anyway).
                HasState = false;
                _lastCmd = cmd;
                return;
            }

            // A clock re-sync skipped ticks: simulate them the way the server will (repeat, then neutral).
            var s = State;
            var prev = State;
            var missing = hadCmd ? 0 : InputSanitizer.RepeatTicks + 1;
            var fillFrom = hadCmd ? _lastCmd : default;
            for (var t = Tick + 1; t < cmd.Tick; t++)
            {
                prev = s;
                StepOne(ref s, ServerFill(in fillFrom, ref missing, t), t);
                ref var g = ref _h[t % HistorySize];
                g = new Entry { Tick = t, HasState = true, State = s };
            }

            prev = s;
            var r = StepOne(ref s, in cmd, cmd.Tick);
            if ((r & PlayerStep.Result.HazardHit) != 0)
                PredictedHits++;
            LastResult = r;
            LastEvents = _motor.Last;
            e.HasState = true;
            e.State = s;
            PrevState = prev;
            State = s;
            Tick = cmd.Tick;
            _lastCmd = cmd;
        }

        /// <summary>Reconciles with our authoritative state after server tick <paramref name="serverTick"/>.</summary>
        /// <param name="now">Local seconds (for the corrections-per-window log).</param>
        public void Reconcile(uint serverTick, in PlayerState auth, in SnapHeader h, double now)
        {
            var a = FromSnapshot(in auth, in h);
            Snapshots++;

            if (!HasState || serverTick >= Tick)
            {
                // Start, or the server is at/after our newest prediction (clock not settled): adopt and replay what we have.
                var first = !HasState;
                Adopted++;
                HasState = true;
                Rewind(in a, serverTick, out _);
                if (first || RenderOffset.SqrMagnitude > SnapDistance * SnapDistance)
                    RenderOffset = Vec3.Zero;
                return;
            }

            ref var e = ref _h[serverTick % HistorySize];
            if (!e.HasState || e.Tick != serverTick)
            {
                Adopted++;
                var before = State.Position;
                Rewind(in a, serverTick, out _);
                AddOffset(before - State.Position, snap: false);
                return;
            }

            var p = e.State;
            if (a.Equals(p))
            {
                Matches++;
                return;
            }

            var err = (a.Position - p.Position).Magnitude;
            // Fell out / teleported to a spawn: not a prediction error (the client never predicts the respawn point).
            var respawn = a.Has(MotorState.Respawning) || p.Has(MotorState.Respawning) || (TeleportsExpected && err > SnapDistance);
            var pushed = (h.OwnEvents & SnapHeader.EventPushed) != 0;
            if (!respawn)
            {
                LastMismatchTick = serverTick;
                LastMismatchAuth = a;
                LastMismatchPredicted = p;
                var errMm = err * 1000f;
                if (!pushed)
                {
                    // Drift statistics only where nothing server-only happened: this is the motor/world divergence.
                    SumDriftMm += errMm;
                    DriftSamples++;
                    if (errMm > MaxDriftMm)
                        MaxDriftMm = errMm;
                    if (errMm < 1f)
                        SubMmMismatches++;
                }

                LastErrorAtS = err;
            }

            var flagsDiffer = ((a.Flags ^ p.Flags) & ~MotorState.Bot) != 0 || a.StunTicks != p.StunTicks;
            var hazardish = (a.StunTicks > 0) != (p.StunTicks > 0) || (a.ImmunityTicks > 0) != (p.ImmunityTicks > 0);
            if (hazardish)
                HazardMismatches++;

            var beforePos = State.Position;
            Rewind(in a, serverTick, out var replayed);
            var shift = (beforePos - State.Position).Magnitude;

            if (respawn)
            {
                Respawns++;
                RenderOffset = Vec3.Zero; // teleport
                return;
            }

            if ((err > CorrectionThreshold || flagsDiffer) && pushed)
            {
                PushCorrections++;
                _correctionTimes[_correctionHead++ % CorrectionLog] = now;
            }
            else if (err > CorrectionThreshold || flagsDiffer)
            {
                Corrections++;
                if (hazardish)
                    HazardCorrections++;
                LastCorrection = shift;
                SumCorrection += shift;
                if (shift > MaxCorrection)
                    MaxCorrection = shift;
                _correctionTimes[_correctionHead++ % CorrectionLog] = now;
            }
            else
            {
                SilentResyncs++;
            }

            AddOffset(beforePos - State.Position, snap: false);
        }

        /// <summary>Corrections in the last <paramref name="seconds"/> (≤ 256 remembered).</summary>
        public int CorrectionsSince(double now, double seconds)
        {
            var n = 0;
            var count = Math.Min(_correctionHead, CorrectionLog);
            for (var i = 0; i < count; i++)
                if (now - _correctionTimes[i] <= seconds)
                    n++;
            return n;
        }

        /// <summary>Decays <see cref="RenderOffset"/> (call once per rendered frame).</summary>
        public void UpdateSmoothing(float dt)
        {
            if (RenderOffset.SqrMagnitude < 1e-8f)
            {
                RenderOffset = Vec3.Zero;
                return;
            }

            var k = (float)Math.Exp(-dt * 3.0 / SmoothSeconds);
            RenderOffset *= k;
        }

        /// <summary>Predicted feet for rendering: PrevState → State by <paramref name="frac"/> (0..1 of the current tick), plus the smoothing offset.</summary>
        public Vec3 RenderPosition(float frac)
        {
            if (frac < 0f) frac = 0f;
            if (frac > 1f) frac = 1f;
            var a = PrevState.Position;
            var b = State.Position;
            if ((b - a).SqrMagnitude > SnapDistance * SnapDistance)
                return b + RenderOffset;
            return a + (b - a) * frac + RenderOffset;
        }

        private void AddOffset(Vec3 delta, bool snap)
        {
            var o = RenderOffset + delta;
            RenderOffset = snap || o.SqrMagnitude > SnapDistance * SnapDistance ? Vec3.Zero : o;
        }

        private void Rewind(in MotorState auth, uint fromTick, out int replayed)
        {
            var s = auth;
            ref var e0 = ref _h[fromTick % HistorySize];
            if (e0.Tick != fromTick)
                e0 = new Entry { Tick = fromTick };
            e0.HasState = true;
            e0.State = s;

            // Replay every tick after S up to the newest command we have sent (or predicted).
            var last = Tick > fromTick ? Tick : fromTick;
            if (_hasCmd && _newestCmdTick > last)
                last = _newestCmdTick;
            var prev = s;
            var prevCmd = e0.HasCmd ? e0.Cmd : default;
            var missing = e0.HasCmd ? 0 : InputSanitizer.RepeatTicks + 1;
            replayed = 0;
            for (var t = fromTick + 1; t <= last; t++)
            {
                ref var e = ref _h[t % HistorySize];
                InputCmd cmd;
                if (e.Tick == t && e.HasCmd)
                {
                    cmd = e.Cmd;
                    prevCmd = cmd;
                    missing = 0;
                }
                else
                {
                    cmd = ServerFill(in prevCmd, ref missing, t);
                    e = new Entry { Tick = t };
                }

                prev = s;
                StepOne(ref s, in cmd, t);
                e.HasState = true;
                e.State = s;
                replayed++;
            }

            PrevState = replayed > 0 ? prev : s;
            State = s;
            Tick = last;
            LastReplay = replayed;
            if (replayed > MaxReplay)
                MaxReplay = replayed;
            ReplayTicksTotal += replayed;
            ReplayCount++;
        }

        /// <summary>
        /// What the server simulates for a tick we sent no command for (InputSanitizer.Consume): the previous command
        /// with press edges cleared for up to <see cref="InputSanitizer.RepeatTicks"/> ticks, then neutral.
        /// </summary>
        private static InputCmd ServerFill(in InputCmd prev, ref int missing, uint tick)
        {
            missing++;
            var c = missing <= InputSanitizer.RepeatTicks ? Repeat(in prev, tick) : new InputCmd { Tick = tick };
            return c;
        }

        private PlayerStep.Result StepOne(ref MotorState s, in InputCmd cmd, uint tick)
        {
            _world.BeginTick(tick);
            return PlayerStep.Run(_motor, ref s, in cmd, tick, _world, _hazards);
        }

        private static InputCmd Repeat(in InputCmd last, uint tick)
        {
            var c = last;
            c.Buttons = (byte)(c.Buttons & ~InputCmd.EdgeButtons);
            c.Tick = tick;
            return c;
        }

        /// <summary>Our full motor state from the snapshot entry plus the owner-only timers in the header.</summary>
        public static MotorState FromSnapshot(in PlayerState p, in SnapHeader h) => new MotorState
        {
            PosX = p.PosX, PosY = p.PosY, PosZ = p.PosZ,
            VelX = p.VelX, VelY = p.VelY, VelZ = p.VelZ,
            Yaw = p.Yaw,
            Flags = (byte)(p.Flags & ~MotorState.Bot),
            StunTicks = p.StunTicks,
            CoyoteTicks = h.OwnCoyote,
            JumpBufferTicks = h.OwnJumpBuffer,
            DiveCooldownTicks = h.OwnDiveCooldown,
            LandTicks = h.OwnLand,
            ImmunityTicks = h.OwnImmunity,
            Surface = h.OwnSurface,
        };
    }
}
