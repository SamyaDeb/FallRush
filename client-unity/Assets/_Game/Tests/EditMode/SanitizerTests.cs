using System;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    public class SanitizerTests
    {
        private static InputCmd C(uint seq, uint tick, int mx = 0, int my = 0, byte b = 0) =>
            new InputCmd { Seq = seq, Tick = tick, MoveX = (sbyte)mx, MoveY = (sbyte)my, Buttons = b };

        [Test]
        public void OnTime_IsAppliedAtItsTick_AndAcked()
        {
            var s = new InputSanitizer();
            Assert.That(s.Offer(C(1, 10, 50), 9), Is.EqualTo(InputSanitizer.Result.Accepted));
            var miss = s.Consume(9, out var own9);
            Assert.That(own9, Is.False);
            Assert.That(miss.MoveX, Is.EqualTo(0));
            var c = s.Consume(10, out var own);
            Assert.That(own, Is.True);
            Assert.That(c.MoveX, Is.EqualTo(50));
            Assert.That(s.AckSeq, Is.EqualTo(1));
        }

        [Test]
        public void StaleAndDuplicateSeq_Dropped()
        {
            var s = new InputSanitizer();
            s.Offer(C(5, 10), 10);
            Assert.That(s.Offer(C(5, 10), 10), Is.EqualTo(InputSanitizer.Result.Redundant));
            Assert.That(s.Offer(C(4, 11), 10), Is.EqualTo(InputSanitizer.Result.Redundant));
            Assert.That(s.Counters.Redundant, Is.EqualTo(2));
            Assert.That(s.Counters.Violations, Is.EqualTo(0));
        }

        [Test]
        public void FarFuture_IsViolation_AndNotStored()
        {
            var s = new InputSanitizer();
            Assert.That(s.Offer(C(1, 100 + InputSanitizer.ViolationLeadTicks + 1), 100), Is.EqualTo(InputSanitizer.Result.FarFuture));
            Assert.That(s.Counters.Violations, Is.EqualTo(1));
            Assert.That(s.Offer(C(2, 100 + InputSanitizer.MaxLeadTicks), 100), Is.EqualTo(InputSanitizer.Result.Accepted));
        }

        [Test]
        public void Early_BeyondWindow_IsDroppedButNotAViolation_AndReportedAsLead()
        {
            var s = new InputSanitizer();
            Assert.That(s.Offer(C(1, 100 + InputSanitizer.MaxLeadTicks + 1), 100), Is.EqualTo(InputSanitizer.Result.Early));
            Assert.That(s.Offer(C(2, 100 + InputSanitizer.ViolationLeadTicks), 100), Is.EqualTo(InputSanitizer.Result.Early));
            Assert.That(s.Counters.Violations, Is.EqualTo(0));
            Assert.That(s.Counters.Early, Is.EqualTo(2));
            Assert.That(s.TakeMinLead(), Is.EqualTo(InputSanitizer.MaxLeadTicks + 1), "the client hears it is early and re-converges");
            var cmd = s.Consume(100 + InputSanitizer.MaxLeadTicks + 1, out var own);
            Assert.That(own, Is.False, "not stored");
        }

        [Test]
        public void Late_CarriesPressEdgesToNextTick()
        {
            var s = new InputSanitizer();
            s.Offer(C(1, 9, 0, 127), 9);
            s.Consume(9, out _);
            Assert.That(s.Offer(C(2, 9, 0, 127, InputCmd.JumpPressed | InputCmd.JumpHeld), 10), Is.EqualTo(InputSanitizer.Result.Late));
            var c = s.Consume(10, out var own);
            Assert.That(own, Is.False);
            Assert.That(c.Has(InputCmd.JumpPressed), Is.True, "late jump is not lost");
            Assert.That(s.TakeMinLead(), Is.EqualTo(-1));
        }

        [Test]
        public void Missing_RepeatsLastFor3Ticks_EdgesCleared_ThenNeutral()
        {
            var s = new InputSanitizer();
            s.Offer(C(1, 1, 60, -60, InputCmd.JumpPressed | InputCmd.JumpHeld), 1);
            var first = s.Consume(1, out _);
            Assert.That(first.Has(InputCmd.JumpPressed), Is.True);
            for (uint t = 2; t <= 4; t++)
            {
                var r = s.Consume(t, out var own);
                Assert.That(own, Is.False);
                Assert.That(r.MoveX, Is.EqualTo(60));
                Assert.That(r.Has(InputCmd.JumpPressed), Is.False, "edges are not repeated");
                Assert.That(r.Has(InputCmd.JumpHeld), Is.True);
            }

            var n = s.Consume(5, out _);
            Assert.That(n.MoveX, Is.EqualTo(0));
            Assert.That(n.Buttons, Is.EqualTo(0));
            Assert.That(s.Counters.Repeated, Is.EqualTo(3));
            Assert.That(s.Counters.Neutral, Is.EqualTo(1));
        }

        [Test]
        public void StallBurst_IsTolerated_SustainedFlood_IsNot()
        {
            // An honest client after a 5 s TCP stall: 150 messages arrive in one tick, then 1 per tick.
            var s = new InputSanitizer();
            for (var i = 0; i < 300; i++)
            {
                s.BeginTick();
                s.AdmitMessage();
            }

            s.BeginTick();
            for (var i = 0; i < 150; i++)
                s.AdmitMessage();
            for (var i = 0; i < 300; i++)
            {
                s.BeginTick();
                s.AdmitMessage();
            }

            Assert.That(s.Counters.Violations, Is.EqualTo(0));
            Assert.That(s.Counters.Backlog, Is.EqualTo(150 - InputSanitizer.MaxMessagesPerTick));

            // A flooder: 200 messages per tick → violations within the first few ticks (kick at 50).
            var f = new InputSanitizer();
            var ticks = 0;
            while (f.Counters.Violations < 50 && ticks < 10)
            {
                f.BeginTick();
                for (var i = 0; i < 200; i++)
                    f.AdmitMessage();
                ticks++;
            }

            Assert.That(f.Counters.Violations, Is.GreaterThanOrEqualTo(50));
            Assert.That(ticks, Is.LessThanOrEqualTo(3));
        }

        [Test]
        public void Flood_MessagesPerTickCapped_NewCommandsPerTickCapped()
        {
            var s = new InputSanitizer();
            var admitted = 0;
            for (var i = 0; i < 1000; i++)
                if (s.AdmitMessage())
                    admitted++;
            Assert.That(admitted, Is.EqualTo(InputSanitizer.MaxMessagesPerTick), "at most 32 decoded per tick");
            Assert.That(s.Counters.Backlog, Is.EqualTo((long)InputSanitizer.RateBurst - InputSanitizer.MaxMessagesPerTick), "a burst within the budget is not a violation");
            Assert.That(s.Counters.Flood, Is.EqualTo(1000 - (long)InputSanitizer.RateBurst), "beyond the budget it is");

            s.BeginTick();
            var accepted = 0;
            for (uint i = 1; i <= 10; i++)
                if (s.Offer(C(i, 10 + i), 10) == InputSanitizer.Result.Accepted)
                    accepted++;
            Assert.That(accepted, Is.EqualTo(InputSanitizer.MaxNewPerTick));
            Assert.That(s.Counters.Burst, Is.EqualTo(7));

            // The deferred one comes again as the redundant copy next tick and is accepted then.
            s.BeginTick();
            Assert.That(s.Offer(C(4, 14), 10), Is.EqualTo(InputSanitizer.Result.Accepted));
        }

        [Test]
        public void Sanitize_ClampsAxesLengthAndButtons()
        {
            var c = new InputCmd { MoveX = -128, MoveY = -128, Buttons = 0xFF };
            Assert.That(InputSanitizer.Sanitize(ref c), Is.True);
            Assert.That(c.MoveX * c.MoveX + c.MoveY * c.MoveY, Is.LessThanOrEqualTo(127 * 127));
            Assert.That(c.MoveX, Is.LessThan(0));
            Assert.That(c.MoveY, Is.LessThan(0));
            Assert.That(c.Buttons, Is.EqualTo(InputCmd.KnownButtons));

            var ok = new InputCmd { MoveX = 0, MoveY = 127, Buttons = InputCmd.JumpHeld };
            Assert.That(InputSanitizer.Sanitize(ref ok), Is.False);
        }

        [Test]
        public void Fuzz_RandomCommands_BoundedOutput()
        {
            var rng = new Random(42);
            var s = new InputSanitizer();
            uint tick = 100;
            uint seq = 1000;
            for (var i = 0; i < 50000; i++)
            {
                if (i % 7 == 0)
                {
                    s.Consume(tick, out _);
                    tick++;
                    s.BeginTick();
                }

                if (!s.AdmitMessage())
                    continue;
                // Mostly fresh, sometimes duplicate/stale, sometimes a random jump.
                var roll = rng.Next(10);
                seq = roll < 7 ? seq + 1 : roll < 9 ? seq - (uint)rng.Next(0, 3) : (uint)rng.Next();
                var c = new InputCmd
                {
                    Seq = seq,
                    Tick = (uint)(tick + rng.Next(-50, 60)),
                    MoveX = (sbyte)rng.Next(-128, 128),
                    MoveY = (sbyte)rng.Next(-128, 128),
                    Buttons = (byte)rng.Next(256),
                    CamYaw = (ushort)rng.Next(65536),
                };
                Assert.DoesNotThrow(() => s.Offer(c, tick));
                var applied = s.Consume(tick, out _);
                tick++;
                s.BeginTick();
                Assert.That(applied.MoveX * applied.MoveX + applied.MoveY * applied.MoveY, Is.LessThanOrEqualTo(127 * 127));
                Assert.That(applied.Buttons & ~InputCmd.KnownButtons, Is.EqualTo(0));
            }

            TestContext.WriteLine($"fuzz counters: accepted={s.Counters.Accepted} redundant={s.Counters.Redundant} late={s.Counters.Late} far={s.Counters.FarFuture} clamped={s.Counters.Clamped}");
        }
    }
}
