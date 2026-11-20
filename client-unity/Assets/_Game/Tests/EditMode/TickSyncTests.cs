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
        public void Dilation_SlowsWhenEarly_SpeedsUpWhenLate_ResyncsWhenFar()
        {
            var t = new TickSync(Rate);
            t.OnSnapshot(1000, SnapHeader.LeadUnknown, 50f, 0);
            t.OnSnapshot(1002, 6, 50f, 0.1);
            Assert.That(t.Rate, Is.LessThan(1f));
            t.OnSnapshot(1004, 1, 50f, 0.2);
            Assert.That(t.Rate, Is.GreaterThan(1f));

            var before = t.PredictedTick;
            t.OnSnapshot(1006, -20, 50f, 0.3);
            Assert.That(t.Resyncs, Is.EqualTo(1));
            Assert.That(t.PredictedTick, Is.GreaterThan(before + 15));
            Assert.That(t.TargetLead, Is.EqualTo(TickSync.InitialTarget + 1), "late raises the target");
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
