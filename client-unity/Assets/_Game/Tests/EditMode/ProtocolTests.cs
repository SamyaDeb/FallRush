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

            Assert.That(bytes.Length, Is.EqualTo(Protocol.HelloMinBytes + 8));
            Assert.That(bytes[0], Is.EqualTo(1)); // u16 little-endian
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
        public void MessageNames_AreStable()
        {
            Assert.That(Protocol.Version, Is.EqualTo(1));
            Assert.That(Protocol.Msg.Ping, Is.EqualTo("ping"));
            Assert.That(Protocol.Msg.Pong, Is.EqualTo("pong"));
            Assert.That(Encoding.UTF8.GetByteCount(Protocol.Msg.Ping), Is.LessThan(16));
        }
    }
}
