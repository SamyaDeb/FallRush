using System;
using System.Text;

namespace FallRush.Shared
{
    /// <summary>
    /// Wire protocol constants and the connection-approval <c>hello</c> payload.
    /// Pure C# (no UnityEngine) so it is testable in EditMode and reusable by tools.
    /// </summary>
    public static class Protocol
    {
        /// <summary>Bump on any incompatible change to messages or approval payload.</summary>
        public const ushort Version = 1;

        /// <summary>Hard cap on players per game server (doc 01: 12-player matches).</summary>
        public const int MaxPlayers = 12;

        /// <summary>Max UTF-8 bytes of the build version string inside <c>hello</c>.</summary>
        public const int MaxBuildVersionBytes = 32;

        /// <summary>Smallest valid hello: u16 version + u8 length (0) + u8 cosmetic.</summary>
        public const int HelloMinBytes = 2 + 1 + 1;

        /// <summary>Largest valid hello.</summary>
        public const int HelloMaxBytes = HelloMinBytes + MaxBuildVersionBytes;

        /// <summary>Named custom messages (NGO <c>CustomMessagingManager</c>).</summary>
        public static class Msg
        {
            /// <summary>C→S: <c>double clientTimeSeconds</c>.</summary>
            public const string Ping = "ping";

            /// <summary>S→C: echoed <c>double clientTimeSeconds</c> + <c>u8 connectedPlayers</c>.</summary>
            public const string Pong = "pong";
        }

        public struct Hello
        {
            public ushort ProtocolVersion;
            public string BuildVersion;
            public byte CosmeticPreset;
        }

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Layout (little-endian): <c>u16 protocolVersion | u8 buildVersionLen | buildVersion UTF-8 | u8 cosmeticPreset</c>.
        /// </summary>
        /// <exception cref="ArgumentException">Build version longer than <see cref="MaxBuildVersionBytes"/> UTF-8 bytes.</exception>
        public static byte[] EncodeHello(in Hello hello)
        {
            var version = hello.BuildVersion ?? string.Empty;
            var len = StrictUtf8.GetByteCount(version);
            if (len > MaxBuildVersionBytes)
                throw new ArgumentException($"buildVersion is {len} UTF-8 bytes; max is {MaxBuildVersionBytes}.", nameof(hello));

            var buffer = new byte[HelloMinBytes + len];
            buffer[0] = (byte)(hello.ProtocolVersion & 0xFF);
            buffer[1] = (byte)(hello.ProtocolVersion >> 8);
            buffer[2] = (byte)len;
            StrictUtf8.GetBytes(version, 0, version.Length, buffer, 3);
            buffer[3 + len] = hello.CosmeticPreset;
            return buffer;
        }

        /// <summary>
        /// Strict decode: rejects truncated buffers, oversize or invalid UTF-8 strings, and trailing bytes.
        /// The protocol version is decoded first so a mismatch can be reported even if later fields changed.
        /// </summary>
        public static bool TryDecodeHello(ReadOnlySpan<byte> data, out Hello hello, out string error)
        {
            hello = default;
            if (data.Length < 2)
            {
                error = "hello truncated (no protocol version)";
                return false;
            }

            hello.ProtocolVersion = (ushort)(data[0] | (data[1] << 8));

            if (data.Length < HelloMinBytes)
            {
                error = "hello truncated";
                return false;
            }

            int len = data[2];
            if (len > MaxBuildVersionBytes)
            {
                error = $"buildVersion too long ({len} > {MaxBuildVersionBytes})";
                return false;
            }

            if (data.Length < HelloMinBytes + len)
            {
                error = "hello truncated (buildVersion)";
                return false;
            }

            if (data.Length > HelloMinBytes + len)
            {
                error = "hello has trailing bytes";
                return false;
            }

            try
            {
                hello.BuildVersion = StrictUtf8.GetString(data.Slice(3, len).ToArray());
            }
            catch (ArgumentException)
            {
                error = "buildVersion is not valid UTF-8";
                return false;
            }

            hello.CosmeticPreset = data[3 + len];
            error = null;
            return true;
        }
    }
}
