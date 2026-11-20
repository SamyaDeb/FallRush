using System;
using System.Collections.Generic;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>M4 protocol v4: <c>match</c>, <c>mevt</c>, <c>ready</c>, <c>ctl</c>, <c>rx</c> round trips, sizes, strict decode, fuzz; <c>snap</c> stays 294 B for 12.</summary>
    public class MatchMessageTests
    {
        private static MatchStateMessage Full12()
        {
            var m = new MatchStateMessage
            {
                Phase = MatchPhase.Intermission, PhaseStartTick = 1234, PhaseEndTick = 1474, ServerTick = 1300, Round = 2, RoundCount = 4, LoadSerial = 7,
                Flags = MatchStateMessage.FlagPractice, HostSlot = 0, OwnSlot = 3, Champion = 255, PlayerCount = 12, EventCount = 23, MatchSerial = 5,
            };
            for (var i = 0; i < 16; i++) m.MatchId[i] = (byte)(i + 1);
            for (var i = 0; i < 32; i++) { m.Seed[i] = (byte)(200 - i); m.Transcript[i] = (byte)(i * 3); }
            for (var i = 0; i < 16; i++) m.Token[i] = (byte)(i * 11);
            m.Targets = new byte[] { 8, 5, 3, 1 };
            m.Maps = new byte[] { 0, 1, 2, 3 };
            var slots = new List<MatchSlotInfo>();
            for (var i = 0; i < 12; i++)
                slots.Add(new MatchSlotInfo
                {
                    Slot = (byte)i, Flags = (byte)(MatchSlotInfo.Active | MatchSlotInfo.Rostered | (i > 0 ? MatchSlotInfo.Bot : 0) | (i < 8 ? MatchSlotInfo.Alive : 0)),
                    Tier = i > 0 ? BotTier.Normal : BotTier.Reference, Reason = i >= 8 ? ElimReason.TimeoutRank : ElimReason.NotQualified, EliminatedRound = (byte)(i >= 8 ? 1 : 0),
                    Places = new[] { (byte)(i + 1), (byte)0, (byte)0, (byte)0 }, Name = i == 0 ? "Bolt fan with a long name ⚡⚡" : MatchDirector.BotNames[i],
                });
            m.Slots = slots.ToArray();
            return m;
        }

        [Test]
        public void Match_RoundTrip_SizeBound_AndStrict()
        {
            var m = Full12();
            var buf = new byte[MatchStateMessage.MaxSize + 64];
            var n = m.Encode(buf);
            TestContext.WriteLine($"match for 12 (names as shown): {n} B, header {MatchStateMessage.HeaderSize} B, max {MatchStateMessage.MaxSize} B");
            Assert.That(n, Is.LessThanOrEqualTo(MatchStateMessage.MaxSize));
            Assert.That(MatchStateMessage.MaxSize, Is.LessThanOrEqualTo(1296 - 16), "fits one NGO non-fragmented message");
            Assert.That(MatchStateMessage.TryDecode(new ReadOnlySpan<byte>(buf, 0, n), out var d), Is.True);
            Assert.That(d.Phase, Is.EqualTo(MatchPhase.Intermission));
            Assert.That(d.Token, Is.EqualTo(m.Token));
            Assert.That(d.Seed, Is.EqualTo(m.Seed));
            Assert.That(d.Slots.Length, Is.EqualTo(12));
            Assert.That(d.Slots[0].Name, Is.EqualTo(System.Text.Encoding.UTF8.GetString(MatchStateMessage.Utf8Clamp(m.Slots[0].Name, MatchSlotInfo.MaxName))));
            Assert.That(d.Slots[9].Reason, Is.EqualTo(ElimReason.TimeoutRank));
            Assert.That(d.Slots[9].Has(MatchSlotInfo.Bot), Is.True);
            for (var cut = 0; cut < n; cut++)
                Assert.That(MatchStateMessage.TryDecode(new ReadOnlySpan<byte>(buf, 0, cut), out _), Is.False, $"truncated at {cut}");
            Assert.That(MatchStateMessage.TryDecode(new ReadOnlySpan<byte>(buf, 0, n + 1), out _), Is.False, "trailing byte");
        }

        [Test]
        public void MatchEvent_RoundTrip_Sizes()
        {
            var buf = new byte[64];
            var deal = new MatchEvent { Seq = 0, Tag = Transcript.Deal, Round = 0, Slot = Transcript.NoSlot, Arg = new byte[32], Tick = 99 };
            var n = MatchEventCodec.Encode(buf, in deal);
            Assert.That(n, Is.EqualTo(44));
            Assert.That(MatchEventCodec.TryDecode(new ReadOnlySpan<byte>(buf, 0, n), out var d), Is.True);
            Assert.That(d.Arg.Length, Is.EqualTo(32));
            var q = new MatchEvent { Seq = 5, Tag = Transcript.Qualified, Round = 2, Slot = 7, Arg = new byte[] { 3 }, Tick = 1234 };
            n = MatchEventCodec.Encode(buf, in q);
            Assert.That(n, Is.EqualTo(13));
            Assert.That(MatchEventCodec.TryDecode(new ReadOnlySpan<byte>(buf, 0, n), out d), Is.True);
            Assert.That((d.Seq, d.Tag, d.Round, d.Slot, d.Arg[0], d.Tick), Is.EqualTo((5u, Transcript.Qualified, (byte)2, (byte)7, (byte)3, 1234u)));
            buf[4] = 9; // bad tag
            Assert.That(MatchEventCodec.TryDecode(new ReadOnlySpan<byte>(buf, 0, n), out _), Is.False);
        }

        [Test]
        public void ReadyAndControl_RoundTrip_AndReject()
        {
            var buf = new byte[8];
            var n = new ReadyMessage { LoadSerial = 3, MapHashPrefix = 0xDEADBEEF }.Encode(buf);
            Assert.That(n, Is.EqualTo(ReadyMessage.Size));
            Assert.That(ReadyMessage.TryDecode(new ReadOnlySpan<byte>(buf, 0, n), out var r), Is.True);
            Assert.That((r.LoadSerial, r.MapHashPrefix), Is.EqualTo(((byte)3, 0xDEADBEEFu)));
            Assert.That(ReadyMessage.Prefix(new byte[] { 0xEF, 0xBE, 0xAD, 0xDE }), Is.EqualTo(0xDEADBEEFu));
            n = ControlCodec.Encode(buf, ControlCmd.RxResend, 77);
            Assert.That(ControlCodec.TryDecode(new ReadOnlySpan<byte>(buf, 0, n), out var c, out var arg), Is.True);
            Assert.That((c, arg), Is.EqualTo((ControlCmd.RxResend, 77u)));
            buf[0] = 0;
            Assert.That(ControlCodec.TryDecode(new ReadOnlySpan<byte>(buf, 0, n), out _, out _), Is.False);
        }

        [Test]
        public void Snapshot_StaysUnder320BytesFor12_InV4()
        {
            Assert.That(SnapHeader.Size + 12 * PlayerState.Size, Is.EqualTo(294));
            Assert.That(NetMessages.SnapMaxBytes, Is.LessThan(320));
        }

        [Test]
        public void Fuzz_NewDecoders_NeverThrow()
        {
            var rnd = new Random(42);
            var buf = new byte[600];
            var tmp = new List<RxEntry>();
            for (var i = 0; i < 20000; i++)
            {
                var n = rnd.Next(0, buf.Length);
                rnd.NextBytes(buf);
                var s = new ReadOnlySpan<byte>(buf, 0, n);
                MatchStateMessage.TryDecode(s, out _);
                MatchEventCodec.TryDecode(s, out _);
                ReadyMessage.TryDecode(s, out _);
                ControlCodec.TryDecode(s, out _, out _);
                ReactiveCodec.TryDecode(s, out _, out _, out _, tmp);
            }
        }
    }
}
