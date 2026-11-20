using System;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// Cross-platform determinism self-test (M3 §3.4): runs <see cref="HazardVectors"/> in the player and prints one
    /// <c>[selftest] PASS|FAIL …</c> line with the digest and the first differing case/field.
    /// <list type="bullet">
    /// <item>Linux / Mac server or probe builds: <c>-fallrushSelfTest</c> (runs, prints, quits with exit code 0 = pass, 4 = fail).</item>
    /// <item>WebGL: <c>?selftest=1</c> (prints to the browser console, then the game continues).</item>
    /// </list>
    /// </summary>
    public static class SelfTest
    {
        public const string Arg = "-fallrushSelfTest";
        public const string QueryKey = "selftest";

        public static bool Requested()
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, Arg, StringComparison.OrdinalIgnoreCase))
                    return true;
            return NetTargets.GetQueryParam(Application.absoluteURL, QueryKey) == "1";
        }

        public static bool Run(out string line)
        {
            var platform = $"{Application.platform}/{SystemInfo.processorType.Replace(' ', '_')}/{(IntPtr.Size * 8)}bit";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ok = HazardVectors.SelfTest(HazardVectorsExpected.Data, platform, out line);
            line += $" ms={sw.ElapsedMilliseconds}";
            Debug.Log(line);
            return ok;
        }
    }
}
