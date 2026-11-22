using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.Networking;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-A parity page: <c>?rcselftest=1</c> (WebGL) or <c>-rcselftest</c> (desktop) runs the 200-case vector subset
    /// (40 per golden file, <c>StreamingAssets/rcvectors/subset_expected.txt</c> written by tools/p3/rc-subset.mjs from
    /// P2's core) on this platform's <see cref="IRushCore"/> and prints <c>[rcselftest] PASS|FAIL</c> lines with the
    /// digests to the console and on screen. Dev check only; nothing else changes.
    /// </summary>
    public sealed class RcSelfTest : MonoBehaviour
    {
        private string _report = "[rcselftest] loading rush-core …";
        private bool _pass;

        public static bool Requested =>
            NetTargets.GetQueryParam(Application.absoluteURL, "rcselftest") == "1" || Array.IndexOf(Environment.GetCommandLineArgs(), "-rcselftest") >= 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!Requested)
                return;
            var go = new GameObject("RcSelfTest");
            DontDestroyOnLoad(go);
            go.AddComponent<RcSelfTest>();
        }

        private IEnumerator Start()
        {
            var t0 = Time.realtimeSinceStartup;
            var core = RushCoreAbi.Create();
            while (!core.Ready && core.Error == null && Time.realtimeSinceStartup - t0 < 20f)
                yield return null;
            if (!core.Ready)
            {
                _report = "[rcselftest] FAIL: " + (core.Error ?? "core not ready in 20 s");
                Debug.LogError(_report);
                yield break;
            }

            var files = new Dictionary<string, byte[]>();
            string expected = null;
            var names = new List<string> { "subset_expected.txt" };
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                using var req = UnityWebRequest.Get(Application.streamingAssetsPath + "/rcvectors/" + name);
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _report = $"[rcselftest] FAIL: {name}: {req.error}";
                    Debug.LogError(_report);
                    yield break;
                }

                if (i > 0)
                {
                    files[name] = req.downloadHandler.data;
                    continue;
                }

                expected = req.downloadHandler.text;
                foreach (var l in expected.Split('\n'))
                {
                    if (l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        continue;
                    var p = l.Trim().Split(' ');
                    if (p.Length > 5 && p[5] != "-" && !names.Contains(p[5]))
                        names.Add(p[5]);
                }
            }

            var t1 = Time.realtimeSinceStartup;
            var n = Run(core, k => files[k], expected, out var rep);
            _pass = n == 5 && !rep.Contains("FAIL");
            _report = $"[rcselftest] {(_pass ? "PASS" : "FAIL")} {n}/5 files, 200 cases, core 0x{core.Version:x}, {(Time.realtimeSinceStartup - t1) * 1000f:F0} ms\n{rep}";
            Debug.Log(_report);
        }

        /// <summary>Runs every subset line (<c>file kind seed n digest map</c>; <c>map</c> = the vector map file or
        /// <c>-</c>); returns the number that match. <paramref name="read"/> returns a file's bytes by name.</summary>
        public static int Run(IRushCore core, Func<string, byte[]> read, string expected, out string report)
        {
            var sb = new StringBuilder();
            var ok = 0;
            foreach (var raw in expected.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var p = line.Split(' ');
                var map = p.Length > 5 && p[5] != "-" ? read(p[5]) : null;
                var got = core.Vectors(uint.Parse(p[1]), ulong.Parse(p[2]), uint.Parse(p[3]), map).ToString("x16");
                var pass = got == p[4];
                if (pass)
                    ok++;
                sb.Append($"[rcselftest] {p[0]} {p[3]} cases {got} {(pass ? "PASS" : "FAIL want " + p[4])}\n");
            }

            report = sb.ToString();
            return ok;
        }

        private void OnGUI()
        {
            GUI.color = _pass ? Color.green : Color.yellow;
            GUI.Label(new Rect(12, 12, 900, 300), _report);
        }
    }
}
