using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>
    /// The output of one race round (M4's MatchDirector and M6's chain events consume it): every participating slot in
    /// final order, with its qualified flag, finish tick (0 = did not finish) and checkpoints reached. Deterministic: the
    /// same inputs give the same result.
    /// </summary>
    public sealed class RaceResult
    {
        public int QualifyCount;
        public int[] Order = Array.Empty<int>();       // slots, place 1 first
        public bool[] Qualified = Array.Empty<bool>(); // by place
        public uint[] FinishTicks = Array.Empty<uint>();
        public int[] Checkpoints = Array.Empty<int>();
        public uint RushTick, EndTick;
        public bool TimerExpired;

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"qualify={QualifyCount} timerExpired={TimerExpired} duration={(EndTick - RushTick) / 30.0:F1}s order=");
            for (var i = 0; i < Order.Length; i++)
                sb.Append(i > 0 ? "," : "").Append(Order[i]).Append(Qualified[i] ? "Q" : "").Append(FinishTicks[i] > 0 ? $"@{(FinishTicks[i] - RushTick) / 30.0:F1}" : $"(cp{Checkpoints[i]})");
            return sb.ToString();
        }
    }

    /// <summary>
    /// <c>RoundRules.Race</c> (GDD §5.1, M3 prompt §5.1): engine-free, driven by <see cref="SimCore"/> through <see cref="ISimRules"/>.
    /// <list type="number">
    /// <item><b>Phases:</b> Waiting (free movement behind the closed start gate) → Intro (everyone on their spawn, inputs
    /// ignored, ≤ 4 s) → Countdown (3-2-1, inputs ignored) → Rush (the start gate opens at the RUSH tick = the round's
    /// hazard time 0) → Ended.</item>
    /// <item><b>Ordered checkpoints</b> (doc 11): a checkpoint counts only if it is the <i>next</i> one and the player passed
    /// the reachability check: the time since the previous checkpoint is at least the course distance between them divided
    /// by <see cref="MaxCourseSpeedMms"/> (the top speed with dive, launch and carry together). A skipped checkpoint never counts.</item>
    /// <item><b>Fall:</b> below the kill-Y → respawn at the last checkpoint after 30 ticks (1.0 s), on a free spot (no stacking), facing the course.</item>
    /// <item><b>Finish:</b> the last checkpoint. The first <see cref="QualifyCount"/> finishers qualify; when that many have
    /// finished the round ends at once. Finished players leave the simulation and collisions.</item>
    /// <item><b>Timer:</b> on expiry the rest are ranked by checkpoint index ↓, distance along the course to the next
    /// checkpoint ↑, tick the last checkpoint was reached ↑, slot ↑. "Distance" is measured along the course polyline.</item>
    /// </list>
    /// </summary>
    public sealed class RaceRules : ISimRules
    {
        /// <summary>15 m/s: run 7 + belt/carry 4 + spring launch horizontal ≤ 8 never all at once, dive 9; generous on purpose.</summary>
        public const int MaxCourseSpeedMms = 15000;

        public struct Racer
        {
            public bool In;            // took part (active at the intro)
            public int Checkpoint;     // checkpoints reached (0 = none; Checkpoints.Length = finished)
            public uint CheckpointTick;
            public bool Finished;
            public uint FinishTick;
            public int Place;          // finish place (1-based), 0 = not finished
            public bool Qualified;
            public bool Eliminated;
            public int RejectedCheckpoints; // reachability failures (anti-cheat statistic)
        }

        public readonly CheckpointDef[] Checkpoints;
        public readonly int[] CourseLineMm;
        public readonly Racer[] Racers = new Racer[SimCore.MaxSlots];
        private readonly int[] _respawnCursor;
        private readonly float[] _lineS;

        public RoundPhase Phase { get; private set; } = RoundPhase.Waiting;
        public uint PhaseStartTick { get; private set; }
        public uint RushTick { get; private set; }
        public uint RushEndTick { get; private set; }
        public int IntroTicks = 90, CountdownTicks = 90;
        public uint TimerTicks;
        public int QualifyCount { get; private set; }
        public int QualifiedCount { get; private set; }
        public int FinishedCount { get; private set; }
        public RaceResult Result { get; private set; }
        public readonly List<int> FinishOrder = new List<int>();
        public bool TimerExpired { get; private set; }

        /// <summary>Domain events in order (the server drains and sends them as <c>evt</c>, reliable, with a sequence number).</summary>
        public readonly List<RaceEvent> Pending = new List<RaceEvent>();
        public uint EventSeq { get; private set; }

        /// <summary>Raised when a phase changes (the server sends a fresh <c>rstate</c>).</summary>
        public event Action<RoundPhase> PhaseChanged;

        public RaceRules(CheckpointDef[] checkpoints, int[] courseLineMm, uint timerTicks)
        {
            Checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
            if (Checkpoints.Length < 1 || Checkpoints.Length > 15)
                throw new ArgumentException("1–15 checkpoints (the race byte holds 4 bits)");
            CourseLineMm = courseLineMm ?? Array.Empty<int>();
            TimerTicks = timerTicks;
            _respawnCursor = new int[Checkpoints.Length + 1];
            var n = CourseLineMm.Length / 3;
            _lineS = new float[Math.Max(1, n)];
            for (var i = 1; i < n; i++)
            {
                var dx = (CourseLineMm[i * 3] - CourseLineMm[i * 3 - 3]) * 0.001f;
                var dy = (CourseLineMm[i * 3 + 1] - CourseLineMm[i * 3 - 2]) * 0.001f;
                var dz = (CourseLineMm[i * 3 + 2] - CourseLineMm[i * 3 - 1]) * 0.001f;
                _lineS[i] = _lineS[i - 1] + (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }

        public int Finish => Checkpoints.Length;
        public bool IsOver => Phase == RoundPhase.Ended;

        /// <summary>Server: start the round now (intro at <paramref name="tick"/> + 1). Returns the RUSH tick (= hazard time 0).</summary>
        public uint Begin(SimCore sim, uint tick)
        {
            var players = 0;
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                Racers[i] = default;
                if (sim.Slots[i].Active)
                {
                    Racers[i].In = true;
                    players++;
                }
            }

            QualifyCount = Math.Min(players, RoundParams.RaceQualifyCount(players));
            QualifiedCount = 0;
            FinishedCount = 0;
            FinishOrder.Clear();
            TimerExpired = false;
            Result = null;
            Array.Clear(_respawnCursor, 0, _respawnCursor.Length);
            RushTick = tick + 1 + (uint)IntroTicks + (uint)CountdownTicks;
            RushEndTick = TimerTicks > 0 ? RushTick + TimerTicks : 0;
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (Racers[i].In)
                    sim.Slots[i].State = sim.SpawnState(i);
            SetPhase(IntroTicks > 0 ? RoundPhase.Intro : RoundPhase.Countdown, tick + 1);
            return RushTick;
        }

        private void SetPhase(RoundPhase p, uint tick)
        {
            Phase = p;
            PhaseStartTick = tick;
            Emit(RaceEventType.Phase, tick, 255, (int)p);
            PhaseChanged?.Invoke(p);
        }

        private void Emit(RaceEventType type, uint tick, int slot, int arg)
        {
            Pending.Add(new RaceEvent { Seq = ++EventSeq, Type = type, Tick = tick, Slot = (byte)slot, Arg = arg });
        }

        // ------------------------------------------------------------------ ISimRules

        public void BeginTick(SimCore sim, uint tick)
        {
            switch (Phase)
            {
                case RoundPhase.Intro when tick >= PhaseStartTick + (uint)IntroTicks:
                    SetPhase(RoundPhase.Countdown, tick);
                    break;
                case RoundPhase.Countdown when tick >= RushTick:
                    SetPhase(RoundPhase.Rush, tick);
                    break;
            }

            // A player who joined after the intro is a spectator of this round.
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (!sim.Slots[i].Active && Racers[i].In && Phase != RoundPhase.Ended)
                    Racers[i].In = false;
        }

        public bool AcceptsInput(SimCore sim, int slot)
        {
            if (Phase == RoundPhase.Intro || Phase == RoundPhase.Countdown || Phase == RoundPhase.Ended)
                return false;
            return !Racers[slot].Finished;
        }

        public bool Simulates(SimCore sim, int slot) => !(Racers[slot].In && Racers[slot].Finished);

        public bool Collides(SimCore sim, int slot) => !(Racers[slot].In && Racers[slot].Finished);

        public bool Respawn(SimCore sim, int slot)
        {
            ref var r = ref Racers[slot];
            var cp = r.Checkpoint;
            if (cp <= 0 || cp > Checkpoints.Length || !r.In)
                return false; // the start: the normal spawn
            var spots = Checkpoints[cp - 1].RespawnMm;
            if (spots == null || spots.Length < 4)
                return false;
            var n = spots.Length / 4;
            // A free spot: the first (round-robin) with nobody within 0.8 m.
            var start = _respawnCursor[cp];
            for (var k = 0; k < n; k++)
            {
                var j = (start + k) % n;
                if (!Occupied(sim, slot, spots[j * 4], spots[j * 4 + 1], spots[j * 4 + 2]))
                {
                    Place(sim, slot, spots, j);
                    _respawnCursor[cp] = (j + 1) % n;
                    Emit(RaceEventType.Respawned, sim.Tick, slot, cp);
                    return true;
                }
            }

            Place(sim, slot, spots, start % n);
            _respawnCursor[cp] = (start + 1) % n;
            Emit(RaceEventType.Respawned, sim.Tick, slot, cp);
            return true;
        }

        private static void Place(SimCore sim, int slot, int[] spots, int j) =>
            sim.Place(slot, new Vec3(spots[j * 4] * 0.001f, spots[j * 4 + 1] * 0.001f, spots[j * 4 + 2] * 0.001f), (byte)spots[j * 4 + 3]);

        private static bool Occupied(SimCore sim, int self, int x, int y, int z)
        {
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (i == self || !sim.Slots[i].Active)
                    continue;
                ref readonly var s = ref sim.Slots[i].State;
                long dx = s.PosX - x, dz = s.PosZ - z, dy = s.PosY - y;
                if (dx * dx + dz * dz < 800L * 800L && dy > -1000 && dy < 1000)
                    return true;
            }

            return false;
        }

        public void EndTick(SimCore sim, uint tick)
        {
            if (Phase != RoundPhase.Rush)
                return;

            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                ref var r = ref Racers[i];
                if (!r.In || r.Finished || !sim.Slots[i].Active)
                    continue;
                ref readonly var s = ref sim.Slots[i].State;
                if (s.Has(MotorState.Respawning))
                    continue;
                TryCheckpoint(i, ref r, in s, tick);
            }

            if (endPending)
            {
                // N have finished: the round ends at once (ties within one tick resolve by slot order).
                endPending = false;
                End(sim, tick);
                return;
            }

            var active = 0;
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (Racers[i].In && sim.Slots[i].Active && !Racers[i].Finished)
                    active++;
            if (RushEndTick != 0 && tick >= RushEndTick)
            {
                TimerExpired = true;
                End(sim, tick);
            }
            else if (active == 0 && FinishedCount > 0)
            {
                End(sim, tick); // everyone left is finished (fewer players than the qualify count)
            }
        }

        /// <summary>The checkpoint rule (exposed for the anti-cheat test): only the next one, and only if reachable in the time taken.</summary>
        public void TryCheckpoint(int slot, ref Racer r, in MotorState s, uint tick)
        {
            var next = r.Checkpoint; // index into Checkpoints of the next one
            if (next >= Checkpoints.Length)
                return;
            ref readonly var c = ref Checkpoints[next];
            if (!c.Contains(s.PosX, s.PosY, s.PosZ))
                return;
            var prevMm = next == 0 ? 0 : Checkpoints[next - 1].CourseMm;
            var since = next == 0 ? tick - RushTick : tick - r.CheckpointTick;
            var minTicks = (long)(c.CourseMm - prevMm) * 30 / MaxCourseSpeedMms;
            if (since < minTicks)
            {
                r.RejectedCheckpoints++;
                return;
            }

            r.Checkpoint = next + 1;
            r.CheckpointTick = tick;
            if (r.Checkpoint < Checkpoints.Length)
            {
                Emit(RaceEventType.Checkpoint, tick, slot, r.Checkpoint);
                return;
            }

            // Finish.
            r.Finished = true;
            r.FinishTick = tick;
            FinishedCount++;
            r.Place = FinishedCount;
            FinishOrder.Add(slot);
            Emit(RaceEventType.Finished, tick, slot, r.Place);
            if (QualifiedCount < QualifyCount)
            {
                r.Qualified = true;
                QualifiedCount++;
                Emit(RaceEventType.Qualified, tick, slot, QualifiedCount);
            }

            if (QualifiedCount >= QualifyCount)
                endPending = true;
        }

        private bool endPending;

        private void End(SimCore sim, uint tick)
        {
            // Final order: finishers by finish tick (place), then the ranking key for the rest.
            var rest = new List<int>();
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (Racers[i].In && !Racers[i].Finished)
                    rest.Add(i);
            var dist = new float[SimCore.MaxSlots];
            foreach (var i in rest)
                dist[i] = RemainingToNext(Racers[i].Checkpoint, sim.Slots[i].Active ? sim.Slots[i].State : default);
            rest.Sort((a, b) =>
            {
                var ra = Racers[a];
                var rb = Racers[b];
                if (ra.Checkpoint != rb.Checkpoint)
                    return rb.Checkpoint.CompareTo(ra.Checkpoint);
                var da = Quant(dist[a]);
                var db = Quant(dist[b]);
                if (da != db)
                    return da.CompareTo(db);
                if (ra.CheckpointTick != rb.CheckpointTick)
                    return ra.CheckpointTick.CompareTo(rb.CheckpointTick);
                return a.CompareTo(b);
            });

            var order = new List<int>(FinishOrder);
            order.AddRange(rest);
            var res = new RaceResult
            {
                QualifyCount = QualifyCount,
                Order = order.ToArray(),
                Qualified = new bool[order.Count],
                FinishTicks = new uint[order.Count],
                Checkpoints = new int[order.Count],
                RushTick = RushTick,
                EndTick = tick,
                TimerExpired = TimerExpired,
            };
            for (var p = 0; p < order.Count; p++)
            {
                ref var r = ref Racers[order[p]];
                // Timer expiry with fewer than N finishers: the remaining places qualify by the ranking key.
                if (!r.Qualified && QualifiedCount < QualifyCount)
                {
                    r.Qualified = true;
                    QualifiedCount++;
                    Emit(RaceEventType.Qualified, tick, order[p], QualifiedCount);
                }

                res.Qualified[p] = r.Qualified;
                res.FinishTicks[p] = r.FinishTick;
                res.Checkpoints[p] = r.Checkpoint;
                if (!r.Qualified)
                {
                    r.Eliminated = true;
                    Emit(RaceEventType.Eliminated, tick, order[p], p + 1);
                }
            }

            Result = res;
            Emit(RaceEventType.RoundEnd, tick, 255, QualifiedCount);
            SetPhase(RoundPhase.Ended, tick);
        }

        private static long Quant(float m) => (long)Math.Round(m * 1000.0); // compare at mm resolution

        /// <summary>Distance along the course polyline from the player to checkpoint <paramref name="cpIndex"/> (the next one), metres.</summary>
        public float RemainingToNext(int cpIndex, in MotorState s)
        {
            if (cpIndex >= Checkpoints.Length)
                return 0f;
            var target = Checkpoints[cpIndex].CourseMm * 0.001f;
            var at = CourseDistance(s.PosX * 0.001f, s.PosY * 0.001f, s.PosZ * 0.001f);
            var d = target - at;
            return d < 0f ? 0f : d;
        }

        /// <summary>Arc length along the course polyline of the closest point to (x, y, z).</summary>
        public float CourseDistance(float x, float y, float z)
        {
            var n = CourseLineMm.Length / 3;
            if (n < 2)
                return 0f;
            var best = float.MaxValue;
            var bestS = 0f;
            for (var i = 1; i < n; i++)
            {
                float ax = CourseLineMm[i * 3 - 3] * 0.001f, ay = CourseLineMm[i * 3 - 2] * 0.001f, az = CourseLineMm[i * 3 - 1] * 0.001f;
                float bx = CourseLineMm[i * 3] * 0.001f, by = CourseLineMm[i * 3 + 1] * 0.001f, bz = CourseLineMm[i * 3 + 2] * 0.001f;
                float ux = bx - ax, uy = by - ay, uz = bz - az;
                var len2 = ux * ux + uy * uy + uz * uz;
                var t = len2 > 0f ? ((x - ax) * ux + (y - ay) * uy + (z - az) * uz) / len2 : 0f;
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                float px = ax + ux * t - x, py = ay + uy * t - y, pz = az + uz * t - z;
                var d2 = px * px + py * py + pz * pz;
                if (d2 < best)
                {
                    best = d2;
                    bestS = _lineS[i - 1] + (float)Math.Sqrt(len2) * t;
                }
            }

            return bestS;
        }

        public byte RaceByte(int slot)
        {
            ref readonly var r = ref Racers[slot];
            if (!r.In)
                return 0;
            var b = (byte)(Math.Min(15, r.Checkpoint) & PlayerState.RaceCheckpointMask);
            if (r.Finished)
                b |= PlayerState.RaceFinished;
            if (r.Qualified)
                b |= PlayerState.RaceQualified;
            if (r.Eliminated)
                b |= PlayerState.RaceEliminated;
            return b;
        }

        /// <summary>The full round state for <c>rstate</c>.</summary>
        public RaceStateMessage StateMessage()
        {
            var m = new RaceStateMessage
            {
                LastEventSeq = EventSeq, Phase = Phase, PhaseStartTick = PhaseStartTick, RushEndTick = RushEndTick,
                QualifyCount = (byte)QualifyCount, QualifiedCount = (byte)QualifiedCount, FinisherCount = (byte)FinishOrder.Count,
                FinisherSlots = new byte[FinishOrder.Count], FinisherTicks = new uint[FinishOrder.Count],
            };
            for (var i = 0; i < FinishOrder.Count; i++)
            {
                m.FinisherSlots[i] = (byte)FinishOrder[i];
                m.FinisherTicks[i] = Racers[FinishOrder[i]].FinishTick;
            }

            return m;
        }
    }
}
