using System;
using System.IO;
using FallRush.Shared;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace FallRush.Net
{
    /// <summary>
    /// The client's reconnect token (M4-6): issued by the server at join (in <c>match</c>), sent back in <c>hello</c> on a
    /// reconnect so the same slot is taken again. WebGL keeps it in the tab's <c>sessionStorage</c>; desktop keeps it in
    /// memory, or in a file with <c>-fallrushTokenFile path</c> (the probe's kill-and-restart test).
    /// TODO(M5): replaced by the Relay's signed guest token.
    /// </summary>
    public static class SessionToken
    {
        private const string Key = "fallrush.reconnect";
        private static byte[] s_Memory;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern IntPtr FallRushSessionGet(string key);
        [DllImport("__Internal")] private static extern void FallRushSessionSet(string key, string value);
#endif

        private static string FilePath()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "-fallrushTokenFile", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        public static byte[] Load()
        {
            if (s_Memory != null)
                return s_Memory;
            string hex = null;
#if UNITY_WEBGL && !UNITY_EDITOR
            var p = FallRushSessionGet(Key);
            if (p != IntPtr.Zero)
                hex = Marshal.PtrToStringUTF8(p);
#else
            var f = FilePath();
            if (f != null && File.Exists(f))
                hex = File.ReadAllText(f).Trim();
#endif
            if (hex == null || hex.Length != 32)
                return null;
            var b = new byte[16];
            for (var i = 0; i < 16; i++)
                if (!byte.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out b[i]))
                    return null;
            s_Memory = b;
            return b;
        }

        public static void Save(byte[] token)
        {
            if (token == null || token.Length != 16)
                return;
            var zero = true;
            foreach (var x in token)
                zero &= x == 0;
            if (zero)
                return;
            if (s_Memory != null && token.AsSpan().SequenceEqual(s_Memory))
                return;
            s_Memory = (byte[])token.Clone();
            var hex = Sha256.Hex(token);
#if UNITY_WEBGL && !UNITY_EDITOR
            FallRushSessionSet(Key, hex);
#else
            var f = FilePath();
            if (f != null)
                File.WriteAllText(f, hex);
#endif
        }

        public static void Clear()
        {
            s_Memory = null;
#if UNITY_WEBGL && !UNITY_EDITOR
            FallRushSessionSet(Key, "");
#else
            var f = FilePath();
            if (f != null && File.Exists(f))
                File.Delete(f);
#endif
        }
    }
}
