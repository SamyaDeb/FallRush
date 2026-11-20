using System;

namespace FallRush.Shared
{
    /// <summary>
    /// Client tick clock (doc 04 §4.3). Decides which server tick the next input is for, so that commands reach the
    /// server shortly before the tick they are stamped with.
    /// <para><b>Feedback loop.</b> The server measures, for every command it receives, <c>lead = cmd.tick − nextServerTick</c>
    /// and reports the window minimum in each snapshot (<see cref="SnapHeader.InputLead"/>). The client keeps that lead
    /// at <see cref="TargetLead"/> ticks by running its tick clock up to ±<see cref="MaxDilation"/> faster or slower,
    /// and re-syncs in one jump when it is more than <see cref="ResyncTicks"/> off.</para>
    /// <para><b>Result.</b> predictedTick ≈ serverTickNow + (RTT/2 + send/receive quantisation)/dt + TargetLead.</para>
    /// <para><b>Adaptive target.</b> Starts at 2; when a command arrived late by k ticks, +max(1, k) (max 6) and the clock
    /// jumps forward to the new target at once; −1 (min 1) after 10 s whose minimum lead never dipped below the target.</para>
    /// <para>Pure C#: the caller passes real elapsed seconds; no UnityEngine.Time inside.</para>
    /// </summary>
    public sealed class TickSync
    {
        public const int MinTarget = 1;
        public const int MaxTarget = 6;
        public const int InitialTarget = 2;
        public const int ResyncTicks = 8;
        public const float MaxDilation = 0.06f;
        public const float DilationPerTick = 0.02f;
        public const double LowerAfterSeconds = 10.0;
        public const int MaxTicksPerAdvance = 5;
        /// <summary>After a re-sync or a target raise, reports already in flight are stale; ignore them this long.</summary>
        public const double HoldSeconds = 0.5;
        /// <summary>Far-late reports without lost time must persist this long before they count as a clock that is really behind (a stall's backlog drains at ≤ 32 messages per server tick).</summary>
        public const double StallPersistSeconds = 2.0;

        private readonly double _dt;
        private double _acc;
        private double _windowStart;
        private int _windowMinLead = int.MaxValue;
        private double _holdUntil;

        public TickSync(int tickRate)
        {
            _dt = 1.0 / tickRate;
        }

        public bool Initialized { get; private set; }

        /// <summary>The tick the next sampled command will be stamped with.</summary>
        public uint PredictedTick { get; private set; }

        public int TargetLead { get; private set; } = InitialTarget;
        public int LastReportedLead { get; private set; }
        public float Rate { get; private set; } = 1f;
        public int Resyncs { get; private set; }
        public int LateReports { get; private set; }
        /// <summary>Far-late reports ignored because the clock had not lost time (transport stall).</summary>
        public int StallReports { get; private set; }
        private long _lostTicks; // ticks the clock dropped in capped Advance calls since the last normal report
        private double _stallSince = -1;

        /// <summary>Latest server tick seen and the local time it arrived (for a server-time estimate).</summary>
        public uint LastServerTick { get; private set; }
        public double LastServerTickAt { get; private set; }

        public double Dt => _dt;

        /// <summary>Elapsed fraction (0..1) of the current tick: how far real time is past the last sampled tick.</summary>
        public double Fraction => Initialized ? Math.Max(0.0, Math.Min(1.0, _acc / _dt)) : 0.0;

        /// <summary>Estimated current server tick (fractional), from the newest snapshot plus elapsed time.</summary>
        public double ServerTickEstimate(double now) => LastServerTick + (now - LastServerTickAt) / _dt;

        public void Reset()
        {
            Initialized = false;
            _lostTicks = 0;
            _stallSince = -1;
            _acc = 0;
            TargetLead = InitialTarget;
            Rate = 1f;
            _windowMinLead = int.MaxValue;
            _holdUntil = 0;
        }

        /// <param name="now">Local monotonic seconds.</param>
        /// <param name="rttMs">Current RTT estimate (NaN if unknown).</param>
        public void OnSnapshot(uint serverTick, sbyte inputLead, float rttMs, double now)
        {
            if (!Initialized || serverTick > LastServerTick)
            {
                LastServerTick = serverTick;
                LastServerTickAt = now;
            }

            if (!Initialized)
            {
                var rtt = float.IsNaN(rttMs) || rttMs <= 0f ? 100f : rttMs;
                PredictedTick = serverTick + (uint)Math.Ceiling(rtt / 1000.0 / _dt) + (uint)TargetLead;
                Initialized = true;
                _windowStart = now;
                return;
            }

            if (inputLead == SnapHeader.LeadUnknown || now < _holdUntil)
                return;

            int lead = inputLead;
            LastReportedLead = lead;
            if (lead < _windowMinLead)
                _windowMinLead = lead;

            if (lead - TargetLead >= -ResyncTicks)
            {
                _stallSince = -1;
                _lostTicks = 0; // whatever was lost has been absorbed
            }
            else if (_lostTicks == 0 && (_stallSince < 0 || now - _stallSince < StallPersistSeconds))
            {
                if (_stallSince < 0)
                    _stallSince = now;
                // Far late, but our clock never lost time: the commands were held up in transit (a TCP stall on the
                // network or on this machine) and arrive in a burst. Jumping now would put every following command
                // ~stall-length too early (M2b VPS gate: 3 clients kicked after a ~3 s stall). Hold instead.
                StallReports++;
                _holdUntil = now + HoldSeconds;
                _windowStart = now;
                _windowMinLead = int.MaxValue;
                return;
            }

            if (lead - TargetLead < -ResyncTicks)
            {
                // Clock far behind because it lost time (or the reports stayed far late past the hold, so it really is
                // behind) (hidden tab, long hitch): a jump, not jitter — don't raise the target for it.
                // Jump by at most what the clock actually lost (+ target); the rest of the lateness is transit delay.
                long jump = TargetLead - lead;
                if (_lostTicks > 0 && (jump > _lostTicks + TargetLead || lead <= sbyte.MinValue + 1))
                    jump = _lostTicks + TargetLead; // a saturated report (−127) only says "at least": trust the ticks we know we lost
                _lostTicks = 0;
                _stallSince = -1;
                PredictedTick += (uint)jump;
                Resyncs++;
                Rate = 1f;
                _holdUntil = now + HoldSeconds;
                _windowStart = now;
                _windowMinLead = int.MaxValue;
                return;
            }

            if (lead < 0)
            {
                // Late by |lead| ticks: raise the target by that much (not just 1) and catch the clock up at once.
                // Dilation alone would take ~1.7 s per 3 ticks, and every command in between would be late too
                // (M2b gate, 150 ms + jitter + loss: 10–13 start-up corrections per client with the +1 rule).
                LateReports++;
                TargetLead = Math.Min(MaxTarget, TargetLead + Math.Max(1, -lead));
                PredictedTick += (uint)(TargetLead - lead);
                Rate = 1f;
                _holdUntil = now + HoldSeconds;
                _windowStart = now;
                _windowMinLead = int.MaxValue;
                return;
            }
            else if (now - _windowStart >= LowerAfterSeconds)
            {
                // The dilation holds the lead at the target, so "never dipped below the target for 10 s" means a
                // whole tick of slack was never used: one tick less still keeps every command on time.
                if (_windowMinLead >= TargetLead && TargetLead > MinTarget)
                    TargetLead--;
                _windowStart = now;
                _windowMinLead = int.MaxValue;
            }

            var error = lead - TargetLead;
            if (error < -ResyncTicks)
            {
                // Far behind: jump forward. (Ticks never go backwards; the skipped ticks are repeated server-side.)
                PredictedTick += (uint)(-error);
                Resyncs++;
                Rate = 1f;
                _holdUntil = now + HoldSeconds;
            }
            else if (error > ResyncTicks)
            {
                // Far ahead: pause sampling for that many ticks.
                _acc -= error * _dt;
                Resyncs++;
                Rate = 1f;
                _holdUntil = now + HoldSeconds;
            }
            else
            {
                // Too early (error > 0) → run slower; too late → faster.
                var r = 1f - DilationPerTick * error;
                Rate = Math.Max(1f - MaxDilation, Math.Min(1f + MaxDilation, r));
            }
        }

        /// <summary>Advances the clock by real time; returns how many ticks to sample now (0..<see cref="MaxTicksPerAdvance"/>).</summary>
        public int Advance(double realDeltaSeconds)
        {
            if (!Initialized || realDeltaSeconds <= 0)
                return 0;
            _acc += realDeltaSeconds * Rate;
            var n = (int)Math.Floor(_acc / _dt);
            if (n <= 0)
                return 0;
            if (n > MaxTicksPerAdvance)
            {
                // A long hitch (or a hidden tab): don't burst; the lead feedback re-syncs (and is allowed to jump).
                _lostTicks += n - MaxTicksPerAdvance;
                _acc = 0;
                n = MaxTicksPerAdvance;
            }
            else
            {
                _acc -= n * _dt;
            }

            return n;
        }

        /// <summary>Returns the tick for the command being sampled and advances.</summary>
        public uint NextTick() => PredictedTick++;
    }
}
