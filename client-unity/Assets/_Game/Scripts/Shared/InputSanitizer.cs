using System;

namespace FallRush.Shared
{
    /// <summary>Per-player input counters (cumulative; the server logs deltas every 10 s).</summary>
    public struct InputCounters
    {
        public long Messages;
        public long Accepted;
        public long Redundant;      // seq ≤ last accepted: the normal redundant copies (and replays / stale)
        public long Late;           // tick already simulated (press edges carried into the next tick)
        public long FarFuture;      // tick more than ViolationLeadTicks ahead → violation
        public long Early;          // tick beyond the lead window but within ViolationLeadTicks → dropped, not a violation (clock re-converging)
        public long Flood;          // over the sustained message rate (token bucket empty; dropped undecoded) → violation
        public long Backlog;        // > MaxMessagesPerTick in one tick but within the rate budget (a TCP stall releasing a burst): dropped undecoded, not a violation
        public long Burst;          // > MaxNewPerTick new commands in one tick (TCP catch-up after a hitch; redelivered by redundancy) — not a violation
        public long Malformed;      // decode failure → violation
        public long Clamped;        // axis/length/button sanitised (not a violation)
        public long Repeated;       // tick simulated with the previous input (missing command)
        public long Neutral;        // tick simulated with neutral input (missing for > RepeatTicks)

        public long Violations => FarFuture + Flood + Malformed;
    }

    /// <summary>
    /// Server-side input validation and the per-player tick-keyed input buffer (doc 04 §4.2 step 1, doc 11 §2).
    /// Fixed-size storage: flooding cannot grow memory, and extra messages are rejected before any per-command work.
    /// <list type="bullet">
    /// <item>Commands are keyed by <c>tick</c>; at server step <c>S</c> the command with <c>tick == S</c> is applied.</item>
    /// <item>Freshness: <c>seq</c> must be newer than the last accepted one (older/duplicates are the normal redundancy; dropped).</item>
    /// <item>≤ <see cref="MaxNewPerTick"/> new commands per player per tick (extra on-time ones are deferred to the redundant copy in the next message:
    /// over WebSocket/TCP a stall releases a burst of legitimate messages). More than <see cref="MaxMessagesPerTick"/>
    /// messages in one tick are dropped undecoded. Flood <b>violations</b> come from a token bucket
    /// (<see cref="RateTokensPerTick"/> = 45 msg/s, capacity <see cref="RateBurst"/> ≈ 10 s of backlog), so a stall's
    /// burst is tolerated and a sustained flood is not (M2b: a 3 s stall kicked honest clients under the old per-tick rule).</item>
    /// <item>Window: <c>tick</c> in [next, next + <see cref="MaxLeadTicks"/>]; late commands are dropped but their
    /// jump/dive press edges are applied on the next simulated tick; far-future commands are dropped and counted.</item>
    /// <item>Missing command: repeat the last one (edges cleared) for up to <see cref="RepeatTicks"/> ticks, then neutral.</item>
    /// </list>
    /// </summary>
    public sealed class InputSanitizer
    {
        public const int MaxNewPerTick = 3;
        public const int MaxMessagesPerTick = 32;
        public const float RateTokensPerTick = 1.5f; // 45 messages/s; an honest client sends 30
        public const float RateBurst = 450f;
        public const int MaxLeadTicks = 30;
        /// <summary>
        /// Commands up to this far ahead are dropped but not counted as violations: an honest client whose clock is
        /// re-converging (e.g. after a network stall) can briefly run ahead (M2b VPS gate: 3 clients were kicked after a
        /// ~3 s stall). Dropping a future command gives no advantage; beyond 10 s it is treated as abuse.
        /// </summary>
        public const int ViolationLeadTicks = 300;
        public const int RepeatTicks = 3;
        private const int Ring = 64; // > MaxLeadTicks + slack, power of two

        private readonly InputCmd[] _byTick = new InputCmd[Ring];
        private readonly bool[] _has = new bool[Ring];

        private uint _lastSeq;
        private bool _anySeq;
        private int _newThisTick;
        private int _msgsThisTick;
        private float _tokens = RateBurst;
        private byte _carryEdges;
        private InputCmd _lastApplied;
        private int _missing;
        private int _minLead = int.MaxValue;

        public InputCounters Counters;

        /// <summary>Seq of the newest command applied by <see cref="Consume"/> (0 before the first).</summary>
        public uint AckSeq { get; private set; }

        /// <summary>Call once per server tick before offering that tick's messages (resets the per-tick rate limits).</summary>
        public void BeginTick()
        {
            _newThisTick = 0;
            _msgsThisTick = 0;
            _tokens = Math.Min(RateBurst, _tokens + RateTokensPerTick);
        }

        /// <summary>Message-level rate limit. False → drop the whole message without decoding it.</summary>
        public bool AdmitMessage()
        {
            Counters.Messages++;
            if (_tokens < 1f)
            {
                Counters.Flood++; // sustained over-rate
                return false;
            }

            _tokens -= 1f;
            if (++_msgsThisTick > MaxMessagesPerTick)
            {
                Counters.Backlog++; // a burst within the rate budget: decode at most 32 per tick, no violation
                return false;
            }

            return true;
        }

        public void CountMalformed() => Counters.Malformed++;

        public enum Result
        {
            Accepted,
            Redundant,
            Late,
            FarFuture,
            Early,
            Burst,
        }

        /// <param name="nextTick">The next tick the server will simulate.</param>
        public Result Offer(InputCmd cmd, uint nextTick)
        {
            if (_anySeq && cmd.Seq <= _lastSeq)
            {
                Counters.Redundant++;
                return Result.Redundant;
            }

            if (Sanitize(ref cmd))
                Counters.Clamped++;

            var lead = (long)cmd.Tick - nextTick;
            if (lead > ViolationLeadTicks)
            {
                Counters.FarFuture++;
                return Result.FarFuture;
            }

            if (lead > MaxLeadTicks)
            {
                Counters.Early++;
                TrackLead(lead); // reported back, so the client's clock slows down / pauses and re-converges
                return Result.Early;
            }

            if (lead < 0)
            {
                // Already simulated: drop, but keep its press edges for the next tick (a late jump is not lost).
                _lastSeq = cmd.Seq;
                _anySeq = true;
                TrackLead(lead);
                _carryEdges |= (byte)(cmd.Buttons & InputCmd.EdgeButtons);
                Counters.Late++;
                return Result.Late;
            }

            if (_newThisTick >= MaxNewPerTick)
            {
                // Seq is not advanced, so the redundant copy in the next message can still deliver it.
                Counters.Burst++;
                return Result.Burst;
            }

            _newThisTick++;
            _lastSeq = cmd.Seq;
            _anySeq = true;
            TrackLead(lead);

            var i = (int)(cmd.Tick & (Ring - 1));
            _byTick[i] = cmd;
            _has[i] = true;
            Counters.Accepted++;
            return Result.Accepted;
        }

        private void TrackLead(long lead)
        {
            if (lead < _minLead)
                _minLead = lead < int.MinValue ? int.MinValue : (int)lead;
        }

        /// <summary>The input to simulate at <paramref name="tick"/>. <paramref name="own"/> is false when repeated/neutral.</summary>
        public InputCmd Consume(uint tick, out bool own)
        {
            var i = (int)(tick & (Ring - 1));
            InputCmd cmd;
            if (_has[i] && _byTick[i].Tick == tick)
            {
                cmd = _byTick[i];
                _has[i] = false;
                own = true;
                _missing = 0;
                _lastApplied = cmd;
                AckSeq = cmd.Seq;
            }
            else
            {
                if (_has[i] && _byTick[i].Tick < tick)
                    _has[i] = false; // stale slot
                own = false;
                _missing++;
                if (_missing <= RepeatTicks)
                {
                    cmd = _lastApplied;
                    cmd.Buttons = (byte)(cmd.Buttons & ~InputCmd.EdgeButtons);
                    Counters.Repeated++;
                }
                else
                {
                    cmd = default;
                    Counters.Neutral++;
                }

                cmd.Tick = tick;
            }

            if (_carryEdges != 0)
            {
                cmd.Buttons |= _carryEdges;
                _carryEdges = 0;
            }

            return cmd;
        }

        /// <summary>Min lead since the last call (ticks; <see cref="SnapHeader.LeadUnknown"/> if no new input), clamped to i8.</summary>
        public sbyte TakeMinLead()
        {
            if (_minLead == int.MaxValue)
                return SnapHeader.LeadUnknown;
            var v = _minLead;
            _minLead = int.MaxValue;
            if (v > 127)
                v = 127;
            if (v < -127)
                v = -127;
            return (sbyte)v;
        }

        /// <summary>Clamps axes to [−127, 127], the move vector to length ≤ 127, and masks unknown button bits.</summary>
        public static bool Sanitize(ref InputCmd cmd)
        {
            var changed = false;
            int x = cmd.MoveX, y = cmd.MoveY;
            if (x < -127)
            {
                x = -127;
                changed = true;
            }

            if (y < -127)
            {
                y = -127;
                changed = true;
            }

            var l2 = x * x + y * y;
            if (l2 > 127 * 127)
            {
                // Integer scale-down (deterministic): shrink until inside the circle.
                var len = Math.Sqrt(l2);
                x = (int)(x * 127 / len);
                y = (int)(y * 127 / len);
                while (x * x + y * y > 127 * 127)
                {
                    x -= Math.Sign(x);
                    y -= Math.Sign(y);
                }

                changed = true;
            }

            var b = (byte)(cmd.Buttons & InputCmd.KnownButtons);
            if (b != cmd.Buttons)
                changed = true;

            cmd.MoveX = (sbyte)x;
            cmd.MoveY = (sbyte)y;
            cmd.Buttons = b;
            return changed;
        }
    }
}
