using System;
using System.Collections.Generic;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    public class TickSyncTests
    {
        private const int Rate = 30;
        private const double Dt = 1.0 / Rate;

        [Test]
        public void Init_PredictsRttAheadPlusTarget()
        {
            var t = new TickSync(Rate);
            t.OnSnapshot(1000, SnapHeader.LeadUnknown, 66f, 0);
            Assert.That(t.Initialized, Is.True);
            Assert.That(t.PredictedTick, Is.EqualTo(1000 + 2 + TickSync.InitialTarget)); // ceil(66/33.3) = 2
        }

        [Test]
        public void HiddenTab_SaturatedLateReport_JumpsByTheLostTicksAtOnce()
        {
            var t = new TickSync(Rate);
            t.OnSnapshot(1000, SnapHeader.LeadUnknown, 50f, 0);
            t.Advance(60.0); // a 60 s hidden tab: ~1,795 ticks dropped
            var before = t.PredictedTick;
            t.OnSnapshot(2800, -127, 50f, 60.1); // i8-saturated report
            Assert.That(t.PredictedTick - before, Is.GreaterThan(1700u), "one jump covers the whole gap");
            Assert.That(t.Resyncs, Is.EqualTo(1));
        }

        [Test]
        public void FarLate_WithoutLostTime_IsATransportStall_NoJump()
        {
            // M2b VPS gate: after a ~3 s TCP stall the server reported the queued commands 90 ticks late. The clock
            // had not lost time, so jumping forward made every following command 90 ticks early (kick).
            var t = new TickSync(Rate);
            t.OnSnapshot(1000, SnapHeader.LeadUnknown, 50f, 0);
            for (var i = 0; i < 30; i++)
                t.Advance(1.0 / Rate);
            var before = t.PredictedTick;
            t.OnSnapshot(1100, -90, 50f, 1.0);
            Assert.That(t.PredictedTick, Is.EqualTo(before), "no jump");
            Assert.That(t.Resyncs, Is.EqualTo(0));
            Assert.That(t.StallReports, Is.EqualTo(1));
            Assert.That(t.TargetLead, Is.EqualTo(TickSync.InitialTarget), "no target ratchet either");
        }

        [Test]
        public void Dilation_SlowsWhenEarly_SpeedsUpWhenLate_ResyncsWhenFar()
        {
            var t = new TickSync(Rate);
            t.OnSnapshot(1000, SnapHeader.LeadUnknown, 50f, 0);
            t.OnSnapshot(1002, 6, 50f, 0.1);
            Assert.That(t.Rate, Is.LessThan(1f));
            t.OnSnapshot(1004, 1, 50f, 0.2);
            Assert.That(t.Rate, Is.GreaterThan(1f));

            t.Advance(10.0); // a 10 s hitch: the clock loses time (capped at 5 ticks), so a far-late report must re-sync
            var before = t.PredictedTick;
            t.OnSnapshot(1006, -20, 50f, 0.3);
            Assert.That(t.Resyncs, Is.EqualTo(1));
            Assert.That(t.PredictedTick, Is.EqualTo(before + 22));
            Assert.That(t.TargetLead, Is.EqualTo(TickSync.InitialTarget), "a clock jump is a re-sync, not jitter");

            t.OnSnapshot(1030, -1, 50f, 1.0); // after the hold: a little late → raise the target
            Assert.That(t.TargetLead, Is.EqualTo(TickSync.InitialTarget + 1), "late raises the target");
            Assert.That(t.LateReports, Is.EqualTo(1));
        }

        [Test]
        public void Advance_RespectsRateAndCapsBursts()
        {
            var t = new TickSync(Rate);
            t.OnSnapshot(10, SnapHeader.LeadUnknown, 0f, 0);
            var ticks = 0;
            for (var i = 0; i < 60; i++)
                ticks += t.Advance(1.0 / 60);
            Assert.That(ticks, Is.InRange(29, 30));
            Assert.That(t.Advance(10.0), Is.EqualTo(TickSync.MaxTicksPerAdvance), "a hidden-tab hitch does not burst");
        }

        [Test]
        public void Target_Lowers_After10sWithoutDippingBelowIt()
        {
            var t = new TickSync(Rate);
            t.OnSnapshot(100, SnapHeader.LeadUnknown, 30f, 0);
            t.OnSnapshot(102, -1, 30f, 0.1);               // late once → target 3
            Assert.That(t.TargetLead, Is.EqualTo(3));
            var now = 1.0;
            for (var i = 0; i < 160; i++, now += 0.066)    // ~10.5 s at lead == target
                t.OnSnapshot((uint)(104 + 2 * i), 3, 30f, now);
            Assert.That(t.TargetLead, Is.EqualTo(2));
        }

        [Test]
        public void StaleStart_ResyncsOnce_WithoutRaisingTarget()
        {
            var t = new TickSync(Rate);
            t.OnSnapshot(100, SnapHeader.LeadUnknown, 30f, 0);
            // Commands start 3 s later from a clock that did not run: very late reports that persist (> StallPersistSeconds,
            // so this is a clock that is really behind, not a transport stall's backlog).
            for (var i = 0; i < 50; i++)
                t.OnSnapshot((uint)(200 + 2 * i), -80, 30f, 3.0 + i * 0.066);
            Assert.That(t.TargetLead, Is.EqualTo(TickSync.InitialTarget));
            Assert.That(t.Resyncs, Is.InRange(1, 3));
        }

        /// <summary>
        /// Closed-loop simulation: server at exactly 30 Hz, one-way latency + jitter, snapshots at 15 Hz carrying the
        /// min input lead. The client starts with a wrong guess and must settle with (almost) no late commands.
        /// </summary>
        [TestCase(20.0, 0.0)]
        [TestCase(75.0, 10.0)]
        [TestCase(150.0, 25.0)]
        public void ClosedLoop_ConvergesToTargetLead(double oneWayMs, double jitterMs)
        {
            var rng = new Random(3);
            var clientFps = 1.0 / 60.0;
            var t = new TickSync(Rate);
            var inFlightCmds = new List<(double arrive, uint tick)>();
            var inFlightSnaps = new List<(double arrive, uint tick, sbyte lead)>();
            var serverTick = 500u;
            var nextServerTickAt = 0.0;
            var minLead = int.MaxValue;
            var late = 0;
            var lateAfterSettle = 0;
            double Lat() => (oneWayMs + rng.NextDouble() * jitterMs) / 1000.0;

            // Bootstrap with a snapshot (client guesses the RTT 50 % too low).
            t.OnSnapshot(serverTick, SnapHeader.LeadUnknown, (float)(oneWayMs), 0.0);

            for (var now = 0.0; now < 30.0; now += clientFps)
            {
                // server
                while (nextServerTickAt <= now)
                {
                    // Deliver commands that arrived before this tick.
                    for (var i = inFlightCmds.Count - 1; i >= 0; i--)
                    {
                        if (inFlightCmds[i].arrive > nextServerTickAt)
                            continue;
                        var lead = (int)((long)inFlightCmds[i].tick - (serverTick + 1));
                        if (lead < 0)
                        {
                            late++;
                            if (nextServerTickAt > 10.0)
                                lateAfterSettle++;
                        }

                        minLead = Math.Min(minLead, lead);
                        inFlightCmds.RemoveAt(i);
                    }

                    serverTick++;
                    if (serverTick % 2 == 0)
                    {
                        var l = minLead == int.MaxValue ? SnapHeader.LeadUnknown : (sbyte)Math.Max(-127, Math.Min(127, minLead));
                        inFlightSnaps.Add((nextServerTickAt + Lat(), serverTick, l));
                        minLead = int.MaxValue;
                    }

                    nextServerTickAt += Dt;
                }

                // client
                for (var i = inFlightSnaps.Count - 1; i >= 0; i--)
                {
                    if (inFlightSnaps[i].arrive > now)
                        continue;
                    t.OnSnapshot(inFlightSnaps[i].tick, inFlightSnaps[i].lead, (float)(2 * oneWayMs), now);
                    inFlightSnaps.RemoveAt(i);
                }

                var n = t.Advance(clientFps);
                for (var k = 0; k < n; k++)
                    inFlightCmds.Add((now + Lat(), t.NextTick()));
            }

            TestContext.WriteLine($"oneWay={oneWayMs}ms jitter={jitterMs}ms → target={t.TargetLead} lastLead={t.LastReportedLead} late={late} lateAfter10s={lateAfterSettle} resyncs={t.Resyncs} rate={t.Rate:F3}");
            Assert.That(lateAfterSettle, Is.LessThanOrEqualTo(2), "commands keep arriving in time once settled");
            Assert.That(t.LastReportedLead, Is.InRange(0, t.TargetLead + 3));
        }
    }
}
