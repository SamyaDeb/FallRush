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
        public const ushort Version = 7; // v7 (P7-UX): hello carries a private-room code + the player's skill tier (after the token, before the shell); v6 (G2.2): `match` slot rows carry the cosmetic preset (hello already sent it); v5 (G1): ctl Emote + `emo`, motor flag Stumbling (bit 4); v4 (M4): match / mevt / rx / ready / ctl, hello name + reconnect token; v3 (M3): snap race byte, round / rstate / evt

        /// <summary>Hard cap on players per game server (doc 01: 12-player matches).</summary>
        public const int MaxPlayers = 12;

        /// <summary>Max UTF-8 bytes of the build version string inside <c>hello</c>.</summary>
        public const int MaxBuildVersionBytes = 32;

        /// <summary>Smallest valid hello: u16 version + u8 length (0) + u8 cosmetic.</summary>
        public const int HelloMinBytes = 2 + 1 + 1;

        /// <summary>Largest valid hello without the v4 extension.</summary>
        public const int HelloMaxBytes = HelloMinBytes + MaxBuildVersionBytes;

        /// <summary>v4 extension after the cosmetic byte: <c>u8 nameLen | name UTF-8 (≤ 16) | 16 B reconnect token</c> (zeros = none).</summary>
        public const int MaxNameBytes = 16;
        public const int TokenBytes = 16;
        public const int HelloV4MaxBytes = HelloMaxBytes + 1 + MaxNameBytes + TokenBytes;

        /// <summary>Named custom messages (NGO <c>CustomMessagingManager</c>).</summary>
        public static class Msg
        {
            /// <summary>C→S: <c>double clientTimeSeconds</c>.</summary>
            public const string Ping = "ping";

            /// <summary>S→C: echoed <c>double clientTimeSeconds</c> + <c>u8 connectedPlayers</c>.</summary>
            public const string Pong = "pong";

            /// <summary>C→S, 30 Hz: the last 1–3 <see cref="InputCmd"/>s (<see cref="NetMessages"/>). Delivery: Unreliable.</summary>
            public const string Input = "in";

            /// <summary>S→C, 15 Hz (30 Hz option): <see cref="SnapHeader"/> + N × <see cref="PlayerState"/>. Delivery: UnreliableSequenced.</summary>
            public const string Snap = "snap";

            /// <summary>S→C, reliable: <see cref="RoundMessage"/> (round params + map content hash), on connect and per round.</summary>
            public const string Round = "round";

            /// <summary>S→C, reliable: <see cref="RaceStateMessage"/> (full round state), on connect and on every phase change.</summary>
            public const string RaceState = "rstate";

            /// <summary>S→C, reliable: <see cref="RaceEvent"/> (checkpoint / finish / qualify / round end) with a sequence number.</summary>
            public const string Event = "evt";

            /// <summary>S→C, reliable (M4): <see cref="MatchStateMessage"/>, the full match state for this recipient.</summary>
            public const string Match = "match";

            /// <summary>S→C, reliable (M4): one transcript <see cref="MatchEvent"/> (DEAL … COMPLETE_MATCH / ABORT), program order.</summary>
            public const string MatchEvent = "mevt";

            /// <summary>S→C, reliable (M4): reactive-element deltas / full state (<see cref="ReactiveCodec"/>).</summary>
            public const string Reactive = "rx";

            /// <summary>C→S, reliable (M4): <see cref="ReadyMessage"/> (map loaded, hash ok).</summary>
            public const string Ready = "ready";

            /// <summary>C→S, reliable (M4): <see cref="ControlCmd"/> (host start, back to lobby, rx resend).</summary>
            public const string Control = "ctl";

            /// <summary>S→C, reliable (G1): <see cref="EmoteCodec"/> (a player's emote, relayed to everyone; visual only).</summary>
            public const string Emote = "emo";
        }

        public struct Hello
        {
            public ushort ProtocolVersion;
            public string BuildVersion;
            public byte CosmeticPreset;
            /// <summary>v4: display name (≤ 16 UTF-8 bytes; null = "Player N").</summary>
            public string Name;
            /// <summary>v4: reconnect token from a previous <c>match</c> message (16 B; null / zeros = new player).</summary>
            public byte[] Token;
            /// <summary>P5 task 4 (optional, after the token): an owned Core capsule to wear (base58 asset + the wallet that owns it).
            /// The server checks ownership with an RPC read before applying its shell; anything else keeps the preset.</summary>
            public string ShellAsset, ShellWallet;
            /// <summary>v7 (P7-UX): a private room's code (≤ 8 ASCII; null / empty = a public lobby).</summary>
            public string RoomCode;
            /// <summary>v7 (P7-UX): the player's skill tier (0 Rookie, 1 Contender, 2 Champion): shapes bot difficulty only.</summary>
            public byte SkillTier;
        }

        /// <summary>v7: longest room code.</summary>
        public const int MaxRoomBytes = 8;

        /// <summary>v7: a room code is 4–8 characters of A–Z / 0–9 (upper-cased); anything else is "no room".</summary>
        public static string CleanRoom(string code)
        {
            if (string.IsNullOrEmpty(code))
                return null;
            var c = code.Trim().ToUpperInvariant();
            if (c.Length < 4 || c.Length > MaxRoomBytes)
                return null;
            foreach (var ch in c)
                if (!(ch >= 'A' && ch <= 'Z' || ch >= '0' && ch <= '9'))
                    return null;
            return c;
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

            var shell = !string.IsNullOrEmpty(hello.ShellAsset) && !string.IsNullOrEmpty(hello.ShellWallet) && hello.ShellAsset.Length <= 44 && hello.ShellWallet.Length <= 44;
            var v7 = hello.ProtocolVersion >= 7;
            var room = v7 ? CleanRoom(hello.RoomCode) ?? string.Empty : string.Empty;
            var ext = hello.Name != null || hello.Token != null || shell || v7;
            var name = ext ? MatchStateMessage.Utf8Clamp(hello.Name, MaxNameBytes) : Array.Empty<byte>();
            var shellBytes = shell ? 2 + hello.ShellAsset.Length + hello.ShellWallet.Length : 0;
            var v7Bytes = v7 && ext ? 2 + room.Length : 0;
            var buffer = new byte[HelloMinBytes + len + (ext ? 1 + name.Length + TokenBytes + v7Bytes + shellBytes : 0)];
            buffer[0] = (byte)(hello.ProtocolVersion & 0xFF);
            buffer[1] = (byte)(hello.ProtocolVersion >> 8);
            buffer[2] = (byte)len;
            StrictUtf8.GetBytes(version, 0, version.Length, buffer, 3);
            buffer[3 + len] = hello.CosmeticPreset;
            if (ext)
            {
                var o = HelloMinBytes + len;
                buffer[o++] = (byte)name.Length;
                Array.Copy(name, 0, buffer, o, name.Length);
                o += name.Length;
                if (hello.Token != null)
                    Array.Copy(hello.Token, 0, buffer, o, Math.Min(TokenBytes, hello.Token.Length));
                o += TokenBytes;
                if (v7)
                {
                    buffer[o++] = (byte)room.Length;
                    o += Encoding.ASCII.GetBytes(room, 0, room.Length, buffer, o);
                    buffer[o++] = hello.SkillTier;
                }

                if (shell)
                {
                    buffer[o++] = (byte)hello.ShellAsset.Length;
                    o += Encoding.ASCII.GetBytes(hello.ShellAsset, 0, hello.ShellAsset.Length, buffer, o);
                    buffer[o++] = (byte)hello.ShellWallet.Length;
                    Encoding.ASCII.GetBytes(hello.ShellWallet, 0, hello.ShellWallet.Length, buffer, o);
                }
            }

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

            var extAt = HelloMinBytes + len;
            if (data.Length > extAt)
            {
                // v4 extension: exactly u8 nameLen | name | 16 B token; anything else is trailing garbage.
                var nameLen = data[extAt];
                var tokenEnd = extAt + 1 + nameLen + TokenBytes;
                if (nameLen > MaxNameBytes || data.Length < tokenEnd)
                {
                    error = "hello has trailing bytes";
                    return false;
                }

                if (hello.ProtocolVersion >= 7)
                {
                    // v7: u8 roomLen | room ASCII (≤ 8) | u8 tier, then the optional shell extension
                    if (data.Length < tokenEnd + 2 || data[tokenEnd] > MaxRoomBytes || data.Length < tokenEnd + 2 + data[tokenEnd])
                    {
                        error = "hello truncated (room)";
                        return false;
                    }

                    var rl = data[tokenEnd];
                    hello.RoomCode = rl > 0 ? CleanRoom(Encoding.ASCII.GetString(data.Slice(tokenEnd + 1, rl).ToArray())) : null;
                    hello.SkillTier = (byte)Math.Min(2, (int)data[tokenEnd + 1 + rl]);
                    tokenEnd += 2 + rl;
                }

                if (data.Length > tokenEnd)
                {
                    // P5 shell extension: u8 len | asset base58 | u8 len | wallet base58 (≤ 44 each), nothing after it
                    var al = data[tokenEnd];
                    var wAt = tokenEnd + 1 + al;
                    if (al > 44 || data.Length <= wAt || data[wAt] > 44 || data.Length != wAt + 1 + data[wAt])
                    {
                        error = "hello has trailing bytes";
                        return false;
                    }

                    hello.ShellAsset = Encoding.ASCII.GetString(data.Slice(tokenEnd + 1, al).ToArray());
                    hello.ShellWallet = Encoding.ASCII.GetString(data.Slice(wAt + 1, data[wAt]).ToArray());
                }

                try
                {
                    hello.Name = StrictUtf8.GetString(data.Slice(extAt + 1, nameLen).ToArray());
                }
                catch (ArgumentException)
                {
                    error = "name is not valid UTF-8";
                    return false;
                }

                hello.Token = data.Slice(extAt + 1 + nameLen, TokenBytes).ToArray();
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
