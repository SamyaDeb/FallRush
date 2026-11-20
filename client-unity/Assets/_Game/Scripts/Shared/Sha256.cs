using System;

namespace FallRush.Shared
{
    /// <summary>
    /// Minimal SHA-256 (FIPS 180-4), pure C# integer code. Used for the map content hash and the hazard-vector digest
    /// so every build (Mono server, IL2CPP/wasm client) hashes with the same code, independent of the platform's
    /// System.Security.Cryptography (which code stripping or WebGL could change).
    /// </summary>
    public sealed class Sha256
    {
        private static readonly uint[] K =
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
        };

        private readonly uint[] _h = new uint[8];
        private readonly byte[] _block = new byte[64];
        private readonly uint[] _w = new uint[64];
        private int _blockLen;
        private ulong _total;

        public Sha256() => Reset();

        public void Reset()
        {
            _h[0] = 0x6a09e667; _h[1] = 0xbb67ae85; _h[2] = 0x3c6ef372; _h[3] = 0xa54ff53a;
            _h[4] = 0x510e527f; _h[5] = 0x9b05688c; _h[6] = 0x1f83d9ab; _h[7] = 0x5be0cd19;
            _blockLen = 0;
            _total = 0;
        }

        public void Update(ReadOnlySpan<byte> data)
        {
            _total += (ulong)data.Length;
            var i = 0;
            while (i < data.Length)
            {
                var n = Math.Min(64 - _blockLen, data.Length - i);
                data.Slice(i, n).CopyTo(new Span<byte>(_block, _blockLen, n));
                _blockLen += n;
                i += n;
                if (_blockLen == 64)
                {
                    Compress();
                    _blockLen = 0;
                }
            }
        }

        public byte[] Final()
        {
            var bits = _total * 8;
            var pad = new byte[(_blockLen < 56 ? 56 - _blockLen : 120 - _blockLen) + 8];
            pad[0] = 0x80;
            for (var i = 0; i < 8; i++)
                pad[pad.Length - 1 - i] = (byte)(bits >> (8 * i));
            var saved = _total;
            Update(pad);
            _total = saved;
            var r = new byte[32];
            for (var i = 0; i < 8; i++)
            {
                r[i * 4] = (byte)(_h[i] >> 24);
                r[i * 4 + 1] = (byte)(_h[i] >> 16);
                r[i * 4 + 2] = (byte)(_h[i] >> 8);
                r[i * 4 + 3] = (byte)_h[i];
            }

            return r;
        }

        public static byte[] Hash(ReadOnlySpan<byte> data)
        {
            var s = new Sha256();
            s.Update(data);
            return s.Final();
        }

        public static string Hex(byte[] h)
        {
            var c = new char[h.Length * 2];
            const string d = "0123456789abcdef";
            for (var i = 0; i < h.Length; i++)
            {
                c[i * 2] = d[h[i] >> 4];
                c[i * 2 + 1] = d[h[i] & 15];
            }

            return new string(c);
        }

        private static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));

        private void Compress()
        {
            var w = _w;
            for (var i = 0; i < 16; i++)
                w[i] = (uint)(_block[i * 4] << 24 | _block[i * 4 + 1] << 16 | _block[i * 4 + 2] << 8 | _block[i * 4 + 3]);
            for (var i = 16; i < 64; i++)
            {
                var s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
                var s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }

            uint a = _h[0], b = _h[1], c = _h[2], d = _h[3], e = _h[4], f = _h[5], g = _h[6], h = _h[7];
            for (var i = 0; i < 64; i++)
            {
                var s1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
                var ch = (e & f) ^ (~e & g);
                var t1 = h + s1 + ch + K[i] + w[i];
                var s0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
                var maj = (a & b) ^ (a & c) ^ (b & c);
                var t2 = s0 + maj;
                h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
            }

            _h[0] += a; _h[1] += b; _h[2] += c; _h[3] += d; _h[4] += e; _h[5] += f; _h[6] += g; _h[7] += h;
        }
    }

    /// <summary>Little-endian canonical byte writer for content hashes (map hash, vector digest).</summary>
    public sealed class CanonicalWriter
    {
        private byte[] _buf = new byte[1024];
        public int Length { get; private set; }

        public ReadOnlySpan<byte> Bytes => new ReadOnlySpan<byte>(_buf, 0, Length);

        private void Ensure(int n)
        {
            if (Length + n <= _buf.Length)
                return;
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
        }

        public CanonicalWriter U8(int v)
        {
            Ensure(1);
            _buf[Length++] = (byte)v;
            return this;
        }

        public CanonicalWriter I32(int v)
        {
            Ensure(4);
            _buf[Length++] = (byte)v;
            _buf[Length++] = (byte)(v >> 8);
            _buf[Length++] = (byte)(v >> 16);
            _buf[Length++] = (byte)(v >> 24);
            return this;
        }

        public CanonicalWriter U32(uint v) => I32((int)v);

        public CanonicalWriter Str(string s)
        {
            s ??= string.Empty;
            I32(s.Length);
            foreach (var ch in s)
            {
                Ensure(2);
                _buf[Length++] = (byte)ch;
                _buf[Length++] = (byte)(ch >> 8);
            }

            return this;
        }

        public CanonicalWriter Ints(int[] a)
        {
            if (a == null)
                return I32(-1);
            I32(a.Length);
            foreach (var v in a)
                I32(v);
            return this;
        }

        public byte[] Sha256() => FallRush.Shared.Sha256.Hash(Bytes);
    }
}
