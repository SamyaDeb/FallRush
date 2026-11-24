using System;
using System.Text;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    public class ProtocolTests
    {
        [Test]
        public void Hello_RoundTrip()
        {
            var hello = new Protocol.Hello { ProtocolVersion = Protocol.Version, BuildVersion = "0.1.0-m1", CosmeticPreset = 7 };
            var bytes = Protocol.EncodeHello(hello);

            // v7: the extension is always present (name len + token + room len + tier)
            Assert.That(bytes.Length, Is.EqualTo(Protocol.HelloMinBytes + 8 + 1 + Protocol.TokenBytes + 2));
            Assert.That(bytes[0], Is.EqualTo(7)); // u16 little-endian (protocol v7, P7-UX)
            Assert.That(bytes[1], Is.EqualTo(0));

            Assert.That(Protocol.TryDecodeHello(bytes, out var decoded, out var error), Is.True, error);
            Assert.That(decoded.ProtocolVersion, Is.EqualTo(Protocol.Version));
            Assert.That(decoded.BuildVersion, Is.EqualTo("0.1.0-m1"));
            Assert.That(decoded.CosmeticPreset, Is.EqualTo(7));
        }

        [Test]
        public void Hello_RoundTrip_HighVersionAndEmptyBuild()
        {
            var bytes = Protocol.EncodeHello(new Protocol.Hello { ProtocolVersion = 999, BuildVersion = null, CosmeticPreset = 255 });
            Assert.That(Protocol.TryDecodeHello(bytes, out var d, out _), Is.True);
            Assert.That(d.ProtocolVersion, Is.EqualTo(999));
            Assert.That(d.BuildVersion, Is.EqualTo(string.Empty));
            Assert.That(d.CosmeticPreset, Is.EqualTo(255));
        }

        [Test]
        public void Hello_MaxLengthBuildVersion_RoundTrips()
        {
            var max = new string('x', Protocol.MaxBuildVersionBytes);
            var bytes = Protocol.EncodeHello(new Protocol.Hello { ProtocolVersion = 1, BuildVersion = max });
            Assert.That(bytes.Length, Is.EqualTo(Protocol.HelloMaxBytes));
            Assert.That(Protocol.TryDecodeHello(bytes, out var d, out _), Is.True);
            Assert.That(d.BuildVersion, Is.EqualTo(max));
        }

        [Test]
        public void Hello_OverMaxLength_EncodeThrows()
        {
            var tooLong = new string('x', Protocol.MaxBuildVersionBytes + 1);
            Assert.Throws<ArgumentException>(() => Protocol.EncodeHello(new Protocol.Hello { BuildVersion = tooLong }));
        }

        [Test]
        public void Hello_MultiByteUtf8_CountsBytesNotChars()
        {
            // 16 × "é" = 32 bytes (fits exactly); 17 × "é" = 34 bytes (does not).
            Assert.DoesNotThrow(() => Protocol.EncodeHello(new Protocol.Hello { BuildVersion = new string('é', 16) }));
            Assert.Throws<ArgumentException>(() => Protocol.EncodeHello(new Protocol.Hello { BuildVersion = new string('é', 17) }));
        }

        [Test]
        public void Hello_DeclaredLengthOverMax_Rejected()
        {
            var bytes = new byte[Protocol.HelloMinBytes + Protocol.MaxBuildVersionBytes + 1];
            bytes[0] = 1;
            bytes[2] = (byte)(Protocol.MaxBuildVersionBytes + 1);
            Assert.That(Protocol.TryDecodeHello(bytes, out _, out var error), Is.False);
            StringAssert.Contains("too long", error);
        }

        [Test]
        public void Hello_Truncated_RejectedAtEveryLength()
        {
            var full = Protocol.EncodeHello(new Protocol.Hello { ProtocolVersion = 1, BuildVersion = "abc", CosmeticPreset = 1 });
            for (var len = 0; len < full.Length; len++)
            {
                var cut = new byte[len];
                Array.Copy(full, cut, len);
                Assert.That(Protocol.TryDecodeHello(cut, out _, out var error), Is.False, $"length {len} accepted");
                StringAssert.Contains("truncated", error);
            }
        }

        [Test]
        public void Hello_Truncated_StillExposesProtocolVersion()
        {
            // A future-format client whose hello does not parse can still be told "protocol mismatch".
            var bytes = new byte[] { 0xE7, 0x03 }; // 999
            Assert.That(Protocol.TryDecodeHello(bytes, out var d, out _), Is.False);
            Assert.That(d.ProtocolVersion, Is.EqualTo(999));
        }

        [Test]
        public void Hello_TrailingBytes_Rejected()
        {
            var full = Protocol.EncodeHello(new Protocol.Hello { ProtocolVersion = 1, BuildVersion = "abc" });
            var extra = new byte[full.Length + 1];
            Array.Copy(full, extra, full.Length);
            Assert.That(Protocol.TryDecodeHello(extra, out _, out var error), Is.False);
            StringAssert.Contains("trailing", error);
        }

        [Test]
        public void Hello_InvalidUtf8_Rejected()
        {
            var bytes = new byte[] { 1, 0, 2, 0xC3, 0x28, 0 }; // invalid 2-byte sequence
            Assert.That(Protocol.TryDecodeHello(bytes, out _, out var error), Is.False);
            StringAssert.Contains("UTF-8", error);
        }

        [Test]
        public void HelloV4_NameAndToken_RoundTrip_AndBadExtensionRejected()
        {
            var token = new byte[16];
            for (var i = 0; i < 16; i++)
                token[i] = (byte)(i * 17 + 3);
            var bytes = Protocol.EncodeHello(new Protocol.Hello { ProtocolVersion = Protocol.Version, BuildVersion = "m4", CosmeticPreset = 2, Name = "Bolt fan ⚡ with a long name", Token = token });
            Assert.That(bytes.Length, Is.LessThanOrEqualTo(Protocol.HelloV4MaxBytes));
            Assert.That(Protocol.TryDecodeHello(bytes, out var d, out var err), Is.True, err);
            Assert.That(d.Token, Is.EqualTo(token));
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(d.Name), Is.LessThanOrEqualTo(Protocol.MaxNameBytes));
            Assert.That(d.Name, Does.StartWith("Bolt fan"));
            for (var cut = bytes.Length - 1; cut > bytes.Length - 18; cut--)
            {
                var b = new byte[cut];
                Array.Copy(bytes, b, cut);
                Assert.That(Protocol.TryDecodeHello(b, out _, out _), Is.False, $"cut {cut}");
            }

            var plain = Protocol.EncodeHello(new Protocol.Hello { ProtocolVersion = Protocol.Version, BuildVersion = "m4" });
            Assert.That(Protocol.TryDecodeHello(plain, out var p, out _), Is.True);
            Assert.That(p.Token, Is.EqualTo(new byte[Protocol.TokenBytes]), "v7: a zero token = a new player");
            Assert.That(p.RoomCode, Is.Null);
        }

        [Test]
        public void MessageNames_AreStable()
        {
            Assert.That(Protocol.Version, Is.EqualTo(7));
            Assert.That(Protocol.Msg.Match, Is.EqualTo("match"));
            Assert.That(Protocol.Msg.MatchEvent, Is.EqualTo("mevt"));
            Assert.That(Protocol.Msg.Reactive, Is.EqualTo("rx"));
            Assert.That(Protocol.Msg.Ready, Is.EqualTo("ready"));
            Assert.That(Protocol.Msg.Control, Is.EqualTo("ctl"));
            Assert.That(Protocol.Msg.Emote, Is.EqualTo("emo"));
            Assert.That(Protocol.Msg.Round, Is.EqualTo("round"));
            Assert.That(Protocol.Msg.RaceState, Is.EqualTo("rstate"));
            Assert.That(Protocol.Msg.Event, Is.EqualTo("evt"));
            Assert.That(Protocol.Msg.Ping, Is.EqualTo("ping"));
            Assert.That(Protocol.Msg.Pong, Is.EqualTo("pong"));
            Assert.That(Protocol.Msg.Input, Is.EqualTo("in"));
            Assert.That(Protocol.Msg.Snap, Is.EqualTo("snap"));
            Assert.That(Encoding.UTF8.GetByteCount(Protocol.Msg.Ping), Is.LessThan(16));
        }
    
        [Test]
        public void HelloV7_RoomAndTier_RoundTrip()
        {
            var h = new Protocol.Hello { ProtocolVersion = Protocol.Version, BuildVersion = "p7", CosmeticPreset = 4, Name = "Ann", Token = new byte[16], RoomCode = "goo7", SkillTier = 2,
                ShellAsset = "8d6SFeBgcitcisjU3NMvgV5GaVFxjsH9Gk8HCPvKzbfq", ShellWallet = "58YqMtZDoDAZ3zToumjRJa8zJuyU5bhpiFJqkTeZwAVk" };
            Assert.That(Protocol.TryDecodeHello(Protocol.EncodeHello(h), out var d, out var err), Is.True, err);
            Assert.That(d.RoomCode, Is.EqualTo("GOO7"), "upper-cased");
            Assert.That(d.SkillTier, Is.EqualTo(2));
            Assert.That(d.ShellWallet, Is.EqualTo(h.ShellWallet), "the shell still rides after the room block");
            Assert.That(d.Name, Is.EqualTo("Ann"));
        }

        [Test]
        public void HelloV7_BadRoomCodesMeanNoRoom()
        {
            Assert.That(Protocol.CleanRoom("ab"), Is.Null);
            Assert.That(Protocol.CleanRoom("ABCDEFGHJ"), Is.Null);
            Assert.That(Protocol.CleanRoom("AB-D"), Is.Null);
            Assert.That(Protocol.CleanRoom(" xk29 "), Is.EqualTo("XK29"));
            var h = new Protocol.Hello { ProtocolVersion = Protocol.Version, BuildVersion = "p7", RoomCode = "a b!", SkillTier = 9 };
            Assert.That(Protocol.TryDecodeHello(Protocol.EncodeHello(h), out var d, out var err), Is.True, err);
            Assert.That(d.RoomCode, Is.Null);
            Assert.That(d.SkillTier, Is.EqualTo(2), "tier clamped");
        }

        [Test]
        public void HelloV6_IsStillDecodedForTheMismatchMessage()
        {
            var v6 = Protocol.EncodeHello(new Protocol.Hello { ProtocolVersion = 6, BuildVersion = "old", Name = "Ann", Token = new byte[16] });
            Assert.That(Protocol.TryDecodeHello(v6, out var d, out var err), Is.True, err);
            Assert.That(d.ProtocolVersion, Is.EqualTo(6));
            Assert.That(d.RoomCode, Is.Null);
        }
    }
}
