using System;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    public class NetMessageTests
    {
        [Test]
        public void Sizes_MatchDoc04()
        {
            Assert.That(InputCmd.Size, Is.EqualTo(13));
            Assert.That(NetMessages.InputMaxBytes, Is.EqualTo(40));
            Assert.That(SnapHeader.Size, Is.EqualTo(18));
            Assert.That(PlayerState.Size, Is.EqualTo(22));
            Assert.That(NetMessages.SnapMaxBytes, Is.EqualTo(18 + 12 * 22));
        }

        private static InputCmd C(uint seq, uint tick, int mx, int my, byte b, ushort yaw) =>
            new InputCmd { Seq = seq, Tick = tick, MoveX = (sbyte)mx, MoveY = (sbyte)my, Buttons = b, CamYaw = yaw };

        [Test]
        public void Input_RoundTrip_1to3()
        {
            var cmds = new[] { C(uint.MaxValue, 123456789, -127, 127, 0x0F, 65535), C(41, 7, 0, -1, 0, 0), C(40, 6, 5, 6, 1, 300) };
            var buf = new byte[NetMessages.InputMaxBytes];
            var outCmds = new InputCmd[3];
            for (var n = 1; n <= 3; n++)
            {
                var len = NetMessages.EncodeInput(buf, cmds.AsSpan(0, n));
                Assert.That(len, Is.EqualTo(1 + 13 * n));
                Assert.That(NetMessages.TryDecodeInput(buf.AsSpan(0, len), outCmds, out var count, out var err), Is.True, err);
                Assert.That(count, Is.EqualTo(n));
                for (var i = 0; i < n; i++)
                    Assert.That(outCmds[i], Is.EqualTo(cmds[i]));
            }
        }

        [Test]
        public void Input_Truncated_TrailingAndBadCount_Rejected()
        {
            var buf = new byte[NetMessages.InputMaxBytes + 1];
            var len = NetMessages.EncodeInput(buf, new[] { C(1, 1, 1, 1, 1, 1), C(0, 0, 0, 0, 0, 0) });
            var dst = new InputCmd[3];
            for (var l = 0; l < len; l++)
                Assert.That(NetMessages.TryDecodeInput(buf.AsSpan(0, l), dst, out _, out _), Is.False, $"prefix {l}");
            Assert.That(NetMessages.TryDecodeInput(buf.AsSpan(0, len + 1), dst, out _, out _), Is.False, "trailing byte");
            buf[0] = 0;
            Assert.That(NetMessages.TryDecodeInput(buf.AsSpan(0, 1), dst, out _, out _), Is.False);
            buf[0] = 4;
            Assert.That(NetMessages.TryDecodeInput(new byte[1 + 4 * 13].AsSpan(), dst, out _, out _), Is.False, "count 4");
        }

        private static PlayerState P(byte slot, int x, int y, int z) => new PlayerState
        {
            Slot = slot, PosX = x, PosY = y, PosZ = z, VelX = -32767, VelY = 1234, VelZ = -1, Yaw = 200, Flags = 0x81, StunTicks = 15,
        };

        [Test]
        public void Snap_RoundTrip_12Players()
        {
            var players = new PlayerState[12];
            for (var i = 0; i < 12; i++)
                players[i] = P((byte)i, int.MinValue + i, 1000 * i - 6000, int.MaxValue - i);
            var h = new SnapHeader
            {
                ServerTick = 4000000000u, AckSeq = 77, InputLead = -3, OwnSlot = 5,
                OwnCoyote = 3, OwnJumpBuffer = 4, OwnDiveCooldown = 30, OwnLand = 11, OwnImmunity = 9, OwnSurface = 2, OwnEvents = SnapHeader.EventPushed,
            };
            var buf = new byte[NetMessages.SnapMaxBytes];
            var len = NetMessages.EncodeSnap(buf, h, players);
            Assert.That(len, Is.EqualTo(282));

            var outPlayers = new PlayerState[12];
            Assert.That(NetMessages.TryDecodeSnap(buf.AsSpan(0, len), out var h2, outPlayers, out var err), Is.True, err);
            Assert.That(h2.ServerTick, Is.EqualTo(h.ServerTick));
            Assert.That(h2.AckSeq, Is.EqualTo(77));
            Assert.That(h2.InputLead, Is.EqualTo(-3));
            Assert.That(h2.OwnSlot, Is.EqualTo(5));
            Assert.That(h2.OwnCoyote, Is.EqualTo(3));
            Assert.That(h2.OwnJumpBuffer, Is.EqualTo(4));
            Assert.That(h2.OwnDiveCooldown, Is.EqualTo(30));
            Assert.That(h2.OwnLand, Is.EqualTo(11));
            Assert.That(h2.OwnImmunity, Is.EqualTo(9));
            Assert.That(h2.OwnSurface, Is.EqualTo(2));
            Assert.That(h2.OwnEvents, Is.EqualTo(SnapHeader.EventPushed));
            Assert.That(h2.Count, Is.EqualTo(12));
            for (var i = 0; i < 12; i++)
            {
                Assert.That(outPlayers[i].Slot, Is.EqualTo(players[i].Slot));
                Assert.That(outPlayers[i].PosX, Is.EqualTo(players[i].PosX));
                Assert.That(outPlayers[i].PosY, Is.EqualTo(players[i].PosY));
                Assert.That(outPlayers[i].PosZ, Is.EqualTo(players[i].PosZ));
                Assert.That(outPlayers[i].VelX, Is.EqualTo(players[i].VelX));
                Assert.That(outPlayers[i].VelY, Is.EqualTo(players[i].VelY));
                Assert.That(outPlayers[i].VelZ, Is.EqualTo(players[i].VelZ));
                Assert.That(outPlayers[i].Yaw, Is.EqualTo(200));
                Assert.That(outPlayers[i].Flags, Is.EqualTo(0x81));
                Assert.That(outPlayers[i].StunTicks, Is.EqualTo(15));
            }
        }

        [Test]
        public void Snap_Truncated_Trailing_BadCount_BadSlot_Rejected()
        {
            var players = new[] { P(0, 1, 2, 3), P(11, 4, 5, 6) };
            var buf = new byte[NetMessages.SnapMaxBytes + 1];
            var len = NetMessages.EncodeSnap(buf, new SnapHeader { OwnSlot = SnapHeader.NoSlot }, players);
            var dst = new PlayerState[12];
            for (var l = 0; l < len; l++)
                Assert.That(NetMessages.TryDecodeSnap(buf.AsSpan(0, l), out _, dst, out _), Is.False, $"prefix {l}");
            Assert.That(NetMessages.TryDecodeSnap(buf.AsSpan(0, len + 1), out _, dst, out _), Is.False, "trailing");

            var bad = new byte[18 + 13 * 22];
            Array.Copy(buf, bad, len);
            bad[17] = 13; // count
            Assert.That(NetMessages.TryDecodeSnap(bad, out _, new PlayerState[13], out _), Is.False);

            bad = (byte[])buf.Clone();
            bad[18] = 12; // first slot
            Assert.That(NetMessages.TryDecodeSnap(bad.AsSpan(0, len), out _, dst, out _), Is.False);

            bad = (byte[])buf.Clone();
            bad[9] = 12; // own slot (not 255)
            Assert.That(NetMessages.TryDecodeSnap(bad.AsSpan(0, len), out _, dst, out _), Is.False);
        }

        [Test]
        public void Fuzz_RandomBytes_NeverThrow()
        {
            var rng = new Random(2026);
            var cmds = new InputCmd[3];
            var players = new PlayerState[12];
            var okIn = 0;
            var okSnap = 0;
            for (var i = 0; i < 20000; i++)
            {
                var b = new byte[rng.Next(0, 320)];
                rng.NextBytes(b);
                if (rng.Next(4) == 0 && b.Length > 0)
                    b[0] = (byte)rng.Next(1, 4); // plausible count
                Assert.DoesNotThrow(() =>
                {
                    if (NetMessages.TryDecodeInput(b, cmds, out _, out _))
                        okIn++;
                    if (NetMessages.TryDecodeSnap(b, out _, players, out _))
                        okSnap++;
                });
            }

            TestContext.WriteLine($"fuzz: {okIn} random buffers decoded as valid in, {okSnap} as valid snap (of 20000)");
        }
    }
}
