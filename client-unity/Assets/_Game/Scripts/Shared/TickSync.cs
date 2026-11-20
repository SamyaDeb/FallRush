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
    /// <para><b>Adaptive target.</b> Starts at 2; +1 (max 6) whenever a command arrived late; −1 (min 1) after 10 s whose minimum lead stayed ≥ target + 1.</para>
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

        /// <summary>Latest server tick seen and the local time it arrived (for a server-time estimate).</summary>
        public uint LastServerTick { get; private set; }
        public double LastServerTickAt { get; private set; }

        public double Dt => _dt;

        /// <summary>Estimated current server tick (fractional), from the newest snapshot plus elapsed time.</summary>
        public double ServerTickEstimate(double now) => LastServerTick + (now - LastServerTickAt) / _dt;

        public void Reset()
        {
            Initialized = false;
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

            if (lead < 0)
            {
                LateReports++;
                if (TargetLead < MaxTarget)
                    TargetLead++;
                _holdUntil = now + HoldSeconds;
                _windowStart = now;
                _windowMinLead = int.MaxValue;
            }
            else if (now - _windowStart >= LowerAfterSeconds)
            {
                if (_windowMinLead >= TargetLead + 1 && TargetLead > MinTarget)
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
                // A long hitch (or a hidden tab): don't burst; the lead feedback re-syncs.
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
