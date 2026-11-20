using System;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    public class SimCoreTests
    {
        private static Vec3[] SpawnRow()
        {
            var s = new Vec3[12];
            for (var i = 0; i < 12; i++)
                s[i] = new Vec3(-11f + 2f * i, 0.1f, 0f);
            return s;
        }

        private static AnalyticWorld Arena() => new AnalyticWorld()
            .Ground()
            .AddBox(new Vec3(-20, -1, -20), new Vec3(20, 2, -19))
            .AddBox(new Vec3(-20, -1, 19), new Vec3(20, 2, 20))
            .AddBox(new Vec3(-20, -1, -20), new Vec3(-19, 2, 20))
            .AddBox(new Vec3(19, -1, -20), new Vec3(20, 2, 20));

        [Test]
        public void TwelveBots_Run_StayValid_AndAreDeterministic()
        {
            var a = new SimCore(MovementConfig.Default, Arena(), SpawnRow());
            var b = new SimCore(MovementConfig.Default, Arena(), SpawnRow());
            a.SetBotArea(0, 0, 15, 15);
            b.SetBotArea(0, 0, 15, 15);
            for (var i = 0; i < 12; i++)
            {
                Assert.That(a.AddBot(), Is.EqualTo(i));
                b.AddBot();
            }

            Assert.That(a.AddBot(), Is.EqualTo(-1), "13th occupant rejected");
            for (var t = 0; t < 1800; t++)
            {
                a.Step();
                b.Step();
                for (var i = 0; i < 12; i++)
                    Assert.That(a.Slots[i].State, Is.EqualTo(b.Slots[i].State), $"tick {t} slot {i}");
            }

            var players = new PlayerState[12];
            Assert.That(a.WritePlayerStates(players), Is.EqualTo(12));
            for (var i = 0; i < 12; i++)
            {
                Assert.That(players[i].Flags & MotorState.Bot, Is.Not.Zero);
                Assert.That(Math.Abs(players[i].PosX), Is.LessThan(19000));
                Assert.That(Math.Abs(players[i].PosZ), Is.LessThan(19000));
                Assert.That(players[i].PosY, Is.GreaterThan(-100));
            }

            TestContext.WriteLine($"push events over 60 s: {a.PushEvents}");
        }

        [Test]
        public void Humans_ReplaceBots_WhenFull()
        {
            var sim = new SimCore(MovementConfig.Default, Arena(), SpawnRow());
            for (var i = 0; i < 12; i++)
                sim.AddBot();
            var slot = sim.AddHuman(42);
            Assert.That(slot, Is.EqualTo(11));
            Assert.That(sim.HumanCount, Is.EqualTo(1));
            Assert.That(sim.BotCount, Is.EqualTo(11));
            Assert.That(sim.FindSlot(42), Is.EqualTo(11));
            Assert.That(sim.Input(11), Is.Not.Null);
        }

        [Test]
        public void StackedPlayers_AreSeparated_AtMost2p5mps()
        {
            var spawns = new Vec3[12];
            for (var i = 0; i < 12; i++)
                spawns[i] = new Vec3(0f, 0.1f, 0f);
            var sim = new SimCore(MovementConfig.Default, Arena(), spawns);
            sim.AddHuman(1);
            sim.AddHuman(2);
            for (var t = 0; t < 30; t++)
                sim.Step();
            var a = sim.Slots[0].State.Position;
            var b = sim.Slots[1].State.Position;
            var d = (a - b).Horizontal.Magnitude;
            Assert.That(d, Is.GreaterThanOrEqualTo(0.62f - 0.01f), $"separated to {d:F3} m");
            // 2.5 m/s each way for 1 s at most → ≤ 5 m apart.
            Assert.That(d, Is.LessThan(5.01f));
        }

        [Test]
        public void FallVolume_RespawnsAfter1s()
        {
            var spawns = SpawnRow();
            var sim = new SimCore(MovementConfig.Default, new AnalyticWorld().AddBox(new Vec3(-30, -1, -2), new Vec3(30, 0, 2)), spawns);
            var slot = sim.AddHuman(7);
            var input = sim.Input(slot);
            var ticks = 0;
            uint seq = 0;
            while (!sim.Slots[slot].State.Has(MotorState.Respawning) && ticks < 300)
            {
                input.Offer(new InputCmd { Seq = ++seq, Tick = sim.NextTick, MoveY = 127 }, sim.NextTick);
                sim.Step();
                ticks++;
            }

            Assert.That(sim.Slots[slot].State.Has(MotorState.Respawning), Is.True, "fell below killY");
            var respawnTicks = 0;
            while (sim.Slots[slot].State.Has(MotorState.Respawning) && respawnTicks < 100)
            {
                sim.Step();
                respawnTicks++;
            }

            Assert.That(respawnTicks, Is.EqualTo(MovementConfig.Default.RespawnTicks));
            Assert.That(sim.Slots[slot].State.PosX, Is.EqualTo(MotorState.QuantizeMm(spawns[slot].X)));
            Assert.That(sim.Slots[slot].Respawns, Is.EqualTo(1));
        }

        [Test]
        public void SnapshotHeader_CarriesOwnTimersAndAck()
        {
            var sim = new SimCore(MovementConfig.Default, Arena(), SpawnRow());
            var slot = sim.AddHuman(9);
            sim.Input(slot).Offer(new InputCmd { Seq = 1, Tick = 1, Buttons = InputCmd.DivePressed, MoveY = 127 }, sim.NextTick);
            for (var i = 0; i < 20; i++)
                sim.Step();
            var h = sim.HeaderFor(slot, 1);
            Assert.That(h.OwnSlot, Is.EqualTo(slot));
            Assert.That(h.AckSeq, Is.EqualTo(1));
            Assert.That(h.InputLead, Is.EqualTo(0));
            Assert.That(h.OwnDiveCooldown, Is.EqualTo(sim.Slots[slot].State.DiveCooldownTicks));
            Assert.That(h.ServerTick, Is.EqualTo(20));
            Assert.That(sim.HeaderFor(slot, 1).InputLead, Is.EqualTo(SnapHeader.LeadUnknown), "lead window resets per snapshot");
        }
    }
}
