using FallRush.OnChain;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>
    /// Input run (2026-11-25): on the rollup the client stamps further ahead (T+5) inside a 13-tick window
    /// (set_input_lead on the arenas), so a chain micro-stall no longer gets our batches refused as out of window.
    /// The emulator and explicit ?lead= / ?window= keep their values.
    /// </summary>
    public class InputRunTests
    {
        [Test]
        public void RollupGetsWindow13AndLead5()
        {
            var c = new ChainConfig { target = "devnet" };
            c.ApplyRollupDefaults();
            Assert.AreEqual(13, c.inputWindow);
            Assert.AreEqual(5, c.inputLead);
        }

        [Test]
        public void EmulatorKeepsTheOldValues()
        {
            var c = new ChainConfig { target = "emu" };
            c.ApplyRollupDefaults();
            Assert.AreEqual(8, c.inputWindow);
            Assert.AreEqual(3, c.inputLead);
        }

        [Test]
        public void ExplicitWindowAndLeadWin()
        {
            var c = new ChainConfig { target = "devnet", inputWindow = 8, windowSet = true };
            c.ApplyRollupDefaults();
            Assert.AreEqual(8, c.inputWindow, "?window=8 = the old behaviour");
            Assert.AreEqual(5, c.inputLead, "lead ≤ window − 3");

            var d = new ChainConfig { target = "devnet", inputLead = 2, leadSet = true };
            d.ApplyRollupDefaults();
            Assert.AreEqual(13, d.inputWindow);
            Assert.AreEqual(2, d.inputLead);
        }

        [Test]
        public void TheExtraLeadStaysInsideTheWindow()
        {
            var c = new ChainConfig { target = "devnet" };
            c.ApplyRollupDefaults();
            // OnChainSession.MaxExtraLead = window − lead − LeadMargin (4 ticks kept for clock error)
            var maxExtra = c.inputWindow - c.inputLead - OnChainSession.LeadMargin;
            Assert.AreEqual(4, maxExtra);
            Assert.LessOrEqual(c.inputLead + maxExtra + c.batchTicks - 1, c.inputWindow, "the last tick of a batch fits too");
        }

        [Test]
        public void ClockOneWayIsCappedSoASlowHttpProbeCannotRunItEarly()
        {
            var a = new OnChainClock(30) { RttMs = 1800f };
            var b = new OnChainClock(30) { RttMs = 80f };
            for (var k = 0; k < 30; k++)
            {
                a.OnWorld((uint)(1000 + k), 10.0 + k / 30.0);
                b.OnWorld((uint)(1000 + k), 10.0 + k / 30.0);
            }
            var now = 11.0;
            // RTT 1,800 ms used to add 900 ms = 27 ticks; capped at 60 ms = 1.8 ticks
            Assert.That(a.ChainNow(now) - b.ChainNow(now), Is.LessThan(0.7), "60 ms cap vs 40 ms");
            Assert.That(a.ChainNow(now), Is.EqualTo(1030 + 1.8).Within(0.2));
        }
    
        /// <summary>Input run 3: a steady 30/s chain whose Worlds arrive 0.2–1.5 s late with 3 s gaps (production pattern).</summary>
        private static double Worst(bool disciplined, out bool wentBack)
        {
            var c = new OnChainClock(30) { RttMs = 80f, Disciplined = disciplined, NominalRateOnly = disciplined };
            var rng = new System.Random(7);
            var worst = 0.0;
            wentBack = false;
            var last = double.MinValue;
            var pending = new System.Collections.Generic.List<(double at, uint tick)>();
            var lastAt = 0.0;
            for (var t = 0.0; t < 60.0; t += 1.0 / 60)
            {
                // the chain: tick = 30·t; every 0.1 s a World leaves, delivered 0.2–1.5 s later, except during 3 s gaps
                var inGap = (int)(t / 6) % 2 == 1 && t % 6 < 3;
                if (((int)(t * 60)) % 6 == 0 && !inGap)
                {
                    // one ordered stream (as the socket): a World never arrives before an older one
                    lastAt = System.Math.Max(lastAt, t + 0.2 + rng.NextDouble() * 1.3);
                    pending.Add((lastAt, (uint)(t * 30)));
                }
                pending.Sort((a, b) => a.at.CompareTo(b.at));
                while (pending.Count > 0 && pending[0].at <= t)
                {
                    c.OnWorld(pending[0].tick, pending[0].at);
                    pending.RemoveAt(0);
                }
                if (!c.HasData)
                    continue;
                var est = c.ChainNow(t);
                if (t > 10)
                {
                    worst = System.Math.Max(worst, System.Math.Abs(est - t * 30));
                    if (est < last - 1e-6)
                        wentBack = true;
                }
                last = est;
            }
            return worst;
        }

        [Test]
        public void DisciplinedClockRidesThroughLateBurstyWorlds()
        {
            var raw = Worst(false, out var rawBack);
            var disc = Worst(true, out var discBack);
            TestContext.WriteLine($"worst error: raw {raw:F1} ticks (back {rawBack}), disciplined {disc:F1} ticks (back {discBack})");
            Assert.IsTrue(rawBack, "the bug: the raw estimate stepped backwards");
            Assert.IsFalse(discBack, "the disciplined clock never runs backwards");
            Assert.That(disc, Is.LessThan(raw), "closer to the chain than the raw estimate");
            Assert.That(disc, Is.LessThan(20), "within 20 ticks of the real chain tick");
        }
}
}
